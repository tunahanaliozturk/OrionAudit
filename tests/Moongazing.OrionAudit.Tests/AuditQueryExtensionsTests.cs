using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

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

    private static async Task<TestContext> BuildAsync(
        string? tenantId = null, IAuditTenantResolver? resolver = null)
    {
        var services = new ServiceCollection();
        var cfg = new AuditConfigurationBuilder().Audit<Order>().Build();
        services.AddSingleton(cfg);
        if (resolver is not null)
        {
            services.AddScoped(_ => resolver);
        }
        else if (tenantId is not null)
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

    /// <summary>A registered resolver that cannot name a tenant - a dropped header, a job thread.</summary>
    private sealed class UnresolvedTenant : IAuditTenantResolver
    {
        public string? Resolve(IServiceProvider sp) => null;
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

    /// <summary>
    /// The tenant filter used to fail OPEN: with a resolver registered but unable to name a tenant,
    /// the read fell through unfiltered and returned every tenant's audit rows - a cross-tenant leak
    /// that fired exactly when the caller's identity was unknown. An unresolved tenant must deny.
    /// </summary>
    [Fact]
    public async Task TenantFilter_DeniesEveryTenantsRows_WhenTheResolverCannotNameOne()
    {
        await using var ctx = await BuildAsync(resolver: new UnresolvedTenant());
        var orderType = typeof(Order).AssemblyQualifiedName!;
        ctx.AuditLogs.AddRange(
            new AuditLog { EntityType = orderType, EntityId = "1", TenantId = "tenant-A", Diff = "[]", OccurredOnUtc = DateTime.UtcNow },
            new AuditLog { EntityType = orderType, EntityId = "2", TenantId = "tenant-B", Diff = "[]", OccurredOnUtc = DateTime.UtcNow },
            new AuditLog { EntityType = "Other", EntityId = "3", TenantId = "tenant-C", Diff = "[]", OccurredOnUtc = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        Assert.Empty(await ctx.AuditLog().ToListAsync());
        Assert.Empty(await ctx.AuditFor<Order>().ToListAsync());
    }

    /// <summary>
    /// The deny is scoped to the no-tenant stream rather than to nothing at all, so a genuinely
    /// single-tenant deployment whose resolver returns null by design still reads its own history.
    /// </summary>
    [Fact]
    public async Task TenantFilter_StillReturnsTheNoTenantStream_WhenTheResolverCannotNameOne()
    {
        await using var ctx = await BuildAsync(resolver: new UnresolvedTenant());
        ctx.AuditLogs.AddRange(
            new AuditLog { EntityType = "T", EntityId = "1", TenantId = null, Diff = "[]", OccurredOnUtc = DateTime.UtcNow },
            new AuditLog { EntityType = "T", EntityId = "2", TenantId = "", Diff = "[]", OccurredOnUtc = DateTime.UtcNow },
            new AuditLog { EntityType = "T", EntityId = "3", TenantId = "tenant-A", Diff = "[]", OccurredOnUtc = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var logs = await ctx.AuditLog().ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.DoesNotContain(logs, a => a.TenantId == "tenant-A");
    }
}
