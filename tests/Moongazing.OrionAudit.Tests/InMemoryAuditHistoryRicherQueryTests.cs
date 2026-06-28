using System.Text.Json.Nodes;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Store;
using Moongazing.OrionAudit.Testing;

namespace Moongazing.OrionAudit.Tests;

/// <summary>
/// v0.11.0 richer filters and aggregations on the in-memory store. The in-memory store is the test
/// double consumers prototype against, so it must mirror the EF Core store's filter / sort / group
/// semantics exactly. Mirrors the assertions in <see cref="AuditHistoryRicherQueryTests"/>.
/// </summary>
public class InMemoryAuditHistoryRicherQueryTests
{
    private const string OrderType = "Acme.Order, Acme";
    private const string CustomerType = "Acme.Customer, Acme";
    private const string InvoiceType = "Acme.Invoice, Acme";

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Guid SeqId(byte sequence)
    {
        var bytes = new byte[16];
        bytes[15] = sequence;
        return new Guid(bytes);
    }

    private static string Replace(string path, object value)
        => new JsonArray { new JsonObject { ["op"] = "replace", ["path"] = path, ["value"] = JsonValue.Create(value) } }
            .ToJsonString();

    private static AuditLog Row(byte seq, string entityType, string entityId, AuditAction action, DateTime when,
        string? userId = null, string? userType = null, string? tenantId = null, string? correlationId = null, string? diff = null)
        => new()
        {
            Id = SeqId(seq),
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            OccurredOnUtc = when,
            UserId = userId,
            UserType = userType,
            TenantId = tenantId,
            CorrelationId = correlationId,
            Diff = diff ?? "[]",
        };

    private static InMemoryAuditHistoryStore SeededStore() => new(new[]
    {
        Row(1, OrderType, "o1", AuditAction.Inserted, T0, userId: "alice", userType: "user", tenantId: "t1", correlationId: "corr-A"),
        Row(2, OrderType, "o1", AuditAction.Updated, T0.AddHours(1), userId: "bob", userType: "user", tenantId: "t1", correlationId: "corr-A", diff: Replace("/status", "shipped")),
        Row(3, OrderType, "o1", AuditAction.Updated, T0.AddDays(1), userId: "alice", userType: "user", tenantId: "t1", correlationId: "corr-B", diff: Replace("/total", 42)),
        Row(4, OrderType, "o2", AuditAction.Deleted, T0.AddDays(1).AddHours(2), userId: "system", userType: "system", tenantId: "t1", correlationId: "corr-B"),
        Row(5, CustomerType, "c1", AuditAction.Inserted, T0.AddDays(2), userId: "alice", userType: "user", tenantId: "t2", correlationId: "corr-C",
            diff: new JsonArray { new JsonObject { ["op"] = "replace", ["path"] = "/address/city", ["value"] = "Rome" } }.ToJsonString()),
        Row(6, CustomerType, "c1", AuditAction.Updated, T0.AddDays(2).AddHours(3), userId: "bob", userType: "user", tenantId: "t2", correlationId: "corr-C", diff: Replace("/status", "active")),
        Row(7, InvoiceType, "i1", AuditAction.Inserted, T0.AddDays(2).AddHours(5), userId: "system", userType: "job", tenantId: "t2", correlationId: "corr-D"),
    });

    [Fact]
    public async Task EntityTypesAndActionsSets_Match()
    {
        var store = SeededStore();

        var byTypes = await store.QueryAsync(new AuditHistoryQuery { EntityTypes = new[] { OrderType, InvoiceType }, Take = 100 });
        Assert.Equal(5, byTypes.TotalCount);

        var byActions = await store.QueryAsync(new AuditHistoryQuery { Actions = new[] { AuditAction.Inserted, AuditAction.Deleted }, Take = 100 });
        Assert.Equal(4, byActions.TotalCount);
    }

    [Fact]
    public async Task CorrelationUserTypeChangedPath_Match()
    {
        var store = SeededStore();

        Assert.Equal(2, (await store.QueryAsync(new AuditHistoryQuery { CorrelationId = "corr-B", Take = 100 })).TotalCount);
        Assert.Equal(1, (await store.QueryAsync(new AuditHistoryQuery { UserType = "job", Take = 100 })).TotalCount);
        Assert.Equal(2, (await store.QueryAsync(new AuditHistoryQuery { ChangedPath = "/status", Take = 100 })).TotalCount);
        Assert.Equal(1, (await store.QueryAsync(new AuditHistoryQuery { ChangedPath = "/address", Take = 100 })).TotalCount);
        Assert.Equal(0, (await store.QueryAsync(new AuditHistoryQuery { ChangedPath = "/missing", Take = 100 })).TotalCount);
    }

    [Fact]
    public async Task Aggregate_ByAction_MatchesEfCoreSemantics()
    {
        var store = SeededStore();
        var buckets = await store.AggregateAsync(new AuditAggregationQuery { GroupBy = AuditAggregateBy.Action });
        var byKey = buckets.ToDictionary(b => b.Key!, b => b.Count, StringComparer.Ordinal);

        Assert.Equal(3, byKey[nameof(AuditAction.Inserted)]);
        Assert.Equal(3, byKey[nameof(AuditAction.Updated)]);
        Assert.Equal(1, byKey[nameof(AuditAction.Deleted)]);
        Assert.Equal(7, buckets.Sum(b => b.Count));
    }

    [Fact]
    public async Task Aggregate_ByDayBucket_CountsPerCalendarDay()
    {
        var store = SeededStore();
        var buckets = await store.AggregateAsync(new AuditAggregationQuery
        {
            GroupBy = AuditAggregateBy.TimeBucket,
            TimeBucket = AuditTimeBucket.Day,
        });
        var byDay = buckets.ToDictionary(b => b.BucketStartUtc!.Value, b => b.Count);

        Assert.Equal(3, byDay.Count);
        Assert.Equal(2, byDay[new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)]);
        Assert.Equal(2, byDay[new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)]);
        Assert.Equal(3, byDay[new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)]);
    }

    [Fact]
    public async Task Aggregate_NullUserAndTenant_FoldIntoOneBucket()
    {
        var store = new InMemoryAuditHistoryStore(new[]
        {
            Row(1, OrderType, "o1", AuditAction.Inserted, T0), // null user, null tenant
            Row(2, OrderType, "o1", AuditAction.Updated, T0.AddHours(1)), // null user, null tenant
            Row(3, OrderType, "o2", AuditAction.Inserted, T0.AddHours(2), userId: "alice", tenantId: "t1"),
        });

        var byUser = await store.AggregateAsync(new AuditAggregationQuery { GroupBy = AuditAggregateBy.UserId });
        Assert.Equal(2, byUser.Single(b => b.Key is null).Count);
        Assert.Equal(1, byUser.Single(b => b.Key == "alice").Count);
    }

    [Fact]
    public async Task Validate_RejectsChangedPathWithoutLeadingSlash()
    {
        var store = SeededStore();
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => store.QueryAsync(new AuditHistoryQuery { ChangedPath = "status" }));
        Assert.Contains("ChangedPath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptySetFilters_AreTreatedAsUnfiltered()
    {
        var store = SeededStore();
        // An empty (non-null) set must NOT filter everything out; it leaves the dimension open.
        var page = await store.QueryAsync(new AuditHistoryQuery
        {
            EntityTypes = Array.Empty<string>(),
            Actions = Array.Empty<AuditAction>(),
            Take = 100,
        });
        Assert.Equal(7, page.TotalCount);
    }
}
