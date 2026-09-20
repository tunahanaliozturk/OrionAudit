using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.IntegrationTests;

/// <summary>
/// What happens when a stream's timestamp order and its chain order disagree.
/// </summary>
/// <remarks>
/// <para>
/// <c>OccurredOnUtc</c> is stamped near the start of capture; the chain's order is decided much
/// later, by which writer wins the anchor lock. Two concurrent same-stream writers can therefore
/// invert: the one that read the earlier clock value can lose the race and land <em>second</em> in
/// the chain. Nothing prevents it, and nothing about it is exotic - it is two requests touching one
/// entity at the same moment.
/// </para>
/// <para>
/// These tests construct that inversion deterministically rather than racing for it: a
/// <see cref="TimeProvider"/> hands the first caller the earlier timestamp and holds it there while
/// the second caller takes the later one, wins the lock and commits. Each test asserts the inversion
/// actually happened before asserting anything about it, so a run where the construction failed to
/// interleave fails loudly instead of passing for the wrong reason.
/// </para>
/// </remarks>
public class HashChainOrderInversionTests
{
    private const string KeyId1Base64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Auditable]
    public sealed class Meter
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Reading { get; set; }
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Meter> Meters => Set<Meter>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditChainAnchor> Anchors => Set<AuditChainAnchor>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Meter>().HasKey(m => m.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    /// <summary>
    /// Hands the first armed caller the EARLIER timestamp and parks it there, so the second caller
    /// takes the later timestamp, wins the anchor lock and commits first. The parked writer then
    /// chains onto the other one while carrying the earlier <c>OccurredOnUtc</c>.
    /// </summary>
    private sealed class InvertingClock(DateTimeOffset start) : TimeProvider
    {
        private static readonly TimeSpan ParkFor = TimeSpan.FromMilliseconds(500);
        private int armed;
        private int calls;

        public void Arm() => Interlocked.Exchange(ref armed, 1);

        public override DateTimeOffset GetUtcNow()
        {
            if (Volatile.Read(ref armed) == 0)
            {
                return start;
            }

            if (Interlocked.Increment(ref calls) != 1)
            {
                return start.AddSeconds(1);     // the writer that will win the lock
            }

            // The timestamp is already decided here - capture reads the clock long before the chain
            // writer takes the anchor lock, which is the whole point. Parking after deciding it, and
            // before the stamp, is exactly the window the race opens in.
            Thread.Sleep(ParkFor);
            return start;
        }
    }

    [Fact]
    public async Task ConcurrentWritersWithDistinctTimestamps_InvertedLockOrder_StillVerifies()
    {
        using var db = new TempSqliteDatabase();
        var clock = new InvertingClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddOrionAudit<TestContext>(o =>
        {
            o.Audit<Meter>();
            o.UseHashChain(h => h.UseKey(1, KeyId1Base64));
        });
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(db.ConnectionString).UseOrionAudit(sp), ServiceLifetime.Scoped);
        await using var provider = services.BuildServiceProvider();

        Guid meterId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            await ctx.Database.EnsureCreatedAsync();
            var meter = new Meter { Reading = 0 };
            ctx.Meters.Add(meter);
            await ctx.SaveChangesAsync();   // genesis; the clock is not armed yet
            meterId = meter.Id;
        }

        clock.Arm();

        async Task UpdateAsync(int reading)
        {
            await using var scope = provider.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var meter = await ctx.Meters.FirstAsync(m => m.Id == meterId);
            meter.Reading = reading;
            await ctx.SaveChangesAsync();
        }

        // Task.Run: SQLite's async methods complete synchronously, so a bare call would run the
        // whole first save before the second started and nothing would interleave.
        await Task.WhenAll(Task.Run(() => UpdateAsync(10)), Task.Run(() => UpdateAsync(20)));

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var updates = await ctx.AuditLogs.AsNoTracking()
                .Where(a => a.EntityId == meterId.ToString() && a.Action == AuditAction.Updated)
                .OrderBy(a => a.ChainSequence)
                .ToListAsync();

            // Precondition: the construction really did construct the inversion. The row written SECOND
            // into the chain must be the one carrying the EARLIER timestamp.
            Assert.Equal(2, updates.Count);
            Assert.True(
                updates[1].OccurredOnUtc < updates[0].OccurredOnUtc,
                $"inversion was not constructed: chain order {updates[0].ChainSequence}@{updates[0].OccurredOnUtc:O}"
                + $" then {updates[1].ChainSequence}@{updates[1].OccurredOnUtc:O}");

            // And the chain itself is intact: each row links to the one actually before it.
            Assert.Equal(updates[0].EntryHash, updates[1].PreviousHash);
            var anchor = await ctx.Anchors.AsNoTracking().SingleAsync(a => a.EntityId == meterId.ToString());
            Assert.Equal(3, anchor.RowCount);
            Assert.Equal(updates[1].EntryHash, anchor.LatestEntryHash);

            // "The last row by OccurredOnUtc" is not the head here - which is exactly why the
            // concurrency test that used that proxy flaked, and why the head is now identified as the
            // row nothing links back to.
            var all = await ctx.AuditLogs.AsNoTracking()
                .Where(a => a.EntityId == meterId.ToString())
                .ToListAsync();
            var lastByTimestamp = all.OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id).Last();
            Assert.NotEqual(anchor.LatestEntryHash, lastByTimestamp.EntryHash);

            var linkedFrom = all.Select(a => a.PreviousHash).Where(h => h is not null).ToHashSet(StringComparer.Ordinal);
            var head = Assert.Single(all, a => !linkedFrom.Contains(a.EntryHash!));
            Assert.Equal(anchor.LatestEntryHash, head.EntryHash);

            var verifier = scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
            var result = await verifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(
                typeof(Meter).AssemblyQualifiedName!, meterId.ToString()));

            Assert.True(result.IsValid, $"intact chain reported {result.Reason}: {result.Detail}");
            Assert.Equal(3, result.VerifiedRowCount);
        }
    }
}
