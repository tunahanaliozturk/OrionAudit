using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.Bench;

/// <summary>
/// End-to-end SaveChanges cost with and without OrionAudit capturing. The delta is the
/// per-entity overhead that consumers pay in production.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class InterceptorBench
{
    [Auditable]
    public sealed class Row
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public decimal Amount { get; set; }
    }

    [Params(1, 10, 100)]
    public int BatchSize { get; set; }

    private SqliteConnection auditConn = null!;
    private ServiceProvider auditSp = null!;

    private SqliteConnection plainConn = null!;
    private ServiceProvider plainSp = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        // Audit-enabled context
        auditConn = new SqliteConnection("DataSource=:memory:");
        await auditConn.OpenAsync();
        var auditServices = new ServiceCollection();
        auditServices.AddOrionAudit<AuditDb>(o => o.Audit<Row>());
        auditServices.AddSingleton(auditConn);
        auditServices.AddDbContext<AuditDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        auditSp = auditServices.BuildServiceProvider();
        await using (var scope = auditSp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuditDb>().Database.EnsureCreatedAsync();
        }

        // Plain context, no audit
        plainConn = new SqliteConnection("DataSource=:memory:");
        await plainConn.OpenAsync();
        var plainServices = new ServiceCollection();
        plainServices.AddSingleton(plainConn);
        plainServices.AddDbContext<PlainDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()));
        plainSp = plainServices.BuildServiceProvider();
        await using (var scope = plainSp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PlainDb>().Database.EnsureCreatedAsync();
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await auditSp.DisposeAsync();
        await auditConn.DisposeAsync();
        await plainSp.DisposeAsync();
        await plainConn.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> SaveChanges_NoAudit()
    {
        await using var scope = plainSp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<PlainDb>();
        for (var i = 0; i < BatchSize; i++)
        {
            ctx.Rows.Add(new Row { Name = $"r{i}", Amount = i });
        }
        return await ctx.SaveChangesAsync();
    }

    [Benchmark]
    public async Task<int> SaveChanges_WithAudit()
    {
        await using var scope = auditSp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDb>();
        for (var i = 0; i < BatchSize; i++)
        {
            ctx.Rows.Add(new Row { Name = $"r{i}", Amount = i });
        }
        return await ctx.SaveChangesAsync();
    }

    public sealed class AuditDb : DbContext
    {
        public DbSet<Row> Rows => Set<Row>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public AuditDb(DbContextOptions<AuditDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Row>().HasKey(r => r.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    public sealed class PlainDb : DbContext
    {
        public DbSet<Row> Rows => Set<Row>();
        public PlainDb(DbContextOptions<PlainDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Row>().HasKey(r => r.Id);
        }
    }
}
