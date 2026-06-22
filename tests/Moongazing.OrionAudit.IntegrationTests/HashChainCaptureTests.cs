using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.IntegrationTests;

/// <summary>
/// End-to-end coverage of tamper-evident hash-chaining through the real capture interceptor and the
/// DI-registered verifier, over an in-memory SQLite database. The chain is a keyed MAC anchored by a
/// per-stream head row, so these tests configure a key and exercise concurrency + truncation.
/// </summary>
public class HashChainCaptureTests
{
    // Fixed 32-byte key (base64) so MACs are reproducible across runs.
    private const string KeyId1Base64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Auditable]
    public sealed class Account
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Owner { get; set; } = "";
        public int Balance { get; set; }
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Account> Accounts => Set<Account>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditChainAnchor> Anchors => Set<AuditChainAnchor>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Account>().HasKey(a => a.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private static async Task<(ServiceProvider provider, SqliteConnection conn)> BuildAsync(bool enableHashChain)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();

        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o =>
        {
            o.Audit<Account>();
            if (enableHashChain)
            {
                o.UseHashChain(h => h.UseKey(1, KeyId1Base64));
            }
        });
        services.AddSingleton(conn);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        await ctx.Database.EnsureCreatedAsync();
        return (provider, conn);
    }

    [Fact]
    public void HashChainDisabled_DoesNotRegisterVerifier()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Account>());
        using var sp = services.BuildServiceProvider();
        Assert.Null(sp.GetService<IAuditIntegrityVerifier>());
        Assert.Null(sp.GetService<AuditHashChainOptions>());
    }

    [Fact]
    public void HashChainEnabled_RegistersVerifierAndOptions()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => { o.Audit<Account>(); o.UseHashChain(h => h.UseKey(1, KeyId1Base64)); });
        services.AddSingleton(new SqliteConnection("DataSource=:memory:"));
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<IAuditIntegrityVerifier>());
        Assert.NotNull(sp.GetService<AuditHashChainOptions>());
        Assert.NotNull(sp.GetService<IAuditChainKeyProvider>());
    }

    [Fact]
    public void HashChainEnabledWithoutKey_FailsClearlyAtResolve()
    {
        // The chain is a keyed MAC; enabling it without a key (and without a custom key provider) must
        // fail with a clear configuration error rather than silently producing an unkeyed chain.
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => { o.Audit<Account>(); o.UseHashChain(); });
        services.AddSingleton(new SqliteConnection("DataSource=:memory:"));
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        var ex = Assert.Throws<OrionAuditConfigurationException>(
            () => scope.ServiceProvider.GetRequiredService<IAuditChainKeyProvider>());
        Assert.Contains("keyed MAC", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_WithHashChainDisabled_LeavesHashColumnsNull()
    {
        var (provider, conn) = await BuildAsync(enableHashChain: false);
        await using var _ = provider;
        await using var __ = conn;

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Accounts.Add(new Account { Owner = "Alice", Balance = 100 });
            await ctx.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var row = await ctx.AuditLogs.SingleAsync();
            Assert.Null(row.EntryHash);
            Assert.Null(row.PreviousHash);
            Assert.Null(row.HashKeyId);
            Assert.Equal(0, await ctx.Anchors.CountAsync());
        }
    }

    [Fact]
    public async Task Capture_WithHashChainEnabled_StampsAndVerifiesAcrossMultipleSaves()
    {
        var (provider, conn) = await BuildAsync(enableHashChain: true);
        await using var _ = provider;
        await using var __ = conn;

        Guid accountId;

        // Insert, then two updates: each in its own SaveChanges, so the chain must be continued by
        // reading the persisted anchor each time (the cross-save seam).
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var account = new Account { Owner = "Bob", Balance = 0 };
            ctx.Accounts.Add(account);
            await ctx.SaveChangesAsync();
            accountId = account.Id;

            account.Balance = 50;
            await ctx.SaveChangesAsync();

            account.Balance = 75;
            await ctx.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var rows = await ctx.AuditLogs
                .Where(a => a.EntityId == accountId.ToString())
                .OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id)
                .ToListAsync();

            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.NotNull(r.EntryHash));
            Assert.All(rows, r => Assert.Equal(1, r.HashKeyId));
            Assert.Null(rows[0].PreviousHash);                  // genesis
            Assert.Equal(rows[0].EntryHash, rows[1].PreviousHash);
            Assert.Equal(rows[1].EntryHash, rows[2].PreviousHash);

            // Anchor advanced to the latest row with the full count.
            var anchor = await ctx.Anchors.SingleAsync();
            Assert.Equal(3, anchor.RowCount);
            Assert.Equal(rows[2].EntryHash, anchor.LatestEntryHash);

            var verifier = scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
            var result = await verifier.VerifyChainAsync(
                AuditChainVerificationRequest.ForEntity(rows[0].EntityType, accountId.ToString()));
            Assert.True(result.IsValid);
            Assert.Equal(3, result.VerifiedRowCount);
        }
    }

    [Fact]
    public async Task Capture_ThenTamper_IsDetectedByVerifier()
    {
        var (provider, conn) = await BuildAsync(enableHashChain: true);
        await using var _ = provider;
        await using var __ = conn;

        Guid accountId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var account = new Account { Owner = "Carol", Balance = 10 };
            ctx.Accounts.Add(account);
            await ctx.SaveChangesAsync();
            accountId = account.Id;
            account.Balance = 20;
            await ctx.SaveChangesAsync();
        }

        // Tamper: overwrite the captured Diff on the first row directly in the table with a fixed,
        // deterministic value that differs from whatever was captured. (A substring Replace can no-op
        // if the search text is absent; a fixed assignment is unambiguous.)
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var first = await ctx.AuditLogs
                .Where(a => a.EntityId == accountId.ToString())
                .OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id)
                .FirstAsync();
            first.Diff = "[{\"op\":\"replace\",\"path\":\"/Balance\",\"value\":9999999}]";
            await ctx.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var verifier = scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
            var result = await verifier.VerifyChainAsync(
                AuditChainVerificationRequest.ForEntity(typeof(Account).AssemblyQualifiedName!, accountId.ToString()));
            Assert.False(result.IsValid);
            Assert.Equal(AuditChainBreakReason.ContentMismatch, result.Reason);
        }
    }

    [Fact]
    public async Task Capture_ThenDeleteTailRow_IsDetectedAsTruncation()
    {
        var (provider, conn) = await BuildAsync(enableHashChain: true);
        await using var _ = provider;
        await using var __ = conn;

        Guid accountId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var account = new Account { Owner = "Frank", Balance = 1 };
            ctx.Accounts.Add(account);
            await ctx.SaveChangesAsync();
            accountId = account.Id;
            account.Balance = 2;
            await ctx.SaveChangesAsync();
            account.Balance = 3;
            await ctx.SaveChangesAsync();
        }

        // Delete the most recent audit row directly: the surviving prefix still links, but the anchor
        // remembers the true tail.
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var tail = await ctx.AuditLogs
                .Where(a => a.EntityId == accountId.ToString())
                .OrderByDescending(a => a.OccurredOnUtc).ThenByDescending(a => a.Id)
                .FirstAsync();
            ctx.AuditLogs.Remove(tail);
            await ctx.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var verifier = scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
            var result = await verifier.VerifyChainAsync(
                AuditChainVerificationRequest.ForEntity(typeof(Account).AssemblyQualifiedName!, accountId.ToString()));
            Assert.False(result.IsValid);
            Assert.Equal(AuditChainBreakReason.Truncated, result.Reason);
        }
    }

    [Fact]
    public async Task ConcurrentSameStreamAppends_DoNotCorruptChain()
    {
        // Two independent contexts on a SHARED-cache SQLite database append to the SAME entity stream
        // concurrently. The per-stream anchor (plus SQLite's write serialization) must prevent both
        // from stamping the same PreviousHash; under contention the loser sees SQLITE_BUSY, retries,
        // and picks up the committed head. The end state must be a valid, gap-free chain.
        var dbName = "concurrent_" + Guid.NewGuid().ToString("N");
        var connectionString = $"DataSource=file:{dbName}?mode=memory&cache=shared";

        // A keep-alive connection holds the shared in-memory DB alive for the whole test.
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o =>
        {
            o.Audit<Account>();
            o.UseHashChain(h => h.UseKey(1, KeyId1Base64));
        });
        // Each resolved context gets its OWN connection (not a shared singleton), so the two save
        // tasks run on separate connections against the same shared-cache database - real concurrency.
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(connectionString).UseOrionAudit(sp), ServiceLifetime.Scoped);
        await using var provider = services.BuildServiceProvider();

        Guid accountId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            await ctx.Database.EnsureCreatedAsync();
            var account = new Account { Owner = "Grace", Balance = 0 };
            ctx.Accounts.Add(account);
            await ctx.SaveChangesAsync();   // genesis row
            accountId = account.Id;
        }

        // Two concurrent updaters, each its own scope/context/connection, each a retry loop on BUSY.
        async Task UpdateAsync(int newBalance)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var scope = provider.CreateAsyncScope();
                    var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
                    var account = await ctx.Accounts.FirstAsync(a => a.Id == accountId);
                    account.Balance = newBalance;
                    await ctx.SaveChangesAsync();
                    return;
                }
                catch (Exception ex) when (IsTransient(ex) && attempt < 50)
                {
                    await Task.Delay(10);
                }
            }
        }

        await Task.WhenAll(UpdateAsync(100), UpdateAsync(200));

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var rows = await ctx.AuditLogs
                .Where(a => a.EntityId == accountId.ToString())
                .OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id)
                .ToListAsync();

            // Genesis + two concurrent updates = 3 rows, each chained to a distinct predecessor.
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.NotNull(r.EntryHash));
            Assert.Null(rows[0].PreviousHash);

            // No two rows share the same PreviousHash (the corruption symptom the anchor prevents).
            var previousHashes = rows.Skip(1).Select(r => r.PreviousHash).ToList();
            Assert.Equal(previousHashes.Count, previousHashes.Distinct(StringComparer.Ordinal).Count());

            var anchor = await ctx.Anchors.SingleAsync(a => a.EntityId == accountId.ToString());
            Assert.Equal(3, anchor.RowCount);
            Assert.Equal(rows[^1].EntryHash, anchor.LatestEntryHash);

            var verifier = scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
            var result = await verifier.VerifyChainAsync(
                AuditChainVerificationRequest.ForEntity(rows[0].EntityType, accountId.ToString()));
            Assert.True(result.IsValid);
            Assert.Equal(3, result.VerifiedRowCount);
        }
    }

    private static bool IsTransient(Exception ex)
        => ex is SqliteException sqlite
            && (sqlite.SqliteErrorCode == 5 /* SQLITE_BUSY */ || sqlite.SqliteErrorCode == 6 /* SQLITE_LOCKED */)
            || ex.InnerException is SqliteException inner
            && (inner.SqliteErrorCode == 5 || inner.SqliteErrorCode == 6)
            || ex is DbUpdateException; // a concurrency-induced update failure is retryable here

    [Fact]
    public async Task AsyncCapture_DispatchedRows_AreChainedAndVerify()
    {
        // Async-capture path: the interceptor writes queue rows; the dispatcher builds the AuditLog
        // rows and (this feature) stamps the chain in its own transaction. Enable both features.
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var __ = conn;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<TestContext>(o =>
        {
            o.Audit<Account>();
            o.UseAsyncCapture();
            o.UseHashChain(h => h.UseKey(1, KeyId1Base64));
        });
        services.AddSingleton(conn);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestContext>().Database.EnsureCreatedAsync();
        }

        Guid accountId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var account = new Account { Owner = "Eve", Balance = 0 };
            ctx.Accounts.Add(account);
            await ctx.SaveChangesAsync();
            accountId = account.Id;
        }
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var account = await ctx.Accounts.FirstAsync();
            account.Balance = 100;
            await ctx.SaveChangesAsync();
        }

        // Drain the queue: the dispatcher promotes both queue rows to AuditLog rows and chains them.
        var dispatcher = provider.GetRequiredService<Capture.IAuditDispatcher>();
        var processed = await dispatcher.FlushPendingAsync();
        Assert.Equal(2, processed);

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var rows = await ctx.AuditLogs
                .Where(a => a.EntityId == accountId.ToString())
                .OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id)
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.NotNull(r.EntryHash));
            Assert.Null(rows[0].PreviousHash);
            Assert.Equal(rows[0].EntryHash, rows[1].PreviousHash);

            var verifier = scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
            var result = await verifier.VerifyChainAsync(
                AuditChainVerificationRequest.ForEntity(rows[0].EntityType, accountId.ToString()));
            Assert.True(result.IsValid);
            Assert.Equal(2, result.VerifiedRowCount);
        }
    }

    [Fact]
    public async Task Capture_MultipleEntitiesInOneSave_EachStreamGetsItsOwnGenesis()
    {
        var (provider, conn) = await BuildAsync(enableHashChain: true);
        await using var _ = provider;
        await using var __ = conn;

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            // Two distinct entities inserted in the SAME SaveChanges: the batch contains two
            // streams, and each must start its own chain (genesis PreviousHash == null).
            ctx.Accounts.Add(new Account { Owner = "D1", Balance = 1 });
            ctx.Accounts.Add(new Account { Owner = "D2", Balance = 2 });
            await ctx.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var rows = await ctx.AuditLogs.ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.NotNull(r.EntryHash));
            Assert.All(rows, r => Assert.Null(r.PreviousHash)); // both are their stream's genesis

            // Two anchors, one per stream.
            Assert.Equal(2, await ctx.Anchors.CountAsync());

            var verifier = scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
            var all = await verifier.VerifyChainAsync(AuditChainVerificationRequest.All());
            Assert.True(all.IsValid);
            Assert.Equal(2, all.VerifiedRowCount);
        }
    }
}
