using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.IntegrationTests;

/// <summary>
/// Regression coverage for the two write-path defects that made <c>UseHashChain</c> report tampering
/// on intact chains: the anchor lock that was taken outside the write transaction, and the walk order
/// that was only a proxy for the chain's real (insertion) order.
/// </summary>
public class HashChainWritePathTests
{
    // Fixed 32-byte key (base64) so MACs are reproducible across runs.
    private const string KeyId1Base64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Auditable]
    public sealed class Ledger
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Balance { get; set; }
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Ledger> Ledgers => Set<Ledger>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditChainAnchor> Anchors => Set<AuditChainAnchor>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Ledger>().HasKey(l => l.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    // A clock that never moves, so every save in a trial stamps the SAME OccurredOnUtc. This is not
    // an exotic setup: one timestamp is computed per SaveChanges and stamped on every row of that
    // save, and MySQL DATETIME(6) / PostgreSQL timestamp truncate further, so equal timestamps
    // across two saves of one entity are routine in production.
    private sealed class FrozenClock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }

    /// <summary>
    /// Defect 2. Two saves on ONE entity under a frozen clock produce two audit rows sharing a
    /// timestamp. Their chain order is save order; the verifier's walk order was
    /// <c>(OccurredOnUtc, Id)</c>, so the tie was broken by a random Guid - unrelated to the order
    /// the rows were chained in. Roughly half of all intact streams therefore verified as
    /// <c>BrokenLink</c>. Forty independent trials make that coin flip a certainty (a clean run had
    /// probability 2^-40), so this fails reliably before the explicit chain sequence and passes
    /// after it.
    /// </summary>
    [Fact]
    public async Task SameStreamSavesSharingOneTimestamp_VerifyOnEveryTrial()
    {
        const int Trials = 40;

        var (provider, conn) = await BuildAsync(
            clock: new FrozenClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        await using var _ = provider;
        await using var __ = conn;

        var ledgerIds = new List<Guid>(Trials);
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            for (var trial = 0; trial < Trials; trial++)
            {
                var ledger = new Ledger { Balance = 0 };
                ctx.Ledgers.Add(ledger);
                await ctx.SaveChangesAsync();       // row 1: the stream's genesis
                ledger.Balance = trial + 1;
                await ctx.SaveChangesAsync();       // row 2: chained onto row 1, same timestamp
                ledgerIds.Add(ledger.Id);
            }
        }

        var broken = new List<string>();
        await using (var scope = provider.CreateAsyncScope())
        {
            var verifier = scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
            foreach (var id in ledgerIds)
            {
                var result = await verifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(
                    typeof(Ledger).AssemblyQualifiedName!, id.ToString()));
                if (!result.IsValid)
                {
                    broken.Add($"{id:N}={result.Reason}");
                }
            }
        }

        Assert.True(
            broken.Count == 0,
            $"{broken.Count} of {Trials} intact streams reported tampering: {string.Join(", ", broken)}");
    }

    /// <summary>
    /// Defect 1. Two concurrent same-stream saves must not both chain onto the same head. The first
    /// capture to stamp is parked (by a capture observer) between reading the stream head and its
    /// SaveChanges; the second save runs in that window. With the anchor lock/read genuinely inside
    /// the write transaction, the second save <b>waits</b> on the first and then chains onto the head
    /// it committed. Without it both rows commit carrying the SAME PreviousHash and the chain no
    /// longer verifies.
    /// </summary>
    /// <remarks>
    /// Neither task retries. That is the assertion: an ordinary consumer calls <c>SaveChangesAsync</c>
    /// once, and a chained save that contends has to block and succeed, not surface a
    /// <c>SQLITE_BUSY</c> for the caller to handle. A retry loop here would pass either way and prove
    /// nothing - it would just absorb the collisions the fix is supposed to remove.
    /// </remarks>
    [Fact]
    public async Task ConcurrentSameStreamSaves_CannotBothAnchorOnTheSameHead()
    {
        // A real file, not a shared-cache in-memory database: shared cache serialises with
        // table-level locks that report SQLITE_LOCKED, which SQLite's busy handler does not wait on,
        // so no amount of correct locking would let a contending writer block-and-proceed there. A
        // file database - what a SQLite consumer actually runs - uses the ordinary lock ladder and
        // the connection's busy timeout, so contention becomes a wait.
        using var db = new TempSqliteDatabase();

        var observer = new ParkFirstCaptureObserver();
        var services = new ServiceCollection();
        services.AddSingleton<IAuditCaptureObserver>(observer);
        services.AddOrionAudit<TestContext>(o =>
        {
            o.Audit<Ledger>();
            o.UseHashChain(h => h.UseKey(1, KeyId1Base64));
        });
        // Each resolved context gets its OWN connection, so the two save tasks are real concurrency
        // against one shared-cache database.
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(db.ConnectionString).UseOrionAudit(sp), ServiceLifetime.Scoped);
        await using var provider = services.BuildServiceProvider();

        Guid ledgerId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            await ctx.Database.EnsureCreatedAsync();
            var ledger = new Ledger { Balance = 0 };
            ctx.Ledgers.Add(ledger);
            await ctx.SaveChangesAsync();   // genesis row; the observer is not armed yet
            ledgerId = ledger.Id;
        }

        observer.Arm();

        // One save, no retry - exactly what a consumer writes.
        async Task UpdateAsync(int newBalance)
        {
            await using var scope = provider.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var ledger = await ctx.Ledgers.FirstAsync(l => l.Id == ledgerId);
            ledger.Balance = newBalance;
            await ctx.SaveChangesAsync();
        }

        // Task.Run, not a bare call: SQLite's async methods complete synchronously, so invoking
        // UpdateAsync directly would run the whole first save (park included) on this thread before
        // the second was even started - no concurrency at all.
        await Task.WhenAll(Task.Run(() => UpdateAsync(100)), Task.Run(() => UpdateAsync(200)));

        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var ctx = verifyScope.ServiceProvider.GetRequiredService<TestContext>();
            var rows = await ctx.AuditLogs
                .Where(a => a.EntityId == ledgerId.ToString())
                .ToListAsync();

            Assert.Equal(3, rows.Count);

            // The defect's exact symptom: two rows anchored on one head.
            var previousHashes = rows.Where(r => r.PreviousHash is not null)
                .Select(r => r.PreviousHash!)
                .ToList();
            Assert.Equal(
                previousHashes.Count,
                previousHashes.Distinct(StringComparer.Ordinal).Count());

            var anchor = await ctx.Anchors.SingleAsync(a => a.EntityId == ledgerId.ToString());
            Assert.Equal(3, anchor.RowCount);

            var verifier = verifyScope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
            var result = await verifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(
                typeof(Ledger).AssemblyQualifiedName!, ledgerId.ToString()));
            Assert.True(result.IsValid, $"intact chain reported {result.Reason}: {result.Detail}");
        }
    }

    // Parks the FIRST armed capture between "stream head read + rows stamped" and SaveChanges (the
    // observer fires exactly there), for a bounded time so the test can never deadlock: whichever
    // writer wins the race to the observer waits, the other one runs in that window.
    private sealed class ParkFirstCaptureObserver : IAuditCaptureObserver
    {
        private static readonly TimeSpan ParkFor = TimeSpan.FromMilliseconds(750);
        private int armed;
        private int arrivals;

        public void Arm() => Interlocked.Exchange(ref armed, 1);

        public void OnCaptured(int auditedEntityCount, bool isAsyncCapture)
        {
            if (Volatile.Read(ref armed) == 0 || Interlocked.Increment(ref arrivals) != 1)
            {
                return;
            }
            Thread.Sleep(ParkFor);
        }
    }

    /// <summary>
    /// The assumption the chain's serialization on SQLite rests on: EF's <c>BeginTransaction</c> takes
    /// the write lock at BEGIN, not at the first write.
    /// </summary>
    /// <remarks>
    /// SQLite has no row locks, so <c>AnchorLockDialect</c> issues no lock statement there and the
    /// transaction itself is what serialises same-stream appends. That only works because
    /// Microsoft.Data.Sqlite's default Serializable isolation emits <c>BEGIN IMMEDIATE</c>. Were it
    /// ever to emit a plain (deferred) <c>BEGIN</c>, two concurrent saves could both open, both read
    /// the same anchor head, and only collide on the way out - the silently forked chain this whole
    /// mechanism exists to prevent - and nothing else in the suite would notice. This pins it.
    /// </remarks>
    [Fact]
    public async Task EfSqliteTransaction_HoldsItsWriteLockFromBegin()
    {
        using var db = new TempSqliteDatabase();
        var options = new DbContextOptionsBuilder<TestContext>().UseSqlite(db.ConnectionString).Options;
        await using (var setup = new TestContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        await using var holder = new TestContext(options);
        await using var transaction = await holder.Database.BeginTransactionAsync();
        // Deliberately nothing written yet: a deferred transaction would hold no write lock here.

        // A second connection with a one-second busy timeout, so a genuine wait fails fast.
        var connectionString = new SqliteConnectionStringBuilder(db.ConnectionString) { DefaultTimeout = 1 };
        await using var other = new SqliteConnection(connectionString.ToString());
        await other.OpenAsync();
        var write = other.CreateCommand();
        write.CommandText = "INSERT INTO OrionAudit_Chain_Anchor "
            + "(EntityType, EntityId, TenantId, LatestEntryHash, RowCount, KeyId, PrunedRowCount) "
            + "VALUES ('t', 'e', '', 'h', 1, 1, 0)";

        var blocked = await Assert.ThrowsAsync<SqliteException>(() => write.ExecuteNonQueryAsync());
        Assert.Equal(5, blocked.SqliteErrorCode); // SQLITE_BUSY: the holder's write lock is already taken

        await transaction.RollbackAsync();
    }

    private static async Task<(ServiceProvider provider, SqliteConnection conn)> BuildAsync(TimeProvider? clock = null)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();

        var services = new ServiceCollection();
        // AddOrionAudit registers TimeProvider.System with TryAddSingleton, so a clock registered
        // first wins and the interceptor stamps OccurredOnUtc from it.
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }
        services.AddOrionAudit<TestContext>(o =>
        {
            o.Audit<Ledger>();
            o.UseHashChain(h => h.UseKey(1, KeyId1Base64));
        });
        services.AddSingleton(conn);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TestContext>().Database.EnsureCreatedAsync();
        return (provider, conn);
    }
}
