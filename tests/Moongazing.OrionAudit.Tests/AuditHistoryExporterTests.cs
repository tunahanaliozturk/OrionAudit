using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Store;

namespace Moongazing.OrionAudit.Tests;

/// <summary>
/// Coverage of <see cref="AuditHistoryExporter"/>: correct NDJSON for a known, filtered dataset; an
/// empty result writes nothing; a large paged result streams without buffering everything. Rows use a
/// deterministic <see cref="SeqId"/> and fixed timestamps so the export order is stable and never
/// depends on random Guid tie-breaks.
/// </summary>
public class AuditHistoryExporterTests
{
    public sealed class Order
    {
        public int V { get; set; }
    }

    private static readonly string OrderType = typeof(Order).AssemblyQualifiedName!;

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

    private static Guid SeqId(byte sequence)
    {
        var bytes = new byte[16];
        bytes[15] = sequence;
        return new Guid(bytes);
    }

    private static List<JsonElement> ParseNdjson(string text)
    {
        var result = new List<JsonElement>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            result.Add(doc.RootElement.Clone());
        }
        return result;
    }

    [Fact]
    public async Task Export_WritesFilteredRowsAsValidNdjson_InOldestFirstOrder()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Two entities; we will filter to e1 only.
        ctx.AuditLogs.AddRange(
            new AuditLog { Id = SeqId(1), EntityType = OrderType, EntityId = "e1", Action = AuditAction.Inserted, OccurredOnUtc = t0, UserId = "alice", Diff = "[]", Snapshot = "{\"V\":1}" },
            new AuditLog { Id = SeqId(2), EntityType = OrderType, EntityId = "e1", Action = AuditAction.Updated, OccurredOnUtc = t0.AddMinutes(1), UserId = "bob", Diff = "[{\"op\":\"replace\",\"path\":\"/V\",\"value\":2}]" },
            new AuditLog { Id = SeqId(3), EntityType = OrderType, EntityId = "e2", Action = AuditAction.Inserted, OccurredOnUtc = t0.AddMinutes(2), UserId = "carol", Diff = "[]" });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var exporter = new AuditHistoryExporter(new EfCoreAuditHistoryStore(ctx));
        using var ms = new MemoryStream();
        var written = await exporter.ExportNdjsonAsync(
            ms, new AuditHistoryQuery { EntityId = "e1" });

        Assert.Equal(2, written);
        var text = Encoding.UTF8.GetString(ms.ToArray());
        Assert.EndsWith("\n", text, StringComparison.Ordinal);

        var lines = ParseNdjson(text);
        Assert.Equal(2, lines.Count);

        // Oldest-first: row 1 then row 2.
        Assert.Equal(SeqId(1).ToString(), lines[0].GetProperty("id").GetString());
        Assert.Equal("e1", lines[0].GetProperty("entityId").GetString());
        Assert.Equal("Inserted", lines[0].GetProperty("action").GetString());
        Assert.Equal("alice", lines[0].GetProperty("userId").GetString());

        // Snapshot embedded as RAW JSON (an object), not a quoted string.
        Assert.Equal(JsonValueKind.Object, lines[0].GetProperty("snapshot").ValueKind);
        Assert.Equal(1, lines[0].GetProperty("snapshot").GetProperty("V").GetInt32());

        // Diff embedded as a raw JSON array.
        Assert.Equal(JsonValueKind.Array, lines[1].GetProperty("diff").ValueKind);
        Assert.Equal("replace", lines[1].GetProperty("diff")[0].GetProperty("op").GetString());

        // Filter excluded e2.
        Assert.DoesNotContain(lines, l => l.GetProperty("entityId").GetString() == "e2");
    }

    [Fact]
    public async Task Export_EmptyResult_WritesNothing()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        var exporter = new AuditHistoryExporter(new EfCoreAuditHistoryStore(ctx));
        using var ms = new MemoryStream();
        var written = await exporter.ExportNdjsonAsync(
            ms, new AuditHistoryQuery { EntityId = "does-not-exist" });

        Assert.Equal(0, written);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public async Task Export_LargePagedResult_StreamsEveryRow_WithoutBufferingAll()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const int total = 250;
        for (var i = 0; i < total; i++)
        {
            ctx.AuditLogs.Add(new AuditLog
            {
                Id = SeqId((byte)(i + 1 > 255 ? 255 : i + 1)),
                EntityType = OrderType,
                EntityId = "big",
                Action = i == 0 ? AuditAction.Inserted : AuditAction.Updated,
                OccurredOnUtc = t0.AddSeconds(i),
                Diff = i == 0 ? "[]" : $"[{{\"op\":\"replace\",\"path\":\"/V\",\"value\":{i}}}]",
            });
        }
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var exporter = new AuditHistoryExporter(new EfCoreAuditHistoryStore(ctx));
        using var ms = new MemoryStream();
        // Page size well below total so the export must walk multiple pages.
        var written = await exporter.ExportNdjsonAsync(
            ms, new AuditHistoryQuery { EntityId = "big" }, pageSize: 40);

        Assert.Equal(total, written);
        var lines = ParseNdjson(Encoding.UTF8.GetString(ms.ToArray()));
        Assert.Equal(total, lines.Count);

        // Order is oldest-first by OccurredOnUtc: the first line is the Insert, last is the final update.
        Assert.Equal("Inserted", lines[0].GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Array, lines[^1].GetProperty("diff").ValueKind);
    }

    [Fact]
    public async Task StreamRows_NeverHoldsMoreThanPageSizeAtOnce()
    {
        // A store double that asserts each requested Take never exceeds the configured page size and
        // that the exporter advances Skip page-by-page rather than asking for everything at once.
        var store = new RecordingStore(totalRows: 130);
        var exporter = new AuditHistoryExporter(store);

        var seen = 0;
        await foreach (var _ in exporter.StreamRowsAsync(new AuditHistoryQuery { EntityId = "x" }, pageSize: 25))
        {
            seen++;
        }

        Assert.Equal(130, seen);
        Assert.All(store.RequestedTakes, take => Assert.Equal(25, take));
        // 130 rows / 25 per page = 6 pages (last page short -> no extra empty round-trip).
        Assert.Equal(6, store.RequestedTakes.Count);
        Assert.Equal(new[] { 0, 25, 50, 75, 100, 125 }, store.RequestedSkips);
    }

    // Minimal IAuditHistoryStore that hands out synthetic rows in pages, recording the paging it was
    // asked for so the test can assert the exporter never requests more than one page at a time.
    private sealed class RecordingStore : AuditHistoryStoreBase
    {
        private readonly int totalRows;
        public List<int> RequestedTakes { get; } = new();
        public List<int> RequestedSkips { get; } = new();

        public RecordingStore(int totalRows) => this.totalRows = totalRows;

        public override Task<AuditHistoryPage> QueryAsync(AuditHistoryQuery query, CancellationToken cancellationToken = default)
        {
            query.Validate();
            RequestedTakes.Add(query.EffectiveTake);
            RequestedSkips.Add(query.Skip);

            var items = new List<AuditLog>();
            for (var i = query.Skip; i < Math.Min(query.Skip + query.EffectiveTake, totalRows); i++)
            {
                items.Add(new AuditLog
                {
                    EntityType = "T",
                    EntityId = "x",
                    Action = AuditAction.Updated,
                    OccurredOnUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
                    Diff = "[]",
                });
            }
            return Task.FromResult(new AuditHistoryPage(items, totalRows, query.Skip, query.EffectiveTake));
        }
    }
}
