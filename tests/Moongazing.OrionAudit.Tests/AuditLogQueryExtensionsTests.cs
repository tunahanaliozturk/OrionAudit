namespace Moongazing.OrionAudit.Tests;

using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Xunit;

public sealed class AuditLogQueryExtensionsTests : IAsyncLifetime
{
    private sealed class QueryDbContext : DbContext
    {
        public QueryDbContext(DbContextOptions<QueryDbContext> options) : base(options) { }
        public DbSet<AuditLog> Logs => Set<AuditLog>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
    }

    private QueryDbContext db = default!;
    private SqliteConnection connection = default!;

    public async ValueTask InitializeAsync()
    {
        // SQLite in-memory rather than EF Core InMemory: InMemoryProvider does not
        // translate Contains(string[]) or GroupBy(...).Select(record), but a real
        // relational provider does, so the helpers should be exercised on a relational
        // surface.
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<QueryDbContext>()
            .UseSqlite(connection)
            .Options;
        db = new QueryDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        db.Dispose();
        await connection.DisposeAsync();
    }

    private async Task SeedAsync(params AuditLog[] rows)
    {
        db.Logs.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static AuditLog Row(
        DateTime occurred,
        string entityType = "Demo.User",
        string? userId = "u-1",
        string userType = "user",
        string? tenant = null,
        string? correlation = null,
        AuditAction action = AuditAction.Inserted) => new()
    {
        EntityType = entityType,
        EntityId = "1",
        Action = action,
        OccurredOnUtc = occurred,
        UserId = userId,
        UserType = userType,
        TenantId = tenant,
        CorrelationId = correlation,
        Diff = "{}",
        Snapshot = null,
    };

    [Fact]
    public async Task BetweenDates_filters_by_inclusive_OccurredOnUtc_window()
    {
        var anchor = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            Row(anchor.AddDays(-2)),  // outside, before
            Row(anchor.AddDays(-1)),  // inside
            Row(anchor),              // inside (boundary)
            Row(anchor.AddDays(1)),   // outside, after
            Row(anchor.AddDays(2)));  // outside

        var matched = await db.Logs.BetweenDates(anchor.AddDays(-1), anchor).ToListAsync();

        Assert.Equal(2, matched.Count);
    }

    [Fact]
    public async Task BetweenDates_rejects_inverted_range()
    {
        Assert.Throws<ArgumentException>(
            () => db.Logs.BetweenDates(DateTime.UtcNow, DateTime.UtcNow.AddDays(-1)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WithinLast_includes_rows_within_window()
    {
        await SeedAsync(
            Row(DateTime.UtcNow.AddMinutes(-30)),  // inside 1h
            Row(DateTime.UtcNow.AddHours(-2)));    // outside 1h

        var matched = await db.Logs.WithinLast(TimeSpan.FromHours(1)).ToListAsync();

        Assert.Single(matched);
    }

    [Fact]
    public async Task WithinLast_rejects_non_positive_window()
    {
        Assert.Throws<ArgumentException>(() => db.Logs.WithinLast(TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => db.Logs.WithinLast(TimeSpan.FromSeconds(-1)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ByUser_filters_by_user_id()
    {
        await SeedAsync(
            Row(DateTime.UtcNow, userId: "u-1"),
            Row(DateTime.UtcNow, userId: "u-2"));

        var matched = await db.Logs.ByUser("u-1").ToListAsync();

        Assert.Single(matched);
        Assert.Equal("u-1", matched[0].UserId);
    }

    [Fact]
    public async Task ByUsers_filters_by_any_of_supplied_ids()
    {
        await SeedAsync(
            Row(DateTime.UtcNow, userId: "u-1"),
            Row(DateTime.UtcNow, userId: "u-2"),
            Row(DateTime.UtcNow, userId: "u-3"));

        var matched = await db.Logs.ByUsers(new[] { "u-1", "u-3" }).ToListAsync();

        Assert.Equal(2, matched.Count);
    }

    [Fact]
    public async Task ByUserType_filters_on_classification()
    {
        await SeedAsync(
            Row(DateTime.UtcNow, userType: "user"),
            Row(DateTime.UtcNow, userType: "system"));

        var systemRows = await db.Logs.ByUserType("system").ToListAsync();

        Assert.Single(systemRows);
        Assert.Equal("system", systemRows[0].UserType);
    }

    [Fact]
    public async Task ByTenant_filters_explicitly_regardless_of_resolver()
    {
        await SeedAsync(
            Row(DateTime.UtcNow, tenant: "acme"),
            Row(DateTime.UtcNow, tenant: "globex"));

        var acme = await db.Logs.ByTenant("acme").ToListAsync();

        Assert.Single(acme);
        Assert.Equal("acme", acme[0].TenantId);
    }

    [Fact]
    public async Task ByAction_filters_on_AuditAction()
    {
        await SeedAsync(
            Row(DateTime.UtcNow, action: AuditAction.Inserted),
            Row(DateTime.UtcNow, action: AuditAction.Updated),
            Row(DateTime.UtcNow, action: AuditAction.Deleted));

        var deletes = await db.Logs.ByAction(AuditAction.Deleted).ToListAsync();

        Assert.Single(deletes);
    }

    [Fact]
    public async Task ByCorrelation_filters_on_correlation_id()
    {
        await SeedAsync(
            Row(DateTime.UtcNow, correlation: "c-1"),
            Row(DateTime.UtcNow, correlation: "c-2"));

        var matched = await db.Logs.ByCorrelation("c-1").ToListAsync();

        Assert.Single(matched);
    }

    [Fact]
    public async Task Newest_orders_descending_by_OccurredOnUtc()
    {
        await SeedAsync(
            Row(DateTime.UtcNow.AddMinutes(-30)),
            Row(DateTime.UtcNow.AddMinutes(-10)),
            Row(DateTime.UtcNow.AddMinutes(-20)));

        var ordered = await db.Logs.Newest().ToListAsync();

        for (var i = 0; i < ordered.Count - 1; i++)
        {
            Assert.True(ordered[i].OccurredOnUtc >= ordered[i + 1].OccurredOnUtc);
        }
    }

    [Fact]
    public async Task DistinctUserIds_returns_unique_non_null_user_ids()
    {
        await SeedAsync(
            Row(DateTime.UtcNow, userId: "u-1"),
            Row(DateTime.UtcNow, userId: "u-1"),
            Row(DateTime.UtcNow, userId: "u-2"),
            Row(DateTime.UtcNow, userId: null));

        var ids = await db.Logs.DistinctUserIds().ToListAsync();

        Assert.Equal(2, ids.Count);
        Assert.Contains("u-1", ids);
        Assert.Contains("u-2", ids);
    }

    [Fact]
    public async Task TopActorsByCount_returns_users_ordered_by_activity()
    {
        await SeedAsync(
            Row(DateTime.UtcNow, userId: "u-1"),
            Row(DateTime.UtcNow, userId: "u-1"),
            Row(DateTime.UtcNow, userId: "u-1"),
            Row(DateTime.UtcNow, userId: "u-2"),
            Row(DateTime.UtcNow, userId: "u-2"),
            Row(DateTime.UtcNow, userId: "u-3"));

        var top2 = await db.Logs.TopActorsByCount(2).ToListAsync();

        Assert.Equal(2, top2.Count);
        Assert.Equal("u-1", top2[0].UserId);
        Assert.Equal(3, top2[0].ActivityCount);
        Assert.Equal("u-2", top2[1].UserId);
        Assert.Equal(2, top2[1].ActivityCount);
    }

    [Fact]
    public async Task TopActorsByCount_rejects_non_positive_top()
    {
        Assert.Throws<ArgumentException>(() => db.Logs.TopActorsByCount(0));
        Assert.Throws<ArgumentException>(() => db.Logs.TopActorsByCount(-1));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Matching_composes_a_free_form_predicate()
    {
        await SeedAsync(
            Row(DateTime.UtcNow, entityType: "Demo.User"),
            Row(DateTime.UtcNow, entityType: "Demo.Order"));

        var orderOnly = await db.Logs.Matching(a => a.EntityType == "Demo.Order").ToListAsync();

        Assert.Single(orderOnly);
    }
}
