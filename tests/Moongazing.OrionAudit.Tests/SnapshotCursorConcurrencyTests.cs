namespace Moongazing.OrionAudit.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Xunit;

/// <summary>
/// Pins the documented concurrency contract of the <see cref="SnapshotCursor"/> counter: a group of
/// concurrent saves for one entity advances it by one instead of one-per-save, which stretches the
/// <c>SnapshotEvery(n)</c> cadence and nothing else. The counter is deliberately unguarded — it
/// drives a replay-cost optimisation, so it must never turn an audit-side collision into a failed
/// business save the way a concurrency token on the cursor would.
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
    public async Task Concurrent_saves_on_one_entity_stretch_the_cadence_and_never_fail_the_save()
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

        // Exactly one increment lost per overlapping group: the counter moved forward, did not
        // double-count, and did not reset. Three updates have happened; the cursor says two.
        Assert.Equal(2, await CursorCountAsync());
        Assert.Equal(3, (await UpdateSnapshotsAsync()).Count);
        Assert.All(await UpdateSnapshotsAsync(), Assert.Null);

        // ...and the cadence self-heals rather than stalling. SnapshotEvery(3) now lands on the
        // fourth update instead of the third - late by exactly the one lost increment, never
        // skipped. That is the documented n..n*k bound with n=3, k=2.
        clock.Advance(TimeSpan.FromSeconds(1));
        await using (var fourth = NewContext())
        {
            var entity = await fourth.Counters.SingleAsync(c => c.Id == id);
            entity.Value = 4;
            await fourth.SaveChangesAsync();
        }

        var snapshots = await UpdateSnapshotsAsync();
        Assert.Equal(4, snapshots.Count);
        Assert.Null(snapshots[2]);
        Assert.NotNull(snapshots[3]);
        Assert.Equal(0, await CursorCountAsync());   // reset by the snapshot it finally took
    }
}
