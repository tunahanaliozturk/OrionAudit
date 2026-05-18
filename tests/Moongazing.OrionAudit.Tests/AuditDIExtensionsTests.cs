using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AuditDIExtensionsTests
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
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    [Fact]
    public void AddOrionAudit_RegistersConfigurationAndReconstructor()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Order>());
        var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<IAuditConfiguration>());
    }

    [Fact]
    public async Task UseOrionAudit_AndApplyConfigurations_EndToEnd_Works()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Order>());
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString())
             .UseOrionAudit(sp));

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.Equal(AuditAction.Inserted, entry.Action);
    }

    [Fact]
    public void AddOrionAudit_CalledTwice_DoesNotDuplicateRegistrations()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Order>());
        services.AddOrionAudit<TestContext>(o => o.Audit<Order>());

        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(IAuditConfiguration)));
    }
}
