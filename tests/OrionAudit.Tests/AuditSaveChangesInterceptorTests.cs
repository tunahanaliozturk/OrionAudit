using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.Capture;
using OrionAudit.Configuration;

namespace OrionAudit.Tests;

public class AuditSaveChangesInterceptorTests
{
    [Auditable]
    public sealed class Order
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Status { get; set; } = "New";
        [NotAuditable] public string Internal { get; set; } = "";
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

    private static (ServiceProvider sp, IAuditConfiguration cfg) Build()
    {
        var services = new ServiceCollection();
        var cfgBuilder = new AuditConfigurationBuilder();
        cfgBuilder.Audit<Order>();
        var cfg = cfgBuilder.Build();
        services.AddSingleton(cfg);
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString())
             .AddInterceptors(new AuditSaveChangesInterceptor(sp)));
        return (services.BuildServiceProvider(), cfg);
    }

    [Fact]
    public async Task Insert_WritesAuditLog_WithInsertedAction()
    {
        var (sp, _) = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var order = new Order { Status = "Pending" };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        var logs = await ctx.AuditLogs.ToListAsync();
        var entry = Assert.Single(logs);
        Assert.Equal(AuditAction.Inserted, entry.Action);
        Assert.Equal(order.Id.ToString(), entry.EntityId);
        Assert.Contains("\"op\":\"add\"", entry.Diff);
    }

    [Fact]
    public async Task Update_WritesAuditLog_WithUpdatedAction_AndReplaceDiff()
    {
        var (sp, _) = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var order = new Order { Status = "Pending" };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        order.Status = "Shipped";
        await ctx.SaveChangesAsync();

        var logs = await ctx.AuditLogs.Where(l => l.Action == AuditAction.Updated).ToListAsync();
        var entry = Assert.Single(logs);
        Assert.Contains("\"op\":\"replace\"", entry.Diff);
        Assert.Contains("Shipped", entry.Diff);
    }

    [Fact]
    public async Task Delete_WritesAuditLog_WithDeletedAction_AndSnapshot()
    {
        var (sp, _) = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var order = new Order { Status = "Pending" };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        ctx.Orders.Remove(order);
        await ctx.SaveChangesAsync();

        var entry = Assert.Single(await ctx.AuditLogs.Where(l => l.Action == AuditAction.Deleted).ToListAsync());
        Assert.NotNull(entry.Snapshot);
        Assert.Contains("Pending", entry.Snapshot);
    }

    [Fact]
    public async Task ExcludedField_DoesNotAppear_InDiff()
    {
        var (sp, _) = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var order = new Order { Status = "Pending", Internal = "secret" };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.DoesNotContain("/Internal", entry.Diff);
        Assert.DoesNotContain("secret", entry.Diff);
    }

    [Fact]
    public async Task UnauditedEntity_ProducesNoAuditLog()
    {
        var services = new ServiceCollection();
        var cfg = new AuditConfigurationBuilder().Build();
        services.AddSingleton<IAuditConfiguration>(cfg);
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString())
             .AddInterceptors(new AuditSaveChangesInterceptor(sp)));
        await using var sp = services.BuildServiceProvider();

        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        Assert.Empty(await ctx.AuditLogs.ToListAsync());
    }
}
