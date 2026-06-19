using System.Text.Json.Nodes;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Store;
using Moongazing.OrionAudit.Testing;

namespace Moongazing.OrionAudit.Tests;

public class InMemoryAuditHistoryStoreTests
{
    private const string OrderType = "Acme.Order, Acme";
    private const string CustomerType = "Acme.Customer, Acme";

    private static AuditLog Row(
        string entityType,
        string entityId,
        AuditAction action,
        DateTime occurredOnUtc,
        string? userId = null,
        string? tenantId = null,
        string? diff = null,
        string? snapshot = null,
        string? baseType = null)
        => new()
        {
            EntityType = entityType,
            EntityBaseType = baseType,
            EntityId = entityId,
            Action = action,
            OccurredOnUtc = occurredOnUtc,
            UserId = userId,
            TenantId = tenantId,
            Diff = diff ?? "[]",
            Snapshot = snapshot,
        };

    private static InMemoryAuditHistoryStore SeededStore()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new InMemoryAuditHistoryStore(new[]
        {
            Row(OrderType, "o1", AuditAction.Inserted, t0, userId: "alice", tenantId: "t1"),
            Row(OrderType, "o1", AuditAction.Updated, t0.AddHours(1), userId: "bob", tenantId: "t1"),
            Row(OrderType, "o1", AuditAction.Updated, t0.AddHours(2), userId: "alice", tenantId: "t1"),
            Row(OrderType, "o2", AuditAction.Inserted, t0.AddHours(3), userId: "bob", tenantId: "t1"),
            Row(OrderType, "o2", AuditAction.Deleted, t0.AddHours(4), userId: "bob", tenantId: "t1"),
            Row(CustomerType, "c1", AuditAction.Inserted, t0.AddHours(5), userId: "alice", tenantId: "t2"),
        });
    }

    [Fact]
    public async Task Query_NoFilters_ReturnsAllNewestFirst()
    {
        var store = SeededStore();

        var page = await store.QueryAsync(new AuditHistoryQuery());

        Assert.Equal(6, page.TotalCount);
        Assert.Equal(6, page.Items.Count);
        // Newest first: the customer insert at +5h leads.
        Assert.Equal(CustomerType, page.Items[0].EntityType);
        Assert.True(page.Items[0].OccurredOnUtc >= page.Items[^1].OccurredOnUtc);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Query_FilterByEntityType_ReturnsOnlyThatType()
    {
        var store = SeededStore();

        var page = await store.QueryAsync(new AuditHistoryQuery { EntityType = OrderType });

        Assert.Equal(5, page.TotalCount);
        Assert.All(page.Items, r => Assert.Equal(OrderType, r.EntityType));
    }

    [Fact]
    public async Task Query_FilterByEntityId_ScopesToSubject()
    {
        var store = SeededStore();

        var page = await store.QueryAsync(new AuditHistoryQuery { EntityType = OrderType, EntityId = "o1" });

        Assert.Equal(3, page.TotalCount);
        Assert.All(page.Items, r => Assert.Equal("o1", r.EntityId));
    }

    [Fact]
    public async Task Query_FilterByAction_ReturnsMatchingActionOnly()
    {
        var store = SeededStore();

        var page = await store.QueryAsync(new AuditHistoryQuery { Action = AuditAction.Updated });

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, r => Assert.Equal(AuditAction.Updated, r.Action));
    }

    [Fact]
    public async Task Query_FilterByUser_ReturnsThatSubjectsRows()
    {
        var store = SeededStore();

        var page = await store.QueryAsync(new AuditHistoryQuery { UserId = "alice" });

        Assert.Equal(3, page.TotalCount);
        Assert.All(page.Items, r => Assert.Equal("alice", r.UserId));
    }

    [Fact]
    public async Task Query_FilterByTenant_Isolates()
    {
        var store = SeededStore();

        var page = await store.QueryAsync(new AuditHistoryQuery { TenantId = "t2" });

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(CustomerType, Assert.Single(page.Items).EntityType);
    }

    [Fact]
    public async Task Query_TimeRange_IsInclusiveOnBothBounds()
    {
        var store = SeededStore();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var page = await store.QueryAsync(new AuditHistoryQuery
        {
            FromUtc = t0.AddHours(1),
            ToUtc = t0.AddHours(3),
        });

        // +1h, +2h, +3h inclusive == 3 rows; the +0h insert and +4h/+5h rows are excluded.
        Assert.Equal(3, page.TotalCount);
        Assert.All(page.Items, r =>
        {
            Assert.True(r.OccurredOnUtc >= t0.AddHours(1));
            Assert.True(r.OccurredOnUtc <= t0.AddHours(3));
        });
    }

    [Fact]
    public async Task Query_Paging_ReturnsDisjointSubsetsAndReportsHasMore()
    {
        var store = SeededStore();
        var q = new AuditHistoryQuery { Order = AuditHistoryOrder.OldestFirst, Take = 2 };

        var page1 = await store.QueryAsync(q);
        var page2 = await store.QueryAsync(q with { Skip = 2 });
        var page3 = await store.QueryAsync(q with { Skip = 4 });

        Assert.Equal(6, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.True(page1.HasMore);
        Assert.True(page2.HasMore);
        Assert.False(page3.HasMore);

        // Pages are disjoint and together cover the whole ordered set.
        var ids = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(r => r.Id).ToList();
        Assert.Equal(6, ids.Distinct().Count());
    }

    [Fact]
    public async Task Query_OldestFirst_OrdersAscending()
    {
        var store = SeededStore();

        var page = await store.QueryAsync(new AuditHistoryQuery { Order = AuditHistoryOrder.OldestFirst });

        for (var i = 1; i < page.Items.Count; i++)
        {
            Assert.True(page.Items[i].OccurredOnUtc >= page.Items[i - 1].OccurredOnUtc);
        }
    }

    [Fact]
    public async Task Query_NoMatch_ReturnsEmptyPage()
    {
        var store = SeededStore();

        var page = await store.QueryAsync(new AuditHistoryQuery { EntityId = "does-not-exist" });

        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Query_InvalidPaging_Throws()
    {
        var store = SeededStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryAsync(new AuditHistoryQuery { Skip = -1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryAsync(new AuditHistoryQuery { Take = 0 }));
    }

    [Fact]
    public async Task Query_InvertedTimeRange_Throws()
    {
        var store = SeededStore();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.QueryAsync(new AuditHistoryQuery { FromUtc = t0.AddHours(5), ToUtc = t0 }));
    }

    // ----- Compaction -----

    // Builds a linear history: Insert {v:1} then Updates v:2..vN, each diff replacing /v.
    private static InMemoryAuditHistoryStore HistoryStore(int updateCount, string entityId = "e1")
    {
        var store = new InMemoryAuditHistoryStore();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        store.Add(Row(OrderType, entityId, AuditAction.Inserted, t0,
            diff: "[]",
            snapshot: new JsonObject { ["v"] = 1 }.ToJsonString()));

        for (var i = 2; i <= updateCount + 1; i++)
        {
            var diff = new JsonArray
            {
                new JsonObject { ["op"] = "replace", ["path"] = "/v", ["value"] = i },
            }.ToJsonString();
            store.Add(Row(OrderType, entityId, AuditAction.Updated, t0.AddMinutes(i), diff: diff));
        }
        return store;
    }

    [Fact]
    public async Task Compact_ReducesHistoryAndRetainsTail()
    {
        // Insert + 9 updates == 10 rows, final v == 10.
        var store = HistoryStore(updateCount: 9);
        Assert.Equal(10, store.Count);

        var result = await store.CompactAsync(new AuditCompactionRequest
        {
            EntityType = OrderType,
            EntityId = "e1",
            RetainTail = 3,
        });

        Assert.True(result.SnapshotWritten);
        Assert.Equal(10, result.RowsBefore);
        // Fold 7 rows into 1 snapshot, keep the 3 most-recent => 4 rows after.
        Assert.Equal(7, result.RowsRemoved);
        Assert.Equal(4, result.RowsAfter);
        Assert.Equal(4, store.Count);
    }

    [Fact]
    public async Task Compact_PreservesLatestReconstructableState()
    {
        var store = HistoryStore(updateCount: 9); // final v == 10
        await store.CompactAsync(new AuditCompactionRequest
        {
            EntityType = OrderType,
            EntityId = "e1",
            RetainTail = 3,
        });

        // Replay the compacted history from the snapshot row through the retained tail.
        var rows = store.Snapshot()
            .OrderBy(r => r.OccurredOnUtc).ThenBy(r => r.Id)
            .ToList();

        var snapRow = rows.First(r => r.Snapshot is not null);
        var state = JsonNode.Parse(snapRow.Snapshot!)!.AsObject();
        foreach (var r in rows.SkipWhile(r => r != snapRow).Skip(1))
        {
            if (r.Diff is { Length: > 0 } && r.Diff != "[]")
            {
                state = Moongazing.OrionAudit.Capture.DiffEngine.Apply(state, r.Diff);
            }
        }

        Assert.Equal(10, (int)state["v"]!);
    }

    [Fact]
    public async Task Compact_RetainTailZero_CollapsesToSingleSnapshot()
    {
        var store = HistoryStore(updateCount: 4); // 5 rows, final v == 5

        var result = await store.CompactAsync(new AuditCompactionRequest
        {
            EntityType = OrderType,
            EntityId = "e1",
            RetainTail = 0,
        });

        Assert.Equal(1, result.RowsAfter);
        Assert.Equal(1, store.Count);

        var only = Assert.Single(store.Snapshot());
        Assert.NotNull(only.Snapshot);
        var state = JsonNode.Parse(only.Snapshot!)!.AsObject();
        Assert.Equal(5, (int)state["v"]!);
    }

    [Fact]
    public async Task Compact_ShortHistory_IsNoOp()
    {
        var store = HistoryStore(updateCount: 2); // 3 rows

        // RetainTail 3 leaves foldCount = 0 -> nothing to fold.
        var result = await store.CompactAsync(new AuditCompactionRequest
        {
            EntityType = OrderType,
            EntityId = "e1",
            RetainTail = 3,
        });

        Assert.False(result.SnapshotWritten);
        Assert.Equal(0, result.RowsRemoved);
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public async Task Compact_OnlyTouchesRequestedEntity()
    {
        var store = HistoryStore(updateCount: 9, entityId: "e1");
        store.AddRange(HistoryStore(updateCount: 9, entityId: "e2").Snapshot());
        Assert.Equal(20, store.Count);

        await store.CompactAsync(new AuditCompactionRequest
        {
            EntityType = OrderType,
            EntityId = "e1",
            RetainTail = 2,
        });

        // e2's 10 rows untouched; e1 collapsed to snapshot + 2 == 3.
        var e2Rows = store.Snapshot().Count(r => r.EntityId == "e2");
        var e1Rows = store.Snapshot().Count(r => r.EntityId == "e1");
        Assert.Equal(10, e2Rows);
        Assert.Equal(3, e1Rows);
    }

    [Fact]
    public async Task Compact_FoldedDelete_StaysTerminal()
    {
        var store = new InMemoryAuditHistoryStore();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        store.Add(Row(OrderType, "e1", AuditAction.Inserted, t0,
            snapshot: new JsonObject { ["v"] = 1 }.ToJsonString()));
        store.Add(Row(OrderType, "e1", AuditAction.Updated, t0.AddMinutes(1),
            diff: new JsonArray { new JsonObject { ["op"] = "replace", ["path"] = "/v", ["value"] = 2 } }.ToJsonString()));
        store.Add(Row(OrderType, "e1", AuditAction.Deleted, t0.AddMinutes(2)));

        var result = await store.CompactAsync(new AuditCompactionRequest
        {
            EntityType = OrderType,
            EntityId = "e1",
            RetainTail = 0,
        });

        Assert.True(result.SnapshotWritten);
        var only = Assert.Single(store.Snapshot());
        Assert.Equal(AuditAction.Deleted, only.Action);
    }

    [Fact]
    public async Task Compact_InvalidRetainTail_Throws()
    {
        var store = HistoryStore(updateCount: 3);

        await Assert.ThrowsAsync<ArgumentException>(() => store.CompactAsync(new AuditCompactionRequest
        {
            EntityType = OrderType,
            EntityId = "e1",
            RetainTail = -1,
        }));
    }
}
