using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.Capture;
using OrionAudit.Configuration;
using OrionAudit.Read;

namespace OrionAudit.Tests;

public class AuditReconstructorTests
{
    [Auditable]
    public sealed class Order
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Status { get; set; } = "New";
        public decimal Total { get; set; }
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

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        var cfg = new AuditConfigurationBuilder().Audit<Order>().Build();
        services.AddSingleton(cfg);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString())
             .AddInterceptors(new AuditSaveChangesInterceptor(sp)));
        services.AddScoped<IAuditReconstructor>(sp =>
            new AuditReconstructor(sp.GetRequiredService<TestContext>()));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ReconstructAsync_ReturnsNull_WhenNoHistoryAtOrBeforeDate()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

        var result = await reconstructor.ReconstructAsync<Order>("nonexistent-id", DateTime.UtcNow);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReconstructAsync_ReturnsInsertedState_WhenOnlyInsertExists()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

        var order = new Order { Status = "Pending", Total = 100 };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        var result = await reconstructor.ReconstructAsync<Order>(order.Id.ToString(), DateTime.UtcNow.AddMinutes(1));
        Assert.NotNull(result);
        Assert.Equal("Pending", result.Status);
        Assert.Equal(100m, result.Total);
    }

    [Fact]
    public async Task ReconstructAsync_ReplaysUpdates_ToProduceLatestState()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

        var order = new Order { Status = "Pending", Total = 100 };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        order.Status = "Shipped";
        order.Total = 110;
        await ctx.SaveChangesAsync();

        var result = await reconstructor.ReconstructAsync<Order>(order.Id.ToString(), DateTime.UtcNow.AddMinutes(1));
        Assert.NotNull(result);
        Assert.Equal("Shipped", result.Status);
        Assert.Equal(110m, result.Total);
    }

    [Fact]
    public async Task ReconstructAsync_ReturnsNull_WhenDeletedBeforeAsOf()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

        var order = new Order { Status = "Pending" };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        ctx.Orders.Remove(order);
        await ctx.SaveChangesAsync();

        var result = await reconstructor.ReconstructAsync<Order>(order.Id.ToString(), DateTime.UtcNow.AddMinutes(1));
        Assert.Null(result);
    }

    [Fact]
    public async Task ReconstructAsync_ReturnsHistoricalState_WhenAsOfBetweenInsertAndUpdate()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

        var order = new Order { Status = "Pending" };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        var afterInsert = DateTime.UtcNow.AddMilliseconds(100);
        await Task.Delay(200);
        order.Status = "Shipped";
        await ctx.SaveChangesAsync();

        var result = await reconstructor.ReconstructAsync<Order>(order.Id.ToString(), afterInsert);
        Assert.NotNull(result);
        Assert.Equal("Pending", result.Status);
    }

    [Fact]
    public async Task ReconstructManyAsync_ReturnsStateForEachId()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

        var a = new Order { Status = "PendingA" };
        var b = new Order { Status = "PendingB" };
        ctx.Orders.AddRange(a, b);
        await ctx.SaveChangesAsync();

        var result = await reconstructor.ReconstructManyAsync<Order>(
            new[] { a.Id.ToString(), b.Id.ToString() },
            DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(2, result.Count);
        Assert.Equal("PendingA", result[a.Id.ToString()]!.Status);
        Assert.Equal("PendingB", result[b.Id.ToString()]!.Status);
    }

    [Fact]
    public async Task ReconstructManyAsync_ReturnsNullForMissingIds()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

        var a = new Order { Status = "Pending" };
        ctx.Orders.Add(a);
        await ctx.SaveChangesAsync();

        var result = await reconstructor.ReconstructManyAsync<Order>(
            new[] { a.Id.ToString(), "missing-id" },
            DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(2, result.Count);
        Assert.NotNull(result[a.Id.ToString()]);
        Assert.Null(result["missing-id"]);
    }

    [Fact]
    public async Task ReconstructManyAsync_EmptyInput_ReturnsEmptyDictionary()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

        var result = await reconstructor.ReconstructManyAsync<Order>(
            Array.Empty<string>(), DateTime.UtcNow);

        Assert.Empty(result);
    }
}
