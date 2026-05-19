using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Read;

namespace Moongazing.OrionAudit.Tests;

public class SnapshotPolicyReplayTests
{
    [Auditable]
    public sealed class Counter
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Value { get; set; }
        public string Tag { get; set; } = "";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Counter> Counters => Set<Counter>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<SnapshotCursor> Cursors => Set<SnapshotCursor>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Counter>().HasKey(c => c.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private static TestContext NewWith(int snapshotEvery)
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Counter>().SnapshotEvery(snapshotEvery));
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));
        return services.BuildServiceProvider().GetRequiredService<TestContext>();
    }

    [Fact]
    public async Task Replay_UsesLatestSnapshot_AndAppliesOnlyDiffsAfterIt()
    {
        await using var ctx = NewWith(snapshotEvery: 10);
        var c = new Counter { Value = 0, Tag = "init" };
        ctx.Counters.Add(c);
        await ctx.SaveChangesAsync();

        // 25 updates with SnapshotEvery(10) → snapshots at updates 10 and 20.
        for (var i = 1; i <= 25; i++)
        {
            c.Value = i;
            await ctx.SaveChangesAsync();
        }

        var rebuilt = await new AuditReconstructor(ctx)
            .ReconstructAsync<Counter>(c.Id.ToString(), DateTime.UtcNow.AddMinutes(1));

        Assert.NotNull(rebuilt);
        Assert.Equal(25, rebuilt!.Value);
        Assert.Equal("init", rebuilt.Tag);
    }

    [Fact]
    public async Task Replay_WithoutPolicy_StillWorksFromInsert()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Counter>());   // no SnapshotEvery
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));
        await using var ctx = services.BuildServiceProvider().GetRequiredService<TestContext>();

        var c = new Counter { Value = 0 };
        ctx.Counters.Add(c);
        await ctx.SaveChangesAsync();
        for (var i = 1; i <= 5; i++)
        {
            c.Value = i;
            await ctx.SaveChangesAsync();
        }

        var rebuilt = await new AuditReconstructor(ctx)
            .ReconstructAsync<Counter>(c.Id.ToString(), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(5, rebuilt!.Value);
    }

    [Fact]
    public async Task Replay_AsOfBetweenTwoSnapshots_PicksTheEarlierOne()
    {
        await using var ctx = NewWith(snapshotEvery: 3);
        var c = new Counter { Value = 0 };
        ctx.Counters.Add(c);
        await ctx.SaveChangesAsync();
        // Update 1, 2, 3 (snapshot), 4, 5, 6 (snapshot), 7, 8
        for (var i = 1; i <= 8; i++)
        {
            c.Value = i;
            await ctx.SaveChangesAsync();
        }

        // asOf right after update #5 — should pick the snapshot at #3 then replay #4, #5.
        var rows = await ctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        var asOfAfter5 = rows[5].OccurredOnUtc;   // Insert + 1..5 == index 5 in 0-based ordered list

        var rebuilt = await new AuditReconstructor(ctx)
            .ReconstructAsync<Counter>(c.Id.ToString(), asOfAfter5);
        Assert.NotNull(rebuilt);
        Assert.Equal(5, rebuilt!.Value);
    }
}
