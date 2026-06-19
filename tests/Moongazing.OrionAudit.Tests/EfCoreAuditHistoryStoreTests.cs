using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Read;
using Moongazing.OrionAudit.Store;

namespace Moongazing.OrionAudit.Tests;

public class EfCoreAuditHistoryStoreTests
{
    private const string OrderType = "Acme.Order, Acme";

    // A real CLR type whose AssemblyQualifiedName is what AuditReconstructor filters on, so a
    // compacted history can be replayed end-to-end through the production reconstructor.
    public sealed class Widget
    {
        public int V { get; set; }
    }

    private static readonly string WidgetType = typeof(Widget).AssemblyQualifiedName!;

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
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite(conn)
            .Options;
        var ctx = new AuditDbContext(options);
        await ctx.Database.EnsureCreatedAsync();
        return (ctx, conn);
    }

    private static AuditLog Row(string entityId, AuditAction action, DateTime occurredOnUtc,
        string? userId = null, string? diff = null, string? snapshot = null)
        => new()
        {
            EntityType = OrderType,
            EntityId = entityId,
            Action = action,
            OccurredOnUtc = occurredOnUtc,
            UserId = userId,
            Diff = diff ?? "[]",
            Snapshot = snapshot,
        };

    [Fact]
    public async Task Query_FiltersAndPagesAgainstRelationalProvider()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ctx.AuditLogs.AddRange(
            Row("o1", AuditAction.Inserted, t0, userId: "alice"),
            Row("o1", AuditAction.Updated, t0.AddHours(1), userId: "bob"),
            Row("o1", AuditAction.Updated, t0.AddHours(2), userId: "alice"),
            Row("o2", AuditAction.Inserted, t0.AddHours(3), userId: "bob"));
        await ctx.SaveChangesAsync();

        var store = new EfCoreAuditHistoryStore(ctx);

        var page = await store.QueryAsync(new AuditHistoryQuery
        {
            EntityType = OrderType,
            EntityId = "o1",
            Order = AuditHistoryOrder.OldestFirst,
            Take = 2,
        });

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.Equal("o1", page.Items[0].EntityId);
        Assert.True(page.Items[0].OccurredOnUtc <= page.Items[1].OccurredOnUtc);

        var byUser = await store.QueryAsync(new AuditHistoryQuery { UserId = "alice" });
        Assert.Equal(2, byUser.TotalCount);
    }

    [Fact]
    public async Task Query_TimeRangeInclusive()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ctx.AuditLogs.AddRange(
            Row("o1", AuditAction.Inserted, t0),
            Row("o1", AuditAction.Updated, t0.AddHours(1)),
            Row("o1", AuditAction.Updated, t0.AddHours(2)));
        await ctx.SaveChangesAsync();

        var store = new EfCoreAuditHistoryStore(ctx);
        var page = await store.QueryAsync(new AuditHistoryQuery
        {
            FromUtc = t0.AddHours(1),
            ToUtc = t0.AddHours(2),
        });

        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task Compact_RemovesFoldedRowsAndPreservesLatestState()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ctx.AuditLogs.Add(Row("e1", AuditAction.Inserted, t0,
            snapshot: new JsonObject { ["v"] = 1 }.ToJsonString()));
        for (var i = 2; i <= 10; i++)
        {
            var diff = new JsonArray { new JsonObject { ["op"] = "replace", ["path"] = "/v", ["value"] = i } }.ToJsonString();
            ctx.AuditLogs.Add(Row("e1", AuditAction.Updated, t0.AddMinutes(i), diff: diff));
        }
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var store = new EfCoreAuditHistoryStore(ctx);
        var result = await store.CompactAsync(new AuditCompactionRequest
        {
            EntityType = OrderType,
            EntityId = "e1",
            RetainTail = 3,
        });

        Assert.True(result.SnapshotWritten);
        Assert.Equal(10, result.RowsBefore);
        Assert.Equal(7, result.RowsRemoved);
        Assert.Equal(4, result.RowsAfter);

        // Verify against the actual persisted rows (fresh read).
        ctx.ChangeTracker.Clear();
        var persisted = await ctx.AuditLogs
            .Where(a => a.EntityId == "e1")
            .OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id)
            .ToListAsync();
        Assert.Equal(4, persisted.Count);

        var snapRow = persisted.First(r => r.Snapshot is not null);
        var state = JsonNode.Parse(snapRow.Snapshot!)!.AsObject();
        foreach (var r in persisted.SkipWhile(r => r != snapRow).Skip(1))
        {
            if (r.Diff is { Length: > 0 } && r.Diff != "[]")
            {
                state = DiffEngine.Apply(state, r.Diff);
            }
        }
        Assert.Equal(10, (int)state["v"]!);
    }

    [Fact]
    public async Task Compact_TenantScoped_OnlyTouchesThatTenant()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Same entity id under two tenants.
        for (var tenant = 1; tenant <= 2; tenant++)
        {
            var snap = new JsonObject { ["v"] = 1 }.ToJsonString();
            ctx.AuditLogs.Add(new AuditLog
            {
                EntityType = OrderType,
                EntityId = "shared",
                Action = AuditAction.Inserted,
                OccurredOnUtc = t0,
                TenantId = $"t{tenant}",
                Diff = "[]",
                Snapshot = snap,
            });
            for (var i = 2; i <= 6; i++)
            {
                ctx.AuditLogs.Add(new AuditLog
                {
                    EntityType = OrderType,
                    EntityId = "shared",
                    Action = AuditAction.Updated,
                    OccurredOnUtc = t0.AddMinutes(i),
                    TenantId = $"t{tenant}",
                    Diff = new JsonArray { new JsonObject { ["op"] = "replace", ["path"] = "/v", ["value"] = i } }.ToJsonString(),
                });
            }
        }
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var store = new EfCoreAuditHistoryStore(ctx);
        await store.CompactAsync(new AuditCompactionRequest
        {
            EntityType = OrderType,
            EntityId = "shared",
            RetainTail = 1,
            TenantId = "t1",
        });

        ctx.ChangeTracker.Clear();
        var t1Count = await ctx.AuditLogs.CountAsync(a => a.TenantId == "t1");
        var t2Count = await ctx.AuditLogs.CountAsync(a => a.TenantId == "t2");
        Assert.Equal(2, t1Count); // snapshot + 1 retained
        Assert.Equal(6, t2Count); // untouched
    }

    [Fact]
    public async Task Compact_InsertUpdateHistory_StaysReconstructableThroughReplay()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        var entityId = Guid.NewGuid().ToString();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        ctx.AuditLogs.Add(new AuditLog
        {
            EntityType = WidgetType,
            EntityId = entityId,
            Action = AuditAction.Inserted,
            OccurredOnUtc = t0,
            Diff = "[]",
            Snapshot = new JsonObject { ["V"] = 1 }.ToJsonString(),
        });
        for (var i = 2; i <= 10; i++)
        {
            ctx.AuditLogs.Add(new AuditLog
            {
                EntityType = WidgetType,
                EntityId = entityId,
                Action = AuditAction.Updated,
                OccurredOnUtc = t0.AddMinutes(i),
                Diff = new JsonArray { new JsonObject { ["op"] = "replace", ["path"] = "/V", ["value"] = i } }.ToJsonString(),
            });
        }
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var store = new EfCoreAuditHistoryStore(ctx);
        await store.CompactAsync(new AuditCompactionRequest
        {
            EntityType = WidgetType,
            EntityId = entityId,
            RetainTail = 3,
        });
        ctx.ChangeTracker.Clear();

        // The compacted base must read as Inserted, otherwise Replay throws "starts with a
        // non-Insert action". Confirm the oldest surviving row is an Inserted snapshot.
        var oldest = await ctx.AuditLogs
            .Where(a => a.EntityId == entityId)
            .OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id)
            .FirstAsync();
        Assert.Equal(AuditAction.Inserted, oldest.Action);
        Assert.NotNull(oldest.Snapshot);

        // Reconstruct through the production reconstructor: the compacted base is accepted and the
        // latest state replays to v == 10.
        var reconstructor = new AuditReconstructor(ctx);
        var widget = await reconstructor.ReconstructAsync<Widget>(entityId, t0.AddMinutes(10));
        Assert.NotNull(widget);
        Assert.Equal(10, widget!.V);
    }

    [Fact]
    public async Task Compact_EqualTimestampBoundaryAndTail_KeepsSnapshotBeforeTail()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        var entityId = Guid.NewGuid().ToString();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Insert + two updates at t0 (boundary), then a retained-tail update at the SAME timestamp
        // t0. With a random snapshot Guid the snapshot could sort after the tail; reusing the
        // boundary Id guarantees it sorts first.
        ctx.AuditLogs.Add(new AuditLog
        {
            EntityType = WidgetType, EntityId = entityId, Action = AuditAction.Inserted,
            OccurredOnUtc = t0, Diff = "[]", Snapshot = new JsonObject { ["V"] = 1 }.ToJsonString(),
        });
        ctx.AuditLogs.Add(new AuditLog
        {
            EntityType = WidgetType, EntityId = entityId, Action = AuditAction.Updated, OccurredOnUtc = t0,
            Diff = new JsonArray { new JsonObject { ["op"] = "replace", ["path"] = "/V", ["value"] = 2 } }.ToJsonString(),
        });
        ctx.AuditLogs.Add(new AuditLog
        {
            EntityType = WidgetType, EntityId = entityId, Action = AuditAction.Updated, OccurredOnUtc = t0,
            Diff = new JsonArray { new JsonObject { ["op"] = "replace", ["path"] = "/V", ["value"] = 3 } }.ToJsonString(),
        });
        // Retained tail row, same timestamp t0.
        ctx.AuditLogs.Add(new AuditLog
        {
            EntityType = WidgetType, EntityId = entityId, Action = AuditAction.Updated, OccurredOnUtc = t0,
            Diff = new JsonArray { new JsonObject { ["op"] = "replace", ["path"] = "/V", ["value"] = 4 } }.ToJsonString(),
        });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var store = new EfCoreAuditHistoryStore(ctx);
        await store.CompactAsync(new AuditCompactionRequest
        {
            EntityType = WidgetType,
            EntityId = entityId,
            RetainTail = 1,
        });
        ctx.ChangeTracker.Clear();

        var ordered = await ctx.AuditLogs
            .Where(a => a.EntityId == entityId)
            .OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id)
            .ToListAsync();

        // Snapshot (the in-place boundary) must be first even though it shares t0 with the tail.
        Assert.Equal(2, ordered.Count);
        Assert.Equal(AuditAction.Inserted, ordered[0].Action);
        Assert.NotNull(ordered[0].Snapshot);

        // And replay (which depends on oldest-first ordering) lands the correct latest state v == 4.
        var reconstructor = new AuditReconstructor(ctx);
        var widget = await reconstructor.ReconstructAsync<Widget>(entityId, t0);
        Assert.NotNull(widget);
        Assert.Equal(4, widget!.V);
    }
}
