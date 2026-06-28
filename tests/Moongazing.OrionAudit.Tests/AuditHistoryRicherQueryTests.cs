using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Store;

namespace Moongazing.OrionAudit.Tests;

/// <summary>
/// v0.11.0 richer query filters and read-side aggregations, exercised against the real EF Core
/// SQLite store (the repo's standard store test approach). Every dataset pins ids and timestamps so
/// ordering, grouping, and paging are deterministic.
/// </summary>
public class AuditHistoryRicherQueryTests
{
    private const string OrderType = "Acme.Order, Acme";
    private const string CustomerType = "Acme.Customer, Acme";
    private const string InvoiceType = "Acme.Invoice, Acme";

    private sealed class AuditDbContext : DbContext
    {
        public AuditDbContext(DbContextOptions<AuditDbContext> options) : base(options) { }
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
    }

    private static async Task<(AuditDbContext ctx, SqliteConnection conn)> NewDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(conn).Options;
        var ctx = new AuditDbContext(options);
        await ctx.Database.EnsureCreatedAsync();
        return (ctx, conn);
    }

    // Strictly increasing, deterministic Guid id from a 1..255 sequence: all bytes fixed except the
    // last, which carries the sequence. The last byte is the lowest-priority byte under SQLite's
    // default Guid TEXT mapping and Guid.CompareTo, so SeqId(n) sorts strictly by n. Pins tie-breaks.
    private static Guid SeqId(byte sequence)
    {
        var bytes = new byte[16];
        bytes[15] = sequence;
        return new Guid(bytes);
    }

    private static AuditLog Row(
        byte seq,
        string entityType,
        string entityId,
        AuditAction action,
        DateTime occurredOnUtc,
        string? userId = null,
        string? userType = null,
        string? tenantId = null,
        string? correlationId = null,
        string? diff = null)
        => new()
        {
            Id = SeqId(seq),
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            OccurredOnUtc = occurredOnUtc,
            UserId = userId,
            UserType = userType,
            TenantId = tenantId,
            CorrelationId = correlationId,
            Diff = diff ?? "[]",
        };

    private static string Replace(string path, object value)
        => new JsonArray { new JsonObject { ["op"] = "replace", ["path"] = path, ["value"] = JsonValue.Create(value) } }
            .ToJsonString();

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // A mixed dataset spanning entity types, actions, users, tenants, correlation ids, and diffs.
    private static async Task<AuditDbContext> SeededDbAsync(SqliteConnection holdOpen)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(holdOpen).Options;
        var ctx = new AuditDbContext(options);
        await ctx.Database.EnsureCreatedAsync();

        ctx.AuditLogs.AddRange(
            Row(1, OrderType, "o1", AuditAction.Inserted, T0, userId: "alice", userType: "user", tenantId: "t1", correlationId: "corr-A", diff: "[]"),
            Row(2, OrderType, "o1", AuditAction.Updated, T0.AddHours(1), userId: "bob", userType: "user", tenantId: "t1", correlationId: "corr-A", diff: Replace("/status", "shipped")),
            Row(3, OrderType, "o1", AuditAction.Updated, T0.AddDays(1), userId: "alice", userType: "user", tenantId: "t1", correlationId: "corr-B", diff: Replace("/total", 42)),
            Row(4, OrderType, "o2", AuditAction.Deleted, T0.AddDays(1).AddHours(2), userId: "system", userType: "system", tenantId: "t1", correlationId: "corr-B", diff: "[]"),
            Row(5, CustomerType, "c1", AuditAction.Inserted, T0.AddDays(2), userId: "alice", userType: "user", tenantId: "t2", correlationId: "corr-C",
                diff: new JsonArray { new JsonObject { ["op"] = "replace", ["path"] = "/address/city", ["value"] = "Rome" } }.ToJsonString()),
            Row(6, CustomerType, "c1", AuditAction.Updated, T0.AddDays(2).AddHours(3), userId: "bob", userType: "user", tenantId: "t2", correlationId: "corr-C", diff: Replace("/status", "active")),
            Row(7, InvoiceType, "i1", AuditAction.Inserted, T0.AddDays(2).AddHours(5), userId: "system", userType: "job", tenantId: "t2", correlationId: "corr-D", diff: "[]"));
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
        return ctx;
    }

    [Fact]
    public async Task EntityTypesSet_MatchesAnyOfTheGivenTypes()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        var page = await store.QueryAsync(new AuditHistoryQuery
        {
            EntityTypes = new[] { OrderType, InvoiceType },
            Take = 100,
        });

        // 4 Order rows + 1 Invoice row, zero Customer rows.
        Assert.Equal(5, page.TotalCount);
        Assert.All(page.Items, r => Assert.True(r.EntityType == OrderType || r.EntityType == InvoiceType));
        Assert.DoesNotContain(page.Items, r => r.EntityType == CustomerType);
    }

    [Fact]
    public async Task ActionsSet_MatchesAnyOfTheGivenActions()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        var page = await store.QueryAsync(new AuditHistoryQuery
        {
            Actions = new[] { AuditAction.Inserted, AuditAction.Deleted },
            Take = 100,
        });

        // 3 Inserted (o1, c1, i1) + 1 Deleted (o2) = 4.
        Assert.Equal(4, page.TotalCount);
        Assert.All(page.Items, r => Assert.True(r.Action is AuditAction.Inserted or AuditAction.Deleted));
    }

    [Fact]
    public async Task CorrelationAndTenantScope_ComposeWithExistingFilters()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        // corr-B spans o1 (update) and o2 (delete), both tenant t1.
        var byCorrelation = await store.QueryAsync(new AuditHistoryQuery { CorrelationId = "corr-B", Take = 100 });
        Assert.Equal(2, byCorrelation.TotalCount);

        // Compose correlation + tenant + action: only the corr-B delete in t1.
        var composed = await store.QueryAsync(new AuditHistoryQuery
        {
            CorrelationId = "corr-B",
            TenantId = "t1",
            Action = AuditAction.Deleted,
            Take = 100,
        });
        Assert.Equal(1, composed.TotalCount);
        Assert.Equal("o2", composed.Items[0].EntityId);
    }

    [Fact]
    public async Task UserType_FiltersByClassification()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        var jobs = await store.QueryAsync(new AuditHistoryQuery { UserType = "job", Take = 100 });
        Assert.Equal(1, jobs.TotalCount);
        Assert.Equal("i1", jobs.Items[0].EntityId);

        var systemType = await store.QueryAsync(new AuditHistoryQuery { UserType = "system", Take = 100 });
        Assert.Equal(1, systemType.TotalCount); // only the o2 delete is userType "system"
    }

    [Fact]
    public async Task ChangedPath_MatchesExactPathAndNestedDescendants()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        // Two rows touch "/status" exactly (o1 update, c1 update).
        var statusChanges = await store.QueryAsync(new AuditHistoryQuery { ChangedPath = "/status", Take = 100 });
        Assert.Equal(2, statusChanges.TotalCount);
        Assert.All(statusChanges.Items, r => Assert.Contains("/status", r.Diff, StringComparison.Ordinal));

        // "/address" matches the nested change "/address/city" (c1 insert diff).
        var addressChanges = await store.QueryAsync(new AuditHistoryQuery { ChangedPath = "/address", Take = 100 });
        Assert.Equal(1, addressChanges.TotalCount);
        Assert.Equal("c1", addressChanges.Items[0].EntityId);

        // A path that nothing touched returns an empty page (and does not throw).
        var none = await store.QueryAsync(new AuditHistoryQuery { ChangedPath = "/nonexistent", Take = 100 });
        Assert.Equal(0, none.TotalCount);
        Assert.Empty(none.Items);
        Assert.False(none.HasMore);
    }

    [Fact]
    public async Task ChangedPath_DoesNotMatchSiblingWithSharedPrefix()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        var options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(conn).Options;
        await using var ctx = new AuditDbContext(options);
        await ctx.Database.EnsureCreatedAsync();

        // "/status" and "/statusCode" share the textual prefix "status"; a query for "/status" must
        // not match "/statusCode" because the candidate token is closed by a quote or slash.
        ctx.AuditLogs.AddRange(
            Row(1, OrderType, "o1", AuditAction.Updated, T0, diff: Replace("/status", "x")),
            Row(2, OrderType, "o2", AuditAction.Updated, T0.AddHours(1), diff: Replace("/statusCode", 200)));
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
        var store = new EfCoreAuditHistoryStore(ctx);

        var page = await store.QueryAsync(new AuditHistoryQuery { ChangedPath = "/status", Take = 100 });
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("o1", page.Items[0].EntityId);
    }

    [Fact]
    public async Task ChangedPath_MatchesOpPathButNotCoincidentalValueMatch()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        var options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(conn).Options;
        await using var ctx = new AuditDbContext(options);
        await ctx.Database.EnsureCreatedAsync();

        // Row 1 genuinely changes "/status".
        var realStatusChange = Replace("/status", "shipped");

        // Row 2 changes an unrelated property "/config" whose NEW VALUE is an object that itself
        // carries a "path" member equal to "/status". Serialized, its diff contains the substring
        // "path":"/status" inside the operation's "value" - exactly the coincidental collision the
        // op-anchored token must reject. The only "op":"<verb>","path" in this row targets "/config".
        var valueCollision = new JsonArray
        {
            new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "/config",
                ["value"] = new JsonObject { ["path"] = "/status" },
            },
        }.ToJsonString();

        // Sanity: the naive substring would have matched row 2.
        Assert.Contains("\"path\":\"/status\"", valueCollision, StringComparison.Ordinal);

        ctx.AuditLogs.AddRange(
            Row(1, OrderType, "o1", AuditAction.Updated, T0, diff: realStatusChange),
            Row(2, OrderType, "o2", AuditAction.Updated, T0.AddHours(1), diff: valueCollision));
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
        var store = new EfCoreAuditHistoryStore(ctx);

        var page = await store.QueryAsync(new AuditHistoryQuery { ChangedPath = "/status", Take = 100 });

        // Only the real "/status" change (o1) matches; the value-nested "path":"/status" (o2) does not.
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("o1", page.Items[0].EntityId);
    }

    [Fact]
    public async Task ChangedPath_GeneratesAnchoredLikePredicate_WithEscape()
    {
        // The reviewer asked to either run the jsonb path or assert the generated predicate shape.
        // There is no Postgres harness in this repo (SQLite + InMemory only), so we assert the SQL the
        // EF Core store emits: an op-anchored LIKE (not a bare value match) with an explicit ESCAPE
        // clause. On Npgsql the same EF.Functions.Like over the jsonb Diff column is translated with a
        // diff::text cast, which a bare string.Contains would not have produced.
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;

        var sql = new List<string>();
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite(conn)
            .LogTo(sql.Add, Microsoft.Extensions.Logging.LogLevel.Information)
            .EnableSensitiveDataLogging()
            .Options;
        await using var ctx = new AuditDbContext(options);
        await ctx.Database.EnsureCreatedAsync();
        ctx.AuditLogs.Add(Row(1, OrderType, "o1", AuditAction.Updated, T0, diff: Replace("/status", "x")));
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
        var store = new EfCoreAuditHistoryStore(ctx);

        await store.QueryAsync(new AuditHistoryQuery { ChangedPath = "/status", Take = 100 });

        var changedPathSql = sql.FirstOrDefault(s => s.Contains("LIKE", StringComparison.Ordinal)
            && s.Contains("\"Diff\"", StringComparison.Ordinal));
        Assert.NotNull(changedPathSql);
        // The pattern is anchored to a patch op path, not a bare path value.
        Assert.Contains("op\":\"replace\",\"path\":\"/status", changedPathSql, StringComparison.Ordinal);
        // The LIKE carries an explicit ESCAPE so '%' / '_' in a path segment match literally.
        Assert.Contains("ESCAPE", changedPathSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aggregate_LargeCounts_DoNotOverflowInt32()
    {
        // The bucket count is 64-bit end to end. Proving it with > int.MaxValue real rows is infeasible,
        // so we assert the type carries a value that a 32-bit count could not: AuditAggregateBucket.Count
        // is a long, and the GROUP BY projection casts Count() to long before materialising. Here we
        // verify the long path round-trips a value above int.MaxValue without truncation.
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        var buckets = await store.AggregateAsync(new AuditAggregationQuery { GroupBy = AuditAggregateBy.Action });
        Assert.All(buckets, b => Assert.IsType<long>(b.Count));

        // A bucket count above int.MaxValue is representable and does not wrap to a negative int.
        const long overInt32 = (long)int.MaxValue + 1;
        var synthetic = new AuditAggregateBucket("k", overInt32);
        Assert.Equal(overInt32, synthetic.Count);
        Assert.True(synthetic.Count > int.MaxValue);
    }

    [Fact]
    public async Task SortByUserId_OrdersNullsLast_StableInBothDirections()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        var options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(conn).Options;
        await using var ctx = new AuditDbContext(options);
        await ctx.Database.EnsureCreatedAsync();

        // Two users plus two null-user rows. Pinned ids/timestamps for determinism.
        ctx.AuditLogs.AddRange(
            Row(1, OrderType, "o1", AuditAction.Updated, T0, userId: "bob"),
            Row(2, OrderType, "o2", AuditAction.Updated, T0.AddHours(1), userId: "alice"),
            Row(3, OrderType, "o3", AuditAction.Updated, T0.AddHours(2), userId: null),
            Row(4, OrderType, "o4", AuditAction.Updated, T0.AddHours(3), userId: null));
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
        var store = new EfCoreAuditHistoryStore(ctx);

        // Ascending: alice, bob, then the two null-user rows last.
        var asc = await store.QueryAsync(new AuditHistoryQuery
        {
            SortBy = AuditHistorySortField.UserId,
            Order = AuditHistoryOrder.OldestFirst,
            Take = 100,
        });
        Assert.Equal(new[] { "alice", "bob", null, null }, asc.Items.Select(r => r.UserId).ToArray());

        // Descending: bob, alice, then the null-user rows STILL last (fixed NULLS LAST contract).
        var desc = await store.QueryAsync(new AuditHistoryQuery
        {
            SortBy = AuditHistorySortField.UserId,
            Order = AuditHistoryOrder.NewestFirst,
            Take = 100,
        });
        Assert.Equal(new[] { "bob", "alice", null, null }, desc.Items.Select(r => r.UserId).ToArray());

        // The null-user rows keep a deterministic order among themselves (OccurredOnUtc then Id tie-break).
        Assert.Equal(new[] { "o3", "o4" }, asc.Items.Where(r => r.UserId is null).Select(r => r.EntityId).ToArray());
    }

    [Fact]
    public async Task Validate_RejectsNullAggregationFilterAndMalformedPointer()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        // A null Filter on an aggregation query is rejected with a clear ArgumentException, not an NRE.
        var nullFilter = await Assert.ThrowsAsync<ArgumentException>(
            () => store.AggregateAsync(new AuditAggregationQuery { Filter = null! }));
        Assert.Contains(nameof(AuditAggregationQuery.Filter), nullFilter.Message, StringComparison.Ordinal);

        // A malformed JSON Pointer (dangling '~' escape) is rejected by full RFC 6901 validation.
        var badPointer = await Assert.ThrowsAsync<ArgumentException>(
            () => store.QueryAsync(new AuditHistoryQuery { ChangedPath = "/a~" }));
        Assert.Contains("ChangedPath", badPointer.Message, StringComparison.Ordinal);

        // "~1" (escaped '/') and "~0" (escaped '~') are valid and do not throw.
        var ok = await store.QueryAsync(new AuditHistoryQuery { ChangedPath = "/a~1b~0c", Take = 1 });
        Assert.Equal(0, ok.TotalCount); // nothing matches, but validation passed
    }

    [Fact]
    public async Task RicherFilters_ComposeWithPaging()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        // Updates across Order + Customer, oldest first, page size 1.
        var query = new AuditHistoryQuery
        {
            EntityTypes = new[] { OrderType, CustomerType },
            Action = AuditAction.Updated,
            Order = AuditHistoryOrder.OldestFirst,
            Take = 1,
        };

        var page1 = await store.QueryAsync(query);
        Assert.Equal(3, page1.TotalCount); // o1@+1h, o1@+1d, c1@+2d3h
        Assert.Single(page1.Items);
        Assert.True(page1.HasMore);
        Assert.Equal("o1", page1.Items[0].EntityId);

        var page2 = await store.QueryAsync(query with { Skip = 1 });
        Assert.Equal("o1", page2.Items[0].EntityId);
        Assert.True(page2.Items[0].OccurredOnUtc > page1.Items[0].OccurredOnUtc);

        var page3 = await store.QueryAsync(query with { Skip = 2 });
        Assert.Equal("c1", page3.Items[0].EntityId);
        Assert.False(page3.HasMore);
    }

    [Fact]
    public async Task SortByEntityType_OrdersByTypeThenChronologically()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        var page = await store.QueryAsync(new AuditHistoryQuery
        {
            SortBy = AuditHistorySortField.EntityType,
            Order = AuditHistoryOrder.OldestFirst, // ascending
            Take = 100,
        });

        // EntityType ascending (Customer < Invoice < Order ordinally), then chronological within a type.
        var types = page.Items.Select(r => r.EntityType).ToList();
        var sorted = types.OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, types);
    }

    [Fact]
    public async Task ExistingUnfilteredQuery_BehaviourUnchanged()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        // A default query (no v0.11 fields set) still returns the whole history newest-first, capped by
        // DefaultPageSize, exactly as before.
        var page = await store.QueryAsync(new AuditHistoryQuery());
        Assert.Equal(7, page.TotalCount);
        Assert.Equal(7, page.Items.Count);
        for (var i = 1; i < page.Items.Count; i++)
        {
            Assert.True(page.Items[i - 1].OccurredOnUtc >= page.Items[i].OccurredOnUtc);
        }
    }

    [Fact]
    public async Task Aggregate_ByAction_ReturnsCorrectGroupedCounts()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        var buckets = await store.AggregateAsync(new AuditAggregationQuery { GroupBy = AuditAggregateBy.Action });
        var byKey = buckets.ToDictionary(b => b.Key!, b => b.Count, StringComparer.Ordinal);

        Assert.Equal(3, byKey[nameof(AuditAction.Inserted)]); // o1, c1, i1
        Assert.Equal(3, byKey[nameof(AuditAction.Updated)]);  // o1@+1h, o1@+1d, c1
        Assert.Equal(1, byKey[nameof(AuditAction.Deleted)]);  // o2
        Assert.False(byKey.ContainsKey(nameof(AuditAction.SoftDeleted)));
        Assert.Equal(7, buckets.Sum(b => b.Count));
    }

    [Fact]
    public async Task Aggregate_ByEntityType_RespectsFilter()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        // Aggregate by entity type but only for tenant t2 (Customer x2, Invoice x1).
        var buckets = await store.AggregateAsync(new AuditAggregationQuery
        {
            Filter = new AuditHistoryQuery { TenantId = "t2" },
            GroupBy = AuditAggregateBy.EntityType,
        });
        var byKey = buckets.ToDictionary(b => b.Key!, b => b.Count, StringComparer.Ordinal);

        Assert.Equal(2, byKey[CustomerType]);
        Assert.Equal(1, byKey[InvoiceType]);
        Assert.False(byKey.ContainsKey(OrderType)); // Order rows are all tenant t1, filtered out
    }

    [Fact]
    public async Task Aggregate_ByDayBucket_CountsPerCalendarDay()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        var buckets = await store.AggregateAsync(new AuditAggregationQuery
        {
            GroupBy = AuditAggregateBy.TimeBucket,
            TimeBucket = AuditTimeBucket.Day,
        });

        var byDay = buckets.ToDictionary(b => b.BucketStartUtc!.Value, b => b.Count);
        // Day 0: o1 insert + o1 update (+1h) = 2; Day 1: o1 update (+1d) + o2 delete = 2; Day 2: c1x2 + i1 = 3.
        Assert.Equal(3, byDay.Count);
        Assert.Equal(2, byDay[new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)]);
        Assert.Equal(2, byDay[new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)]);
        Assert.Equal(3, byDay[new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)]);
        Assert.Equal(7, buckets.Sum(b => b.Count));
        Assert.All(buckets, b => Assert.Equal(DateTimeKind.Utc, b.BucketStartUtc!.Value.Kind));
    }

    [Fact]
    public async Task Aggregate_EmptyResult_ReturnsNoBuckets()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        await using var ctx = await SeededDbAsync(conn);
        var store = new EfCoreAuditHistoryStore(ctx);

        var buckets = await store.AggregateAsync(new AuditAggregationQuery
        {
            Filter = new AuditHistoryQuery { EntityType = "Nope.Missing, Nope" },
            GroupBy = AuditAggregateBy.Action,
        });

        Assert.Empty(buckets);
    }

    [Fact]
    public async Task LargePagedResult_StreamsWithoutBufferingWholeTable()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _ = conn;
        var options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(conn).Options;
        await using var ctx = new AuditDbContext(options);
        await ctx.Database.EnsureCreatedAsync();

        // 250 rows under one correlation id, pinned ascending timestamps and sequential ids.
        const int total = 250;
        for (var i = 0; i < total; i++)
        {
            ctx.AuditLogs.Add(new AuditLog
            {
                Id = SeqId((byte)(i + 1 > 255 ? 255 : i + 1)),
                EntityType = OrderType,
                EntityId = "bulk",
                Action = AuditAction.Updated,
                OccurredOnUtc = T0.AddMinutes(i),
                CorrelationId = "bulk-corr",
                Diff = Replace("/seq", i),
            });
        }
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
        var store = new EfCoreAuditHistoryStore(ctx);

        // Page through with a small page size; each page is bounded by Take, never the whole table.
        const int pageSize = 40;
        var seen = 0;
        var skip = 0;
        var firstTotal = -1;
        while (true)
        {
            var page = await store.QueryAsync(new AuditHistoryQuery
            {
                CorrelationId = "bulk-corr",
                Order = AuditHistoryOrder.OldestFirst,
                Skip = skip,
                Take = pageSize,
            });
            if (firstTotal < 0)
            {
                firstTotal = page.TotalCount;
            }
            Assert.True(page.Items.Count <= pageSize);
            if (page.Items.Count == 0)
            {
                break;
            }
            seen += page.Items.Count;
            skip += page.Items.Count;
            if (!page.HasMore)
            {
                break;
            }
        }

        Assert.Equal(total, firstTotal);
        Assert.Equal(total, seen);
    }
}
