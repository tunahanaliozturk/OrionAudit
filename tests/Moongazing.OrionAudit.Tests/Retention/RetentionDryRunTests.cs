namespace Moongazing.OrionAudit.Tests.Retention;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Retention;
using Xunit;

public sealed class RetentionDryRunTests : IAsyncLifetime
{
    private sealed class DryRunDbContext : DbContext
    {
        public DryRunDbContext(DbContextOptions<DryRunDbContext> options) : base(options) { }
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
        collection.AddDbContext<DryRunDbContext>(o => o.UseSqlite(connection));
        services = collection.BuildServiceProvider();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DryRunDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        await connection.DisposeAsync();
    }

    private static AuditLog Row(DateTime occurred) => new()
    {
        EntityType = "Demo.User",
        EntityId = "1",
        Action = AuditAction.Inserted,
        OccurredOnUtc = occurred,
        UserId = "u-1",
        UserType = "user",
        Diff = "{}",
        Snapshot = null,
    };

    private async Task SeedAsync(params AuditLog[] rows)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DryRunDbContext>();
        db.Logs.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<int> CountAsync()
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DryRunDbContext>();
        return await db.Logs.CountAsync();
    }

    private AuditRetentionHostedService<DryRunDbContext> NewSvc(
        RetentionSweepOptions opts,
        RetentionPolicy policy)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(),
            policy,
            opts,
            TimeProvider.System,
            NullLogger<AuditRetentionHostedService<DryRunDbContext>>.Instance);

    [Fact]
    public async Task DryRun_returns_eligible_count_without_deleting_rows()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            Row(now.AddDays(-200)),
            Row(now.AddDays(-150)),
            Row(now.AddDays(-30)));

        var sut = NewSvc(
            new RetentionSweepOptions { DryRun = true },
            RetentionPolicy.RetainFor(TimeSpan.FromDays(90)));

        var would = await sut.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, would);
        // Live table is untouched.
        Assert.Equal(3, await CountAsync());
    }

    [Fact]
    public async Task DryRun_returns_zero_when_nothing_eligible()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(Row(now.AddDays(-30)));

        var sut = NewSvc(
            new RetentionSweepOptions { DryRun = true },
            RetentionPolicy.RetainFor(TimeSpan.FromDays(90)));

        var would = await sut.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(0, would);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task DryRun_off_actually_deletes_for_the_same_policy()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            Row(now.AddDays(-200)),
            Row(now.AddDays(-150)));

        var sut = NewSvc(
            new RetentionSweepOptions { DryRun = false },
            RetentionPolicy.RetainFor(TimeSpan.FromDays(90)));

        var deleted = await sut.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task DryRun_evaluates_PerTenant_policies_per_tenant()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            new AuditLog
            {
                EntityType = "Demo.User", EntityId = "1",
                Action = AuditAction.Inserted,
                OccurredOnUtc = now.AddDays(-200),
                TenantId = "tenant-a", UserId = "u", UserType = "user", Diff = "{}",
            },
            new AuditLog
            {
                EntityType = "Demo.User", EntityId = "1",
                Action = AuditAction.Inserted,
                OccurredOnUtc = now.AddDays(-50),
                TenantId = "tenant-b", UserId = "u", UserType = "user", Diff = "{}",
            });

        var policy = RetentionPolicy.PerTenant(
            byTenantId: new Dictionary<string, RetentionPolicy>
            {
                ["tenant-a"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
                ["tenant-b"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
            },
            fallback: RetentionPolicy.None);

        var sut = NewSvc(new RetentionSweepOptions { DryRun = true }, policy);
        var would = await sut.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, would);
        Assert.Equal(2, await CountAsync());
    }
}
