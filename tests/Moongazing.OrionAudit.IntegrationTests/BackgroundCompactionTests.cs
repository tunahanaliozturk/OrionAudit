using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Integrity;
using Moongazing.OrionAudit.Read;
using Moongazing.OrionAudit.Store;

namespace Moongazing.OrionAudit.IntegrationTests;

/// <summary>
/// End-to-end coverage of the background compaction hosted service over an in-memory SQLite database,
/// driven through its public <see cref="AuditCompactionHostedService{TDbContext}.SweepOnceAsync"/> so
/// the test controls exactly when a cycle runs (no timer flakiness). Ids and timestamps are pinned via
/// a frozen clock and an explicit per-save advance so ordering never depends on random Guid tie-breaks.
/// </summary>
public class BackgroundCompactionTests
{
    private const string KeyId1Base64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Auditable]
    public sealed class Widget
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Value { get; set; }
    }

    private sealed class CompactionDb : DbContext
    {
        public DbSet<Widget> Widgets => Set<Widget>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditChainAnchor> Anchors => Set<AuditChainAnchor>();
        public CompactionDb(DbContextOptions<CompactionDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>().HasKey(w => w.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private sealed class FrozenClock : TimeProvider
    {
        private DateTimeOffset now;
        public FrozenClock(DateTimeOffset start) => now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now = now.Add(by);
    }

    private static async Task<(ServiceProvider sp, SqliteConnection conn, FrozenClock clock)> BuildAsync(
        Action<OrionAuditOptions> configure)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var clock = new FrozenClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLogging();
        services.AddOrionAudit<CompactionDb>(configure);
        services.AddSingleton(connection);
        services.AddDbContext<CompactionDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<CompactionDb>().Database.EnsureCreatedAsync();
        }
        return (sp, connection, clock);
    }

    // Inserts one widget then applies `updates` updates, each in its own SaveChanges with the clock
    // advanced one minute so OccurredOnUtc strictly increases. Returns the widget id.
    private static async Task<Guid> SeedHistoryAsync(ServiceProvider sp, FrozenClock clock, int updates)
    {
        Guid id;
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<CompactionDb>();
            var w = new Widget { Value = 0 };
            ctx.Widgets.Add(w);
            await ctx.SaveChangesAsync();
            id = w.Id;
        }
        for (var i = 1; i <= updates; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await using var scope = sp.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<CompactionDb>();
            var w = await ctx.Widgets.FirstAsync(x => x.Id == id);
            w.Value = i;
            await ctx.SaveChangesAsync();
        }
        return id;
    }

    [Fact]
    public async Task BackgroundSweep_CompactsLongHistory_AndLeavesItReconstructable()
    {
        var (sp, conn, clock) = await BuildAsync(o =>
        {
            o.Audit<Widget>();
            o.CompactInBackground(c =>
            {
                c.RetainTail = 3;
                c.MinRowsBeforeCompaction = 5; // RetainTail + 2
                c.MaxStreamsPerSweep = 10;
            });
        });
        await using var _conn = conn;
        await using var _sp = sp;

        // 1 Inserted + 9 Updated = 10 rows.
        var id = await SeedHistoryAsync(sp, clock, updates: 9);

        int before;
        await using (var scope = sp.CreateAsyncScope())
        {
            before = await scope.ServiceProvider.GetRequiredService<CompactionDb>()
                .AuditLogs.Where(a => a.EntityId == id.ToString()).CountAsync();
        }
        Assert.Equal(10, before);

        var sweep = ActivatorUtilities.CreateInstance<AuditCompactionHostedService<CompactionDb>>(sp);
        var folded = await sweep.SweepOnceAsync();
        Assert.True(folded > 0, "expected the sweep to fold at least one row");

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<CompactionDb>();
            var rows = await ctx.AuditLogs
                .Where(a => a.EntityId == id.ToString())
                .OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id)
                .ToListAsync();

            // 10 rows, RetainTail 3 -> fold 7 rows into 1 in-place snapshot + 3 tail = 4 rows survive.
            // RowsRemoved counts all 7 folded rows (the boundary is folded too, then kept in place as the
            // snapshot), so the reported folded total is 7 while 6 rows are physically deleted.
            Assert.Equal(4, rows.Count);
            Assert.Equal(7, folded);
            Assert.Equal(AuditAction.Inserted, rows[0].Action);
            Assert.NotNull(rows[0].Snapshot);

            // Reconstruct the latest state through the production reconstructor: still resolves to 9.
            var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();
            var latest = await reconstructor.ReconstructAsync<Widget>(
                id.ToString(), clock.GetUtcNow().UtcDateTime);
            Assert.NotNull(latest);
            Assert.Equal(9, latest!.Value);
        }
    }

    [Fact]
    public async Task BackgroundSweep_ShortHistory_IsNoOp()
    {
        var (sp, conn, clock) = await BuildAsync(o =>
        {
            o.Audit<Widget>();
            o.CompactInBackground(c =>
            {
                c.RetainTail = 3;
                c.MinRowsBeforeCompaction = 50;
                c.MaxStreamsPerSweep = 10;
            });
        });
        await using var _conn = conn;
        await using var _sp = sp;

        var id = await SeedHistoryAsync(sp, clock, updates: 4); // 5 rows, below the threshold of 50

        var sweep = ActivatorUtilities.CreateInstance<AuditCompactionHostedService<CompactionDb>>(sp);
        var folded = await sweep.SweepOnceAsync();
        Assert.Equal(0, folded);

        await using var scope = sp.CreateAsyncScope();
        var remaining = await scope.ServiceProvider.GetRequiredService<CompactionDb>()
            .AuditLogs.Where(a => a.EntityId == id.ToString()).CountAsync();
        Assert.Equal(5, remaining);
    }

    [Fact]
    public async Task BackgroundSweep_WithHashChain_SkipsChainedStreams_AndChainStillVerifies()
    {
        var (sp, conn, clock) = await BuildAsync(o =>
        {
            o.Audit<Widget>();
            o.UseHashChain(h => h.UseKey(1, KeyId1Base64));
            o.CompactInBackground(c =>
            {
                c.RetainTail = 3;
                c.MinRowsBeforeCompaction = 5;
                c.MaxStreamsPerSweep = 10;
            });
        });
        await using var _conn = conn;
        await using var _sp = sp;

        var id = await SeedHistoryAsync(sp, clock, updates: 9); // 10 chained rows

        int before;
        await using (var scope = sp.CreateAsyncScope())
        {
            before = await scope.ServiceProvider.GetRequiredService<CompactionDb>()
                .AuditLogs.Where(a => a.EntityId == id.ToString()).CountAsync();
        }
        Assert.Equal(10, before);

        var sweep = ActivatorUtilities.CreateInstance<AuditCompactionHostedService<CompactionDb>>(sp);
        var folded = await sweep.SweepOnceAsync();

        // The single chained stream is skipped, so nothing is folded.
        Assert.Equal(0, folded);

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<CompactionDb>();
            var after = await ctx.AuditLogs.Where(a => a.EntityId == id.ToString()).CountAsync();
            Assert.Equal(10, after); // untouched

            var verifier = scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
            var result = await verifier.VerifyChainAsync(
                AuditChainVerificationRequest.ForEntity(typeof(Widget).AssemblyQualifiedName!, id.ToString()));
            Assert.True(result.IsValid);
            Assert.Equal(10, result.VerifiedRowCount);
        }
    }

    [Fact]
    public async Task BackgroundSweep_RespectsMaxStreamsPerSweep()
    {
        var (sp, conn, clock) = await BuildAsync(o =>
        {
            o.Audit<Widget>();
            o.CompactInBackground(c =>
            {
                c.RetainTail = 2;
                c.MinRowsBeforeCompaction = 4; // RetainTail + 2
                c.MaxStreamsPerSweep = 1;       // only one stream folded per cycle
            });
        });
        await using var _conn = conn;
        await using var _sp = sp;

        // Two independent entities, each with a long-enough history to be a candidate.
        var idA = await SeedHistoryAsync(sp, clock, updates: 9);
        var idB = await SeedHistoryAsync(sp, clock, updates: 9);

        var sweep = ActivatorUtilities.CreateInstance<AuditCompactionHostedService<CompactionDb>>(sp);
        await sweep.SweepOnceAsync();

        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CompactionDb>();
        var countA = await ctx.AuditLogs.Where(a => a.EntityId == idA.ToString()).CountAsync();
        var countB = await ctx.AuditLogs.Where(a => a.EntityId == idB.ToString()).CountAsync();

        // Exactly one of the two streams was folded this cycle (10 -> 3); the other is untouched (10).
        var compactedCount = new[] { countA, countB }.Count(c => c == 3);
        var untouchedCount = new[] { countA, countB }.Count(c => c == 10);
        Assert.Equal(1, compactedCount);
        Assert.Equal(1, untouchedCount);

        // A second cycle folds the remaining stream.
        await sweep.SweepOnceAsync();
        await using var scope2 = sp.CreateAsyncScope();
        var ctx2 = scope2.ServiceProvider.GetRequiredService<CompactionDb>();
        Assert.Equal(3, await ctx2.AuditLogs.Where(a => a.EntityId == idA.ToString()).CountAsync());
        Assert.Equal(3, await ctx2.AuditLogs.Where(a => a.EntityId == idB.ToString()).CountAsync());
    }
}
