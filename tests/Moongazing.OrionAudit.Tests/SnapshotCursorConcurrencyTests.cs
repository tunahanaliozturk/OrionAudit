namespace Moongazing.OrionAudit.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Xunit;

/// <summary>
/// Pins what survives a <see cref="SnapshotCursor"/> collision: both saves commit, every audited
/// update still gets its audit row, and the counter stays in range. The counter is deliberately
/// unguarded — it drives a replay-cost optimisation, so it must never turn an audit-side collision
/// into a failed business save the way a concurrency token on the cursor would.
/// <para>
/// Deliberately absent: any assertion that snapshots fall a particular distance apart.
/// <c>SnapshotEvery(n)</c> guarantees no interval under concurrent writes to one entity — a
/// collision can snapshot early, and a stale overwrite can lower the counter and defer one without
/// bound. The counts below follow from the fixed interleave this test constructs, not from a
/// cadence property; see the remarks on <c>SnapshotPolicyEvaluator</c>.
/// </para>
/// </summary>
public sealed class SnapshotCursorConcurrencyTests : IAsyncLifetime
{
    [Auditable]
    public sealed class Counter
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Value { get; set; }
    }

    private sealed class CursorDbContext : DbContext
    {
        public CursorDbContext(DbContextOptions<CursorDbContext> options) : base(options) { }
        public DbSet<Counter> Counters => Set<Counter>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<SnapshotCursor> Cursors => Set<SnapshotCursor>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Counter>().HasKey(c => c.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private SqliteConnection connection = default!;
    private ServiceProvider services = default!;
    private MutableTimeProvider clock = default!;

    public async ValueTask InitializeAsync()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var collection = new ServiceCollection();
        // AddOrionAudit registers TimeProvider.System with TryAddSingleton, so a clock registered
        // first wins. Stepping it by hand keeps OccurredOnUtc strictly increasing without sleeping.
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddOrionAudit<CursorDbContext>(o =>
        {
            o.Audit<Counter>();
            o.SnapshotEvery(3);
        });
        collection.AddDbContext<CursorDbContext>((sp, o) =>
            o.UseSqlite(connection).UseOrionAudit(sp));
        services = collection.BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CursorDbContext>().Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        await connection.DisposeAsync();
    }

    private CursorDbContext NewContext()
        => services.CreateScope().ServiceProvider.GetRequiredService<CursorDbContext>();

    private async Task<int?> CursorCountAsync()
    {
        await using var verify = NewContext();
        return (await verify.Cursors.SingleOrDefaultAsync())?.UpdatesSinceLast;
    }

    private async Task<List<string?>> UpdateSnapshotsAsync()
    {
        await using var verify = NewContext();
        return await verify.AuditLogs
            .Where(a => a.Action == AuditAction.Updated)
            .OrderBy(a => a.OccurredOnUtc)
            .Select(a => a.Snapshot)
            .ToListAsync();
    }

    [Fact]
    public async Task Concurrent_saves_on_one_entity_never_fail_the_save_and_never_cost_an_audit_row()
    {
        var id = Guid.NewGuid();

        await using (var seed = NewContext())
        {
            seed.Counters.Add(new Counter { Id = id, Value = 0 });
            await seed.SaveChangesAsync();
            clock.Advance(TimeSpan.FromSeconds(1));
            var seeded = await seed.Counters.SingleAsync();
            seeded.Value = 1;
            await seed.SaveChangesAsync();   // update #1 -> cursor 1
        }

        Assert.Equal(1, await CursorCountAsync());

        // Model the collision deterministically, with no threads: both contexts load the cursor
        // BEFORE either saves, so the interceptor's lookup resolves from each change tracker and
        // both saves evaluate from the same count - exactly the read-both-then-write-both window
        // two concurrent SaveChanges calls open in production.
        await using var first = NewContext();
        await using var second = NewContext();
        var trackedFirst = await first.Cursors.SingleAsync();
        var trackedSecond = await second.Cursors.SingleAsync();
        Assert.Equal(1, trackedFirst.UpdatesSinceLast);
        Assert.Equal(1, trackedSecond.UpdatesSinceLast);

        var entityFirst = await first.Counters.SingleAsync(c => c.Id == id);
        var entitySecond = await second.Counters.SingleAsync(c => c.Id == id);
        entityFirst.Value = 2;
        entitySecond.Value = 3;

        clock.Advance(TimeSpan.FromSeconds(1));
        await first.SaveChangesAsync();    // update #2 -> cursor 1 -> 2
        clock.Advance(TimeSpan.FromSeconds(1));
        // The heart of it: the loser of the race must still commit. A concurrency token on the
        // cursor would raise DbUpdateConcurrencyException here and take a business transaction
        // down with it, to protect a counter that only decides when to cache a snapshot.
        await second.SaveChangesAsync();   // update #3 -> also 1 -> 2, one increment lost

        // Every audited update produced its row, whatever the counter did. This is the part that
        // is guaranteed in general, not just in this interleave.
        var afterCollision = await UpdateSnapshotsAsync();
        Assert.Equal(3, afterCollision.Count);

        // The counter stays in range: never negative, never persisted at or above n. That also
        // holds in general - the value is only ever written as some reader's value + 1, or reset
        // to 0 by a snapshot.
        var counted = await CursorCountAsync();
        Assert.InRange(counted!.Value, 0, 2);

        // This specific count follows from the interleave constructed above and NOT from any
        // general cadence guarantee: both savers read 1, so both wrote 2 and one increment was
        // dropped. Under real concurrency the count can also go backwards (a stale writer
        // overwriting a higher committed value), so nothing here promises an interval - see the
        // remarks on SnapshotPolicyEvaluator.
        Assert.Equal(2, counted.Value);
        Assert.All(afterCollision, Assert.Null);

        // With the contention over, the next uncontended save reads what the last one wrote and
        // the cadence resumes. Again: a property of this serialized tail, not a promise that a
        // deferred snapshot always arrives.
        clock.Advance(TimeSpan.FromSeconds(1));
        await using (var fourth = NewContext())
        {
            var entity = await fourth.Counters.SingleAsync(c => c.Id == id);
            entity.Value = 4;
            await fourth.SaveChangesAsync();
        }

        var snapshots = await UpdateSnapshotsAsync();
        Assert.Equal(4, snapshots.Count);
        Assert.NotNull(snapshots[3]);
        Assert.Equal(0, await CursorCountAsync());   // reset by the snapshot it took
    }
}
