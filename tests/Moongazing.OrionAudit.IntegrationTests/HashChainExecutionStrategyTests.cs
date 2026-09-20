using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.IntegrationTests;

/// <summary>
/// Hash-chaining against a DbContext configured with a <em>retrying</em> execution strategy - what
/// <c>EnableRetryOnFailure()</c> gives you, and an entirely ordinary production configuration.
/// </summary>
/// <remarks>
/// EF Core refuses user-initiated transactions inside a retriable unit, and it enters that unit
/// inside <c>SaveChanges</c>, after the capture interceptor has already run. A transaction opened
/// while capturing therefore existed before the strategy started, and every chained save failed with
/// EF's <c>InvalidOperationException</c> - a message that never mentions OrionAudit. The dispatcher
/// owns its own save and can run the whole unit through the strategy; the interceptor cannot, so it
/// refuses with the one-line change that makes it work.
/// </remarks>
public class HashChainExecutionStrategyTests
{
    private const string KeyId1Base64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Auditable]
    public sealed class Invoice
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Amount { get; set; }
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Invoice> Invoices => Set<Invoice>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Invoice>().HasKey(i => i.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    // A strategy that reports RetriesOnFailure (which is all EF's transaction guard looks at) without
    // actually retrying anything, so the tests stay deterministic. SQL Server's
    // SqlServerRetryingExecutionStrategy - what EnableRetryOnFailure() installs - trips the same guard.
    private sealed class RetryingExecutionStrategy : ExecutionStrategy
    {
        public RetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1)) { }

        protected override bool ShouldRetryOn(Exception exception) => false;
    }

    [Fact]
    public async Task ChainedSave_UnderRetryingStrategy_RefusesWithTheChangeThatFixesIt()
    {
        var (provider, conn) = await BuildAsync(asyncCapture: false);
        await using var _ = provider;
        await using var __ = conn;

        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        Assert.True(ctx.Database.CreateExecutionStrategy().RetriesOnFailure); // the configuration under test

        ctx.Invoices.Add(new Invoice { Amount = 100 });

        // Before the fix this was EF's InvalidOperationException about user-initiated transactions,
        // which says nothing about OrionAudit and leaves the reader with no idea what to change.
        var ex = await Assert.ThrowsAsync<OrionAuditConfigurationException>(() => ctx.SaveChangesAsync());
        Assert.Contains("CreateExecutionStrategy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BeginTransactionAsync", ex.Message, StringComparison.Ordinal);
        Assert.Contains("hash chain", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChainedSave_InsideConsumerOwnedRetriableUnit_ChainsAndVerifies()
    {
        // The resolution the exception above names, verified end to end: with the consumer owning the
        // transaction inside the strategy, the interceptor opens nothing and stamps inside theirs.
        var (provider, conn) = await BuildAsync(asyncCapture: false);
        await using var _ = provider;
        await using var __ = conn;

        Guid invoiceId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var invoice = new Invoice { Amount = 100 };
            await ctx.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await ctx.Database.BeginTransactionAsync();
                ctx.Invoices.Add(invoice);
                await ctx.SaveChangesAsync();
                await transaction.CommitAsync();
            });
            invoiceId = invoice.Id;
        }

        await AssertStreamVerifiesAsync(provider, invoiceId, expectedRows: 1);
    }

    [Fact]
    public async Task AsyncCaptureDispatch_UnderRetryingStrategy_ChainsAndVerifies()
    {
        // The dispatcher owns its SaveChanges, so it runs the whole begin/stamp/save/commit unit
        // through the configured strategy. Before the fix it opened the transaction outside the
        // strategy and every chained dispatch cycle threw.
        var (provider, conn) = await BuildAsync(asyncCapture: true);
        await using var _ = provider;
        await using var __ = conn;

        Guid invoiceId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var invoice = new Invoice { Amount = 250 };
            ctx.Invoices.Add(invoice);
            await ctx.SaveChangesAsync();   // async capture writes a queue row; no chain, no transaction
            invoiceId = invoice.Id;
        }

        var dispatched = await provider.GetRequiredService<Capture.IAuditDispatcher>().FlushPendingAsync();
        Assert.Equal(1, dispatched);

        await AssertStreamVerifiesAsync(provider, invoiceId, expectedRows: 1);
    }

    private static async Task AssertStreamVerifiesAsync(ServiceProvider provider, Guid invoiceId, int expectedRows)
    {
        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var rows = await ctx.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == invoiceId.ToString())
            .ToListAsync();
        Assert.Equal(expectedRows, rows.Count);
        Assert.All(rows, r => Assert.NotNull(r.EntryHash));
        Assert.All(rows, r => Assert.NotNull(r.ChainSequence));

        var verifier = scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
        var result = await verifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(
            typeof(Invoice).AssemblyQualifiedName!, invoiceId.ToString()));
        Assert.True(result.IsValid, $"intact chain reported {result.Reason}: {result.Detail}");
    }

    private static async Task<(ServiceProvider provider, SqliteConnection conn)> BuildAsync(bool asyncCapture)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<TestContext>(o =>
        {
            o.Audit<Invoice>();
            if (asyncCapture)
            {
                o.UseAsyncCapture();
            }
            o.UseHashChain(h => h.UseKey(1, KeyId1Base64));
        });
        services.AddSingleton(conn);
        services.AddDbContext<TestContext>((sp, o) => o
            .UseSqlite(
                sp.GetRequiredService<SqliteConnection>(),
                b => b.ExecutionStrategy(d => new RetryingExecutionStrategy(d)))
            .UseOrionAudit(sp));
        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TestContext>().Database.EnsureCreatedAsync();
        return (provider, conn);
    }
}
