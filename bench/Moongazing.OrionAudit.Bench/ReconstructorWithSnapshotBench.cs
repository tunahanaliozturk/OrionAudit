using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Read;

namespace Moongazing.OrionAudit.Bench;

/// <summary>
/// Same setup as <see cref="ReconstructorBench"/> but with <c>SnapshotEvery(50)</c> enabled.
/// Side-by-side these two benches show the v0.2 snapshotting win: reconstruction over a
/// deep history goes from O(N) to O(K) where K is the number of updates since the latest
/// snapshot (≤ 50 for these params).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class ReconstructorWithSnapshotBench
{
    [Auditable]
    public sealed class Row
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Status { get; set; } = "";
        public int Counter { get; set; }
    }

    [Params(100, 1000)]
    public int HistoryDepth { get; set; }

    private SqliteConnection connection = null!;
    private ServiceProvider sp = null!;
    private string entityId = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<BenchDb>(o => o.Audit<Row>().SnapshotEvery(50));
        services.AddSingleton(connection);
        services.AddDbContext<BenchDb>((p, o) =>
            o.UseSqlite(p.GetRequiredService<SqliteConnection>()).UseOrionAudit(p));
        sp = services.BuildServiceProvider();

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<BenchDb>();
            await ctx.Database.EnsureCreatedAsync();
            var row = new Row { Status = "init", Counter = 0 };
            ctx.Rows.Add(row);
            await ctx.SaveChangesAsync();
            entityId = row.Id.ToString();

            for (var i = 1; i <= HistoryDepth; i++)
            {
                row.Status = $"step-{i}";
                row.Counter = i;
                await ctx.SaveChangesAsync();
            }
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await sp.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Benchmark]
    public async Task<Row?> Reconstruct_AtLatest_WithSnapshotEvery50()
    {
        await using var scope = sp.CreateAsyncScope();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();
        return await reconstructor.ReconstructAsync<Row>(entityId, DateTime.UtcNow.AddMinutes(1));
    }

    public sealed class BenchDb : DbContext
    {
        public DbSet<Row> Rows => Set<Row>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public BenchDb(DbContextOptions<BenchDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Row>().HasKey(r => r.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }
}
