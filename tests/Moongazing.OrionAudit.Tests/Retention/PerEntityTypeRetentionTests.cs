namespace Moongazing.OrionAudit.Tests.Retention;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Retention;
using Xunit;

public sealed class PerEntityTypeRetentionTests : IAsyncLifetime
{
    private sealed class EntityTypeDbContext : DbContext
    {
        public EntityTypeDbContext(DbContextOptions<EntityTypeDbContext> options) : base(options) { }
        public DbSet<AuditLog> Logs => Set<AuditLog>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
    }

    private SqliteConnection connection = default!;
    private ServiceProvider services = default!;

    public async ValueTask InitializeAsync()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var collection = new ServiceCollection();
        collection.AddDbContext<EntityTypeDbContext>(o => o.UseSqlite(connection));
        services = collection.BuildServiceProvider();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EntityTypeDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        await connection.DisposeAsync();
    }

    private static AuditLog Row(DateTime occurred, string entityType, string? tenantId = null) => new()
    {
        EntityType = entityType,
        EntityId = "1",
        Action = AuditAction.Inserted,
        OccurredOnUtc = occurred,
        TenantId = tenantId,
        UserId = "u-1",
        UserType = "user",
        Diff = "{}",
        Snapshot = null,
    };

    private async Task SeedAsync(params AuditLog[] rows)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EntityTypeDbContext>();
        db.Logs.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<int> CountAsync(string entityType, string? tenantId = null)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EntityTypeDbContext>();
        return await db.Logs.CountAsync(a => a.EntityType == entityType && a.TenantId == tenantId);
    }

    private AuditRetentionHostedService<EntityTypeDbContext> NewSvc(RetentionPolicy policy)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(),
            policy,
            new RetentionSweepOptions(),
            TimeProvider.System,
            NullLogger<AuditRetentionHostedService<EntityTypeDbContext>>.Instance);

    [Fact]
    public async Task PerEntityType_applies_distinct_age_windows_per_entity_type()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            Row(now.AddDays(-60), "Demo.User"),
            Row(now.AddDays(-200), "Demo.User"),
            Row(now.AddDays(-60), "Demo.Order"),
            Row(now.AddDays(-200), "Demo.Order"));

        var policy = RetentionPolicy.PerEntityType(
            byEntityType: new Dictionary<string, RetentionPolicy>
            {
                // Users have a 90-day retention -> the -200 day row evicts
                ["Demo.User"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
                // Orders have a 30-day retention -> both rows evict
                ["Demo.Order"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(30)),
            },
            fallback: RetentionPolicy.None);

        var sut = NewSvc(policy);
        await sut.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync("Demo.User"));
        Assert.Equal(0, await CountAsync("Demo.Order"));
    }

    [Fact]
    public async Task PerEntityType_unmapped_entity_falls_back_to_fallback_policy()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            Row(now.AddDays(-200), "Demo.Mapped"),
            Row(now.AddDays(-200), "Demo.Stranger"));

        var policy = RetentionPolicy.PerEntityType(
            byEntityType: new Dictionary<string, RetentionPolicy>
            {
                ["Demo.Mapped"] = RetentionPolicy.None,
            },
            fallback: RetentionPolicy.RetainFor(TimeSpan.FromDays(90)));

        var sut = NewSvc(policy);
        await sut.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync("Demo.Mapped"));
        Assert.Equal(0, await CountAsync("Demo.Stranger"));
    }

    [Fact]
    public async Task PerTenant_with_nested_PerEntityType_applies_per_tenant_per_entity_windows()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            Row(now.AddDays(-200), "Demo.User", tenantId: "tenant-a"),
            Row(now.AddDays(-200), "Demo.Order", tenantId: "tenant-a"),
            Row(now.AddDays(-200), "Demo.User", tenantId: "tenant-b"));

        var tenantAPolicy = RetentionPolicy.PerEntityType(
            byEntityType: new Dictionary<string, RetentionPolicy>
            {
                ["Demo.User"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(365)), // tenant-a users kept
                ["Demo.Order"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(30)), // tenant-a orders evict
            },
            fallback: RetentionPolicy.None);
        var policy = RetentionPolicy.PerTenant(
            byTenantId: new Dictionary<string, RetentionPolicy>
            {
                ["tenant-a"] = tenantAPolicy,
                ["tenant-b"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(30)), // tenant-b everything evicts
            },
            fallback: RetentionPolicy.None);

        var sut = NewSvc(policy);
        await sut.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync("Demo.User", tenantId: "tenant-a"));
        Assert.Equal(0, await CountAsync("Demo.Order", tenantId: "tenant-a"));
        Assert.Equal(0, await CountAsync("Demo.User", tenantId: "tenant-b"));
    }

    [Fact]
    public void PerEntityType_rejects_empty_dictionary()
    {
        Assert.Throws<ArgumentException>(() =>
            RetentionPolicy.PerEntityType(
                new Dictionary<string, RetentionPolicy>(),
                RetentionPolicy.None));
    }

    [Fact]
    public void PerEntityType_rejects_null_policy_values()
    {
        Assert.Throws<ArgumentException>(() =>
            RetentionPolicy.PerEntityType(
                new Dictionary<string, RetentionPolicy> { ["x"] = null! },
                RetentionPolicy.None));
    }

    [Fact]
    public void PerEntityType_rejects_nested_PerTenant_or_PerEntityType()
    {
        var inner = RetentionPolicy.PerEntityType(
            new Dictionary<string, RetentionPolicy> { ["x"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(30)) },
            RetentionPolicy.None);
        var tenant = RetentionPolicy.PerTenant(
            new Dictionary<string, RetentionPolicy> { ["t"] = RetentionPolicy.None },
            RetentionPolicy.None);

        Assert.Throws<ArgumentException>(() =>
            RetentionPolicy.PerEntityType(
                new Dictionary<string, RetentionPolicy> { ["y"] = inner },
                RetentionPolicy.None));
        Assert.Throws<ArgumentException>(() =>
            RetentionPolicy.PerEntityType(
                new Dictionary<string, RetentionPolicy> { ["y"] = tenant },
                RetentionPolicy.None));
    }

    [Fact]
    public void PerEntityType_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RetentionPolicy.PerEntityType(null!, RetentionPolicy.None));
        Assert.Throws<ArgumentNullException>(() =>
            RetentionPolicy.PerEntityType(
                new Dictionary<string, RetentionPolicy> { ["x"] = RetentionPolicy.None },
                fallback: null!));
    }
}
