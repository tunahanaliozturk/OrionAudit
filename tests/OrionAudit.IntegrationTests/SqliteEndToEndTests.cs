using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;

namespace OrionAudit.IntegrationTests;

public class SqliteEndToEndTests : IAsyncLifetime
{
    [Auditable]
    public sealed class Customer
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Customer> Customers => Set<Customer>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>().HasKey(c => c.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private SqliteConnection connection = null!;
    private ServiceProvider provider = null!;

    public async ValueTask InitializeAsync()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Customer>());
        services.AddSingleton(connection);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await provider.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task InsertUpdateDelete_FullCycle_ProducesThreeAuditRows()
    {
        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var customer = new Customer { Name = "Alice", Email = "alice@example.com" };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        customer.Name = "Alice (updated)";
        await ctx.SaveChangesAsync();

        ctx.Customers.Remove(customer);
        await ctx.SaveChangesAsync();

        var logs = await ctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.Equal(3, logs.Count);
        Assert.Equal(AuditAction.Inserted, logs[0].Action);
        Assert.Equal(AuditAction.Updated, logs[1].Action);
        Assert.Equal(AuditAction.Deleted, logs[2].Action);
        Assert.NotNull(logs[2].Snapshot);
    }

    [Fact]
    public async Task Reconstruct_ReplaysFullHistoryToLatestState()
    {
        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var customer = new Customer { Name = "Bob", Email = "bob@example.com" };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();
        customer.Name = "Bob Smith";
        await ctx.SaveChangesAsync();

        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();
        var result = await reconstructor.ReconstructAsync<Customer>(customer.Id.ToString(), DateTime.UtcNow.AddMinutes(1));

        Assert.NotNull(result);
        Assert.Equal("Bob Smith", result.Name);
    }
}
