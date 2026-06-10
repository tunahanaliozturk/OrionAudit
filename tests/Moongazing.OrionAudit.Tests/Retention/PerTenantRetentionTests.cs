namespace Moongazing.OrionAudit.Tests.Retention;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Retention;
using Xunit;

public sealed class PerTenantRetentionTests : IAsyncLifetime
{
    private sealed class TenantDbContext : DbContext
    {
        public TenantDbContext(DbContextOptions<TenantDbContext> options) : base(options) { }
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
        collection.AddDbContext<TenantDbContext>(o => o.UseSqlite(connection));
        services = collection.BuildServiceProvider();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        await connection.DisposeAsync();
    }

    private static AuditLog Row(DateTime occurred, string? tenantId) => new()
    {
        EntityType = "Demo.User",
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
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        db.Logs.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<int> CountAsync(string? tenantId)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        return await db.Logs.CountAsync(a => a.TenantId == tenantId);
    }

    private AuditRetentionHostedService<TenantDbContext> NewSvc(RetentionPolicy policy)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(),
            policy,
            new RetentionSweepOptions(),
            TimeProvider.System,
            NullLogger<AuditRetentionHostedService<TenantDbContext>>.Instance);

    [Fact]
    public async Task PerTenant_evaluates_each_tenants_age_window_independently()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            Row(now.AddDays(-50), tenantId: "tenant-a"),
            Row(now.AddDays(-50), tenantId: "tenant-b"),
            Row(now.AddDays(-200), tenantId: "tenant-a"),
            Row(now.AddDays(-200), tenantId: "tenant-b"));

        var policy = RetentionPolicy.PerTenant(
            byTenantId: new Dictionary<string, RetentionPolicy>
            {
                // tenant-a keeps rows for 30 days -> the -50 day row also evicts
                ["tenant-a"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(30)),
                // tenant-b keeps rows for 100 days -> only the -200 day row evicts
                ["tenant-b"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(100)),
            },
            fallback: RetentionPolicy.None);

        var sut = NewSvc(policy);
        var deleted = await sut.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(3, deleted);
        Assert.Equal(0, await CountAsync("tenant-a"));
        Assert.Equal(1, await CountAsync("tenant-b"));
    }

    [Fact]
    public async Task PerTenant_unmapped_tenant_falls_back_to_fallback_policy()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            Row(now.AddDays(-200), tenantId: "mapped"),
            Row(now.AddDays(-200), tenantId: "stranger"));
        var policy = RetentionPolicy.PerTenant(
            byTenantId: new Dictionary<string, RetentionPolicy>
            {
                ["mapped"] = RetentionPolicy.None,
            },
            fallback: RetentionPolicy.RetainFor(TimeSpan.FromDays(90)));

        var sut = NewSvc(policy);
        await sut.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync("mapped"));
        Assert.Equal(0, await CountAsync("stranger"));
    }

    [Fact]
    public async Task PerTenant_None_policy_for_a_tenant_keeps_all_rows_for_that_tenant()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            Row(now.AddDays(-300), tenantId: "compliance-frozen"),
            Row(now.AddDays(-300), tenantId: "normal"));
        var policy = RetentionPolicy.PerTenant(
            byTenantId: new Dictionary<string, RetentionPolicy>
            {
                ["compliance-frozen"] = RetentionPolicy.None,
                ["normal"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
            },
            fallback: RetentionPolicy.None);

        var sut = NewSvc(policy);
        await sut.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync("compliance-frozen"));
        Assert.Equal(0, await CountAsync("normal"));
    }

    [Fact]
    public void PerTenant_rejects_empty_dictionary()
    {
        Assert.Throws<ArgumentException>(() =>
            RetentionPolicy.PerTenant(
                new Dictionary<string, RetentionPolicy>(),
                RetentionPolicy.None));
    }

    [Fact]
    public void PerTenant_rejects_nested_PerTenant_policy()
    {
        var inner = RetentionPolicy.PerTenant(
            new Dictionary<string, RetentionPolicy>
            {
                ["t"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(30)),
            },
            RetentionPolicy.None);

        Assert.Throws<ArgumentException>(() =>
            RetentionPolicy.PerTenant(
                new Dictionary<string, RetentionPolicy>
                {
                    ["wrapper"] = inner,
                },
                RetentionPolicy.None));
        Assert.Throws<ArgumentException>(() =>
            RetentionPolicy.PerTenant(
                new Dictionary<string, RetentionPolicy>
                {
                    ["x"] = RetentionPolicy.None,
                },
                fallback: inner));
    }

    [Fact]
    public void PerTenant_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RetentionPolicy.PerTenant(null!, RetentionPolicy.None));
        Assert.Throws<ArgumentNullException>(() =>
            RetentionPolicy.PerTenant(
                new Dictionary<string, RetentionPolicy> { ["x"] = RetentionPolicy.None },
                fallback: null!));
    }

    [Fact]
    public async Task PerTenant_snapshot_is_independent_of_caller_dictionary_mutations()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            Row(now.AddDays(-200), tenantId: "tenant-a"));

        var live = new Dictionary<string, RetentionPolicy>
        {
            ["tenant-a"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(30)),
        };
        var policy = RetentionPolicy.PerTenant(live, RetentionPolicy.None);

        // Mutate the caller's dictionary after policy creation - the sweep MUST use the
        // snapshot, not the live reference.
        live["tenant-a"] = RetentionPolicy.None;

        var sut = NewSvc(policy);
        await sut.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(0, await CountAsync("tenant-a"));
    }
}
