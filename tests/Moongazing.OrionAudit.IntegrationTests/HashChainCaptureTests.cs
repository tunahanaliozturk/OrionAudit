using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.IntegrationTests;

/// <summary>
/// End-to-end coverage of tamper-evident hash-chaining through the real capture interceptor and the
/// DI-registered verifier, over an in-memory SQLite database.
/// </summary>
public class HashChainCaptureTests
{
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
                o.UseHashChain();
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
        services.AddOrionAudit<TestContext>(o => { o.Audit<Account>(); o.UseHashChain(); });
        services.AddSingleton(new SqliteConnection("DataSource=:memory:"));
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<IAuditIntegrityVerifier>());
        Assert.NotNull(sp.GetService<AuditHashChainOptions>());
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
        // reading the persisted head each time (the cross-save seam).
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
            Assert.Null(rows[0].PreviousHash);                  // genesis
            Assert.Equal(rows[0].EntryHash, rows[1].PreviousHash);
            Assert.Equal(rows[1].EntryHash, rows[2].PreviousHash);

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

        // Tamper: rewrite the captured Owner display on the first row directly in the table.
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var first = await ctx.AuditLogs
                .Where(a => a.EntityId == accountId.ToString())
                .OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id)
                .FirstAsync();
            first.Diff = first.Diff.Replace("10", "9999999", StringComparison.Ordinal);
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
            o.UseHashChain();
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

            var verifier = scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
            var all = await verifier.VerifyChainAsync(AuditChainVerificationRequest.All());
            Assert.True(all.IsValid);
            Assert.Equal(2, all.VerifiedRowCount);
        }
    }
}
