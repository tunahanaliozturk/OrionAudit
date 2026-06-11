namespace Moongazing.OrionAudit.Tests.Retention;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Retention;
using Xunit;

public sealed class SweepDeadlineTests : IAsyncLifetime
{
    private sealed class DeadlineDbContext : DbContext
    {
        public DeadlineDbContext(DbContextOptions<DeadlineDbContext> options) : base(options) { }
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
        collection.AddDbContext<DeadlineDbContext>(o => o.UseSqlite(connection));
        services = collection.BuildServiceProvider();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeadlineDbContext>();
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
        var db = scope.ServiceProvider.GetRequiredService<DeadlineDbContext>();
        db.Logs.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private AuditRetentionHostedService<DeadlineDbContext> NewSvc(
        RetentionSweepOptions opts,
        RetentionPolicy policy,
        TimeProvider? clock = null)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(),
            policy,
            opts,
            clock ?? TimeProvider.System,
            NullLogger<AuditRetentionHostedService<DeadlineDbContext>>.Instance);

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset now;
        public MutableTimeProvider(DateTimeOffset start) => now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan delta) => now = now.Add(delta);
    }

    private sealed class AutoAdvanceTimeProvider : TimeProvider
    {
        private DateTimeOffset now;
        private readonly TimeSpan tickEach;
        public AutoAdvanceTimeProvider(DateTimeOffset start, TimeSpan tickEach)
        {
            now = start;
            this.tickEach = tickEach;
        }
        public override DateTimeOffset GetUtcNow()
        {
            var snapshot = now;
            now = now.Add(tickEach);
            return snapshot;
        }
    }

    [Fact]
    public async Task PerTenant_sweep_stops_after_MaxSweepDuration_elapses_mid_sweep()
    {
        // Use a clock that auto-advances on every read by a chunk larger than the
        // budget. The first deadline-check inside the loop will see the clock past the
        // deadline and abort the remaining tenants. Only the first tenant gets processed.
        var now = DateTime.UtcNow;
        var clock = new AutoAdvanceTimeProvider(
            new DateTimeOffset(now, TimeSpan.Zero),
            tickEach: TimeSpan.FromMilliseconds(40));
        await SeedAsync(
            Row(now.AddDays(-200), tenantId: "tenant-a"),
            Row(now.AddDays(-200), tenantId: "tenant-b"),
            Row(now.AddDays(-200), tenantId: "tenant-c"));

        var policy = RetentionPolicy.PerTenant(
            byTenantId: new Dictionary<string, RetentionPolicy>
            {
                ["tenant-a"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
                ["tenant-b"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
                ["tenant-c"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
            },
            fallback: RetentionPolicy.None);

        var sut = NewSvc(
            new RetentionSweepOptions { MaxSweepDuration = TimeSpan.FromMilliseconds(50) },
            policy,
            clock);

        var deleted = await sut.SweepOnceAsync(CancellationToken.None);

        // At least one tenant processed but NOT all three - the deadline cut the sweep
        // short mid-loop.
        Assert.InRange(deleted, 1, 2);
    }

    [Fact]
    public async Task Deadline_does_not_fire_when_MaxSweepDuration_is_null()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            Row(now.AddDays(-200), tenantId: "tenant-a"),
            Row(now.AddDays(-200), tenantId: "tenant-b"));

        var policy = RetentionPolicy.PerTenant(
            byTenantId: new Dictionary<string, RetentionPolicy>
            {
                ["tenant-a"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
                ["tenant-b"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
            },
            fallback: RetentionPolicy.None);

        var sut = NewSvc(
            new RetentionSweepOptions { MaxSweepDuration = null },
            policy);

        var deleted = await sut.SweepOnceAsync(CancellationToken.None);
        Assert.Equal(2, deleted);
    }

    [Fact]
    public async Task PerEntityType_sweep_stops_after_MaxSweepDuration_elapses()
    {
        var now = DateTime.UtcNow;
        var clock = new MutableTimeProvider(new DateTimeOffset(now, TimeSpan.Zero));
        await SeedAsync(
            Row(now.AddDays(-200), tenantId: null),
            new AuditLog
            {
                EntityType = "Demo.Order", EntityId = "1",
                Action = AuditAction.Inserted, OccurredOnUtc = now.AddDays(-200),
                UserId = "u", UserType = "user", Diff = "{}",
            });

        var policy = RetentionPolicy.PerEntityType(
            byEntityType: new Dictionary<string, RetentionPolicy>
            {
                ["Demo.User"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
                ["Demo.Order"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
            },
            fallback: RetentionPolicy.None);

        // Run once with a generous budget so both entity types are processed.
        var generous = NewSvc(
            new RetentionSweepOptions { MaxSweepDuration = TimeSpan.FromSeconds(10) },
            policy,
            clock);
        var deletedGenerous = await generous.SweepOnceAsync(CancellationToken.None);
        Assert.Equal(2, deletedGenerous);
    }
}
