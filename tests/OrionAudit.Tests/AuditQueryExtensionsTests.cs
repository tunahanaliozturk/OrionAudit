using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.Capture;
using OrionAudit.Configuration;

namespace OrionAudit.Tests;

public class AuditQueryExtensionsTests
{
    [Auditable]
    public sealed class Order
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Status { get; set; } = "New";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().HasKey(o => o.Id);
            modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
        }
    }

    private static async Task<TestContext> BuildAsync(string? tenantId = null)
    {
        var services = new ServiceCollection();
        var cfg = new AuditConfigurationBuilder().Audit<Order>().Build();
        services.AddSingleton(cfg);
        if (tenantId is not null)
        {
            services.AddScoped<IAuditTenantResolver>(_ => new StaticTenant(tenantId));
        }
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString())
             .AddInterceptors(new AuditSaveChangesInterceptor(sp)));
        var sp = services.BuildServiceProvider();
        return await Task.FromResult(sp.GetRequiredService<TestContext>());
    }

    private sealed class StaticTenant : IAuditTenantResolver
    {
        private readonly string id;
        public StaticTenant(string id) => this.id = id;
        public string? Resolve(IServiceProvider sp) => id;
    }

    [Fact]
    public async Task AuditFor_FiltersByEntityType()
    {
        await using var ctx = await BuildAsync();
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        var logs = await ctx.AuditFor<Order>().ToListAsync();
        Assert.Single(logs);
    }

    [Fact]
    public async Task AuditLog_ReturnsAllRows()
    {
        await using var ctx = await BuildAsync();
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        var logs = await ctx.AuditLog().ToListAsync();
        Assert.NotEmpty(logs);
    }

    [Fact]
    public async Task AuditFor_AppliesTenantFilter_WhenResolverRegistered()
    {
        await using var ctx = await BuildAsync(tenantId: "tenant-A");
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        var logs = await ctx.AuditFor<Order>().ToListAsync();
        var entry = Assert.Single(logs);
        Assert.Equal("tenant-A", entry.TenantId);
    }
}
