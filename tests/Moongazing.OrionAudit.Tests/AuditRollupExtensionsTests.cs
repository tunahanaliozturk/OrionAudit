namespace Moongazing.OrionAudit.Tests;

using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Xunit;

public sealed class AuditRollupExtensionsTests : IAsyncLifetime
{
    private sealed class RollupDbContext : DbContext
    {
        public RollupDbContext(DbContextOptions<RollupDbContext> options) : base(options) { }
        public DbSet<AuditLog> Logs => Set<AuditLog>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
    }

    private RollupDbContext db = default!;
    private SqliteConnection connection = default!;

    public async ValueTask InitializeAsync()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<RollupDbContext>()
            .UseSqlite(connection)
            .Options;
        db = new RollupDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        db.Dispose();
        await connection.DisposeAsync();
    }

    private static AuditLog Row(
        DateTime occurred,
        AuditAction action = AuditAction.Inserted,
        string? userId = "u-1") => new()
    {
        EntityType = "Demo.User",
        EntityId = "1",
        Action = action,
        OccurredOnUtc = occurred,
        UserId = userId,
        UserType = "user",
        Diff = "{}",
        Snapshot = null,
    };

    private async Task SeedAsync(params AuditLog[] rows)
    {
        db.Logs.AddRange(rows);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task RollupByDay_returns_one_bucket_per_calendar_day_with_count()
    {
        var anchor = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            Row(anchor),                     // day 1
            Row(anchor.AddHours(3)),         // day 1
            Row(anchor.AddDays(1)),          // day 2
            Row(anchor.AddDays(2)),          // day 3
            Row(anchor.AddDays(2)));         // day 3

        var buckets = await db.Logs.RollupByDay().ToListAsync();

        Assert.Equal(3, buckets.Count);
        Assert.Equal(new DateOnly(2026, 6, 1), buckets[0].Day);
        Assert.Equal(2, buckets[0].Count);
        Assert.Equal(1, buckets[1].Count);
        Assert.Equal(2, buckets[2].Count);
    }

    [Fact]
    public async Task RollupByDay_orders_buckets_chronologically_ascending()
    {
        await SeedAsync(
            Row(new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)),
            Row(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            Row(new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc)));

        var buckets = await db.Logs.RollupByDay().ToListAsync();

        for (var i = 0; i < buckets.Count - 1; i++)
        {
            Assert.True(buckets[i].Day < buckets[i + 1].Day);
        }
    }

    [Fact]
    public async Task RollupByMonth_returns_one_bucket_per_calendar_month()
    {
        await SeedAsync(
            Row(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)),
            Row(new DateTime(2026, 1, 28, 0, 0, 0, DateTimeKind.Utc)),
            Row(new DateTime(2026, 2, 14, 0, 0, 0, DateTimeKind.Utc)),
            Row(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));

        var buckets = await db.Logs.RollupByMonth().ToListAsync();

        Assert.Equal(3, buckets.Count);
        Assert.Equal(2026, buckets[0].Year);
        Assert.Equal(1, buckets[0].Month);
        Assert.Equal(2, buckets[0].Count);
        Assert.Equal(2, buckets[1].Month);
        Assert.Equal(6, buckets[2].Month);
    }

    [Fact]
    public async Task RollupByDayAndAction_keys_buckets_by_day_and_action_separately()
    {
        var d1 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            Row(d1, AuditAction.Inserted),
            Row(d1, AuditAction.Inserted),
            Row(d1, AuditAction.Updated),
            Row(d1.AddDays(1), AuditAction.Deleted));

        var buckets = await db.Logs.RollupByDayAndAction().ToListAsync();

        Assert.Equal(3, buckets.Count);
        var d1Inserted = buckets.Single(b => b.Day == new DateOnly(2026, 6, 1) && b.Action == AuditAction.Inserted);
        Assert.Equal(2, d1Inserted.Count);
        var d1Updated = buckets.Single(b => b.Day == new DateOnly(2026, 6, 1) && b.Action == AuditAction.Updated);
        Assert.Equal(1, d1Updated.Count);
        var d2Deleted = buckets.Single(b => b.Day == new DateOnly(2026, 6, 2) && b.Action == AuditAction.Deleted);
        Assert.Equal(1, d2Deleted.Count);
    }

    [Fact]
    public async Task RollupByDayAndUser_returns_top_N_users_per_day()
    {
        var d1 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            Row(d1, userId: "u-1"),
            Row(d1, userId: "u-1"),
            Row(d1, userId: "u-2"),
            Row(d1, userId: "u-2"),
            Row(d1, userId: "u-2"),
            Row(d1, userId: "u-3"));

        // Materialise first so the in-memory rollup overload runs (SQLite cannot translate
        // the SelectMany + Take across a sub-grouping). The async version returns an
        // IEnumerable to make this explicit at the API.
        var rows = await db.Logs.ToListAsync();
        var topPerDay = AuditRollupExtensions.RollupByDayAndUser(rows, topUsersPerDay: 2).ToList();

        Assert.Equal(2, topPerDay.Count);
        Assert.Equal("u-2", topPerDay[0].UserId);
        Assert.Equal(3, topPerDay[0].ActivityCount);
        Assert.Equal("u-1", topPerDay[1].UserId);
        Assert.Equal(2, topPerDay[1].ActivityCount);
    }

    [Fact]
    public void RollupByDayAndUser_rejects_non_positive_topUsersPerDay()
    {
        Assert.Throws<ArgumentException>(() =>
            AuditRollupExtensions.RollupByDayAndUser(Array.Empty<AuditLog>(), 0).ToList());
        Assert.Throws<ArgumentException>(() =>
            AuditRollupExtensions.RollupByDayAndUser(Array.Empty<AuditLog>(), -1).ToList());
    }

    [Fact]
    public async Task Rollup_helpers_reject_null_query()
    {
        Assert.Throws<ArgumentNullException>(() => AuditRollupExtensions.RollupByDay(null!));
        Assert.Throws<ArgumentNullException>(() => AuditRollupExtensions.RollupByMonth(null!));
        Assert.Throws<ArgumentNullException>(() => AuditRollupExtensions.RollupByDayAndAction(null!));
        Assert.Throws<ArgumentNullException>(() => AuditRollupExtensions.RollupByDayAndUser(null!, 1).ToList());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Rollup_composes_with_filter_helpers()
    {
        var d1 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            Row(d1, AuditAction.Inserted),
            Row(d1, AuditAction.Updated),
            Row(d1.AddDays(10), AuditAction.Inserted));

        var insertedOnly = await db.Logs.ByAction(AuditAction.Inserted).RollupByDay().ToListAsync();

        Assert.Equal(2, insertedOnly.Count);
        Assert.All(insertedOnly, b => Assert.Equal(1, b.Count));
    }
}
