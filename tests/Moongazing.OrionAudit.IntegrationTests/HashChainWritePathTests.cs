using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.IntegrationTests;

/// <summary>
/// Regression coverage for the write-path defect that made <c>UseHashChain</c> report tampering on
/// intact chains: the anchor lock was taken outside the write transaction, so it serialized nothing.
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

    /// <summary>
    /// Defect 1. Two concurrent same-stream saves must not both chain onto the same head. The first
    /// capture to stamp is parked (by a capture observer) between reading the stream head and its
    /// SaveChanges; the second save runs in that window. With the anchor lock/read genuinely inside
    /// the write transaction the second save cannot commit a row anchored on the head the parked one
    /// already consumed - it contends, retries, and picks up the committed head. Without it both
    /// rows commit carrying the SAME PreviousHash and the chain no longer verifies.
    /// </summary>
    [Fact]
    public async Task ConcurrentSameStreamSaves_CannotBothAnchorOnTheSameHead()
    {
        var dbName = "chainwrite_" + Guid.NewGuid().ToString("N");
        var connectionString = $"DataSource=file:{dbName}?mode=memory&cache=shared";

        // Keep-alive connection holds the shared in-memory database up for the whole test.
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

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
            o.UseSqlite(connectionString).UseOrionAudit(sp), ServiceLifetime.Scoped);
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

        async Task UpdateAsync(int newBalance)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var scope = provider.CreateAsyncScope();
                    var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
                    var ledger = await ctx.Ledgers.FirstAsync(l => l.Id == ledgerId);
                    ledger.Balance = newBalance;
                    await ctx.SaveChangesAsync();
                    return;
                }
                catch (Exception ex) when (IsContention(ex) && attempt < 300)
                {
                    await Task.Delay(10);
                }
            }
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

    private static bool IsContention(Exception ex)
        => ex is SqliteException sqlite
            && (sqlite.SqliteErrorCode == 5 /* SQLITE_BUSY */ || sqlite.SqliteErrorCode == 6 /* SQLITE_LOCKED */)
            || ex.InnerException is SqliteException inner
            && (inner.SqliteErrorCode == 5 || inner.SqliteErrorCode == 6)
            || ex is DbUpdateException;

}
