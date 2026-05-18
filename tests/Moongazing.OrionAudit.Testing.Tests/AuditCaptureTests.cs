using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Testing;

namespace Moongazing.OrionAudit.Testing.Tests;

public class AuditCaptureTests
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

    private static async Task<TestContext> NewContextAsync()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Order>());
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));
        var sp = services.BuildServiceProvider();
        return await Task.FromResult(sp.GetRequiredService<TestContext>());
    }

    [Fact]
    public async Task Capture_From_ProvidesAllAuditRows()
    {
        await using var ctx = await NewContextAsync();
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        var capture = AuditCapture.From(ctx);
        Assert.Single(capture.All);
    }

    [Fact]
    public async Task Should_HaveLogged_Passes_WhenActionPresent()
    {
        await using var ctx = await NewContextAsync();
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        AuditCapture.From(ctx).Should().HaveLogged<Order>(AuditAction.Inserted);
    }

    [Fact]
    public async Task Should_HaveLogged_Throws_WhenActionMissing()
    {
        await using var ctx = await NewContextAsync();
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        Assert.Throws<OrionAuditAssertionException>(() =>
            AuditCapture.From(ctx).Should().HaveLogged<Order>(AuditAction.Deleted));
    }

    [Fact]
    public async Task Should_NotHaveLogged_Passes_WhenNoLogs()
    {
        await using var ctx = await NewContextAsync();
        AuditCapture.From(ctx).Should().NotHaveLogged<Order>();
    }

    [Fact]
    public async Task Should_HaveLoggedExactly_VerifiesCount()
    {
        await using var ctx = await NewContextAsync();
        ctx.Orders.Add(new Order { Status = "A" });
        ctx.Orders.Add(new Order { Status = "B" });
        await ctx.SaveChangesAsync();

        AuditCapture.From(ctx).Should().HaveLoggedExactly(2).Of<Order>();
        Assert.Throws<OrionAuditAssertionException>(() =>
            AuditCapture.From(ctx).Should().HaveLoggedExactly(5).Of<Order>());
    }

    [Fact]
    public void InMemoryAuditUserResolver_ReturnsConfiguredUser()
    {
        var resolver = new InMemoryAuditUserResolver(new AuditUser("u-1", "Alice"));
        var user = resolver.Resolve(null!);
        Assert.NotNull(user);
        Assert.Equal("u-1", user.Id);
    }

    [Fact]
    public void InMemoryAuditTenantResolver_ReturnsConfiguredTenant()
    {
        var resolver = new InMemoryAuditTenantResolver("tenant-x");
        Assert.Equal("tenant-x", resolver.Resolve(null!));
    }
}
