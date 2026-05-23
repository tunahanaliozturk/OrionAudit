using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Bench;

/// <summary>
/// Dispatcher throughput: how fast a queued batch is turned into AuditLog rows. The queue is
/// re-seeded per iteration so each measured FlushPendingAsync drains a known row count.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class DispatcherBench
{
    [Auditable]
    public sealed class Row
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public decimal Amount { get; set; }
    }

    [Params(100, 1000)]
    public int QueuedRows { get; set; }

    private SqliteConnection conn = null!;
    private ServiceProvider sp = null!;

    public sealed class AuditDb : DbContext
    {
        public DbSet<Row> Rows => Set<Row>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditCaptureQueueEntry> Queue => Set<AuditCaptureQueueEntry>();
        public AuditDb(DbContextOptions<AuditDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Row>().HasKey(r => r.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    [GlobalSetup]
    public async Task Setup()
    {
        conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<AuditDb>(o => o.Audit<Row>().UseAsyncCapture(q => q.BatchSize(QueuedRows)));
        services.AddSingleton(conn);
        services.AddDbContext<AuditDb>((s, o) =>
            o.UseSqlite(s.GetRequiredService<SqliteConnection>()).UseOrionAudit(s));
        sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuditDb>().Database.EnsureCreatedAsync();
    }

    [IterationSetup]
    public void SeedQueue()
    {
        using var scope = sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDb>();
        for (var i = 0; i < QueuedRows; i++)
        {
            ctx.Rows.Add(new Row { Name = $"r{i}", Amount = i });
        }
        ctx.SaveChanges();   // async mode → writes QueuedRows queue rows
    }

    [Benchmark]
    public async Task<int> FlushPending()
        => await sp.GetRequiredService<IAuditDispatcher>().FlushPendingAsync();

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await sp.DisposeAsync();
        await conn.DisposeAsync();
    }
}
