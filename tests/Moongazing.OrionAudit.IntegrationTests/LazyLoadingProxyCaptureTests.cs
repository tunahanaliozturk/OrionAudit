using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.IntegrationTests;

/// <summary>
/// Regression cover for capture under <c>UseLazyLoadingProxies()</c>. Every entity loaded from the
/// database is then a Castle proxy (<c>OrderProxy : Order</c>), so resolving the entity's CLR type
/// with <c>entry.Entity.GetType()</c> missed every configuration lookup: the entity looked
/// un-audited (no rows at all) and, where a row did get written, its field rules resolved to
/// nothing and <c>[RedactedAudit]</c> properties were persisted in plaintext. Capture resolves the
/// type from EF metadata (<c>entry.Metadata.ClrType</c>) instead, which is always the declared
/// type.
/// </summary>
public class LazyLoadingProxyCaptureTests : IAsyncLifetime
{
    // Lazy-loading proxies require a non-sealed class with virtual members and an accessible
    // parameterless constructor.
    [Auditable]
    public class Order
    {
        public virtual Guid Id { get; set; } = Guid.NewGuid();
        public virtual string Reference { get; set; } = "";

        [RedactedAudit]
        public virtual string CustomerEmail { get; set; } = "";

        public virtual decimal Total { get; set; }
        public virtual ICollection<OrderLine> Lines { get; set; } = new List<OrderLine>();
    }

    public class OrderLine
    {
        public virtual Guid Id { get; set; } = Guid.NewGuid();
        public virtual Guid OrderId { get; set; }
        public virtual string Sku { get; set; } = "";
    }

    private sealed class ProxyDb : DbContext
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<OrderLine> OrderLines => Set<OrderLine>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public ProxyDb(DbContextOptions<ProxyDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().HasKey(o => o.Id);
            modelBuilder.Entity<OrderLine>().HasKey(l => l.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private const string PlaintextEmail = "vip@example.com";

    private SqliteConnection connection = null!;
    private ServiceProvider provider = null!;
    private Guid orderId;

    public async ValueTask InitializeAsync()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<ProxyDb>(o => o.Audit<Order>());
        services.AddSingleton(connection);
        services.AddDbContext<ProxyDb>((sp, o) => o
            .UseLazyLoadingProxies()
            .UseSqlite(sp.GetRequiredService<SqliteConnection>())
            .UseOrionAudit(sp));
        provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ProxyDb>();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await provider.DisposeAsync();
        await connection.DisposeAsync();
    }

    // Seeds one order. The instance handed to Add() is a plain Order, so the insert row is
    // captured even with the defect present — the proxy only appears once EF materializes the
    // row back out of the database.
    private async Task SeedAsync()
    {
        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ProxyDb>();
        var order = new Order { Reference = "SO-1", CustomerEmail = PlaintextEmail, Total = 10m };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        orderId = order.Id;
    }

    private async Task<List<AuditLog>> ReadLogsAsync()
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ProxyDb>().AuditLogs.ToListAsync();
    }

    [Fact]
    public async Task MaterializedEntityIsAProxy_SoTheTestActuallyExercisesTheDefect()
    {
        await SeedAsync();
        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ProxyDb>();
        var loaded = await ctx.Orders.SingleAsync(o => o.Id == orderId);

        Assert.NotSame(typeof(Order), loaded.GetType());
        Assert.IsAssignableFrom<Order>(loaded);
        Assert.Equal(typeof(Order), ctx.Entry(loaded).Metadata.ClrType);
    }

    [Fact]
    public async Task Update_OnProxiedEntity_IsCaptured_WithTheDeclaredClrType()
    {
        await SeedAsync();

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ProxyDb>();
            var order = await ctx.Orders.SingleAsync(o => o.Id == orderId);
            order.Total = 42m;
            await ctx.SaveChangesAsync();
        }

        var logs = await ReadLogsAsync();
        Assert.Equal(2, logs.Count);
        var updated = Assert.Single(logs, l => l.Action == AuditAction.Updated);

        // The proxy type name must never reach the persisted row: reconstruction and
        // AuditFor<T>() queries both resolve rows by the declared type's AQN.
        Assert.Equal(typeof(Order).AssemblyQualifiedName, updated.EntityType);
        Assert.Contains("Total", updated.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_OnProxiedEntity_StillRedactsTheSensitiveProperty()
    {
        await SeedAsync();

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ProxyDb>();
            var order = await ctx.Orders.SingleAsync(o => o.Id == orderId);
            ctx.Orders.Remove(order);
            await ctx.SaveChangesAsync();
        }

        var logs = await ReadLogsAsync();
        var deleted = Assert.Single(logs, l => l.Action == AuditAction.Deleted);
        var snapshot = deleted.Snapshot;
        Assert.NotNull(snapshot);

        // The [RedactedAudit] rule is discovered off the declared type. Resolved off the proxy it
        // came back empty and the customer's address landed in the audit log in the clear.
        // Compared on the parsed value, since System.Text.Json escapes the marker's angle brackets.
        var node = JsonNode.Parse(snapshot);
        Assert.NotNull(node);
        Assert.Equal(SnapshotBuilder.RedactedMarker, (string?)node["CustomerEmail"]);
        Assert.DoesNotContain(PlaintextEmail, snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaintextEmail, deleted.Diff, StringComparison.Ordinal);
    }
}
