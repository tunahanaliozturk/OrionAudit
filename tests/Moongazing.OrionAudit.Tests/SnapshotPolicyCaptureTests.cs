using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.Tests;

public class SnapshotPolicyCaptureTests
{
    [Auditable]
    public sealed class Counter
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Value { get; set; }
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

    private static TestContext NewWithPolicy(Action<OrionAuditOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o =>
        {
            o.Audit<Counter>();
            configure(o);
        });
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));
        return services.BuildServiceProvider().GetRequiredService<TestContext>();
    }

    [Fact]
    public async Task NeverPolicy_NoSnapshotsOnUpdate()
    {
        await using var ctx = NewWithPolicy(_ => { });   // default = Never
        var c = new Counter { Value = 0 };
        ctx.Counters.Add(c);
        await ctx.SaveChangesAsync();
        c.Value = 1; await ctx.SaveChangesAsync();
        c.Value = 2; await ctx.SaveChangesAsync();

        var rows = await ctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.All(rows.Where(r => r.Action == AuditAction.Updated), r => Assert.Null(r.Snapshot));
        Assert.Empty(await ctx.Cursors.ToListAsync());
    }

    [Fact]
    public async Task SnapshotEvery3_WritesSnapshotOnEveryThirdUpdate()
    {
        await using var ctx = NewWithPolicy(o => o.SnapshotEvery(3));
        var c = new Counter { Value = 0 };
        ctx.Counters.Add(c);
        await ctx.SaveChangesAsync();
        for (var i = 1; i <= 7; i++)
        {
            c.Value = i;
            await ctx.SaveChangesAsync();
        }

        var updateRows = await ctx.AuditLogs
            .Where(a => a.Action == AuditAction.Updated)
            .OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.Equal(7, updateRows.Count);
        // Updates 3 and 6 should carry snapshots; the rest should not.
        var withSnapshot = updateRows.Select((r, i) => (i, r)).Where(t => t.r.Snapshot is not null).Select(t => t.i).ToArray();
        Assert.Equal(new[] { 2, 5 }, withSnapshot);

        var cursor = Assert.Single(await ctx.Cursors.ToListAsync());
        Assert.Equal(1, cursor.UpdatesSinceLast);   // 1 update since the last (6th) snapshot
        Assert.NotNull(cursor.LastSnapshotUtc);
    }

    [Fact]
    public async Task SnapshotEveryDuration_WritesOnFirstThenAfterElapsed()
    {
        await using var ctx = NewWithPolicy(o => o.SnapshotEvery(TimeSpan.FromMilliseconds(100)));
        var c = new Counter { Value = 0 };
        ctx.Counters.Add(c);
        await ctx.SaveChangesAsync();

        c.Value = 1; await ctx.SaveChangesAsync();   // first Update — LastSnapshotUtc=null → snapshot
        c.Value = 2; await ctx.SaveChangesAsync();   // immediately after — should not snapshot

        var updates = await ctx.AuditLogs
            .Where(a => a.Action == AuditAction.Updated)
            .OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.NotNull(updates[0].Snapshot);
        Assert.Null(updates[1].Snapshot);

        await Task.Delay(120);
        c.Value = 3; await ctx.SaveChangesAsync();   // after the elapsed window → snapshot again

        var refreshed = await ctx.AuditLogs
            .Where(a => a.Action == AuditAction.Updated)
            .OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.NotNull(refreshed[2].Snapshot);
    }

    [Fact]
    public async Task SnapshotPolicy_DoesNotInterfereWithInsertOrDelete()
    {
        await using var ctx = NewWithPolicy(o => o.SnapshotEvery(1));
        var c = new Counter { Value = 0 };
        ctx.Counters.Add(c);
        await ctx.SaveChangesAsync();

        ctx.Counters.Remove(c);
        await ctx.SaveChangesAsync();

        var rows = await ctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.Equal(AuditAction.Inserted, rows[0].Action);
        Assert.Null(rows[0].Snapshot);   // Insert doesn't get the periodic snapshot
        Assert.Equal(AuditAction.Deleted, rows[1].Action);
        Assert.NotNull(rows[1].Snapshot);   // Delete still snapshots the last-known state via the original path
    }
}
