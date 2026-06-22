using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.Tests.Integrity;

public class EfCoreAuditIntegrityVerifierTests
{
    private const string OrderType = "Acme.Order, Acme";
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyList<CustomColumn> NoCustomColumns = Array.Empty<CustomColumn>();

    private sealed class AuditDbContext : DbContext
    {
        public AuditDbContext(DbContextOptions<AuditDbContext> options) : base(options) { }

        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditChainAnchor> Anchors => Set<AuditChainAnchor>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyOrionAuditConfigurations();
    }

    // Dedicated context whose model maps a "Severity" custom column. A separate CLR type so EF caches
    // its model independently of AuditDbContext (the model cache key is the context type; a runtime
    // field would NOT change it, so the custom column must be baked into a distinct type).
    private static readonly CustomColumn SeverityColumn = new("Severity", typeof(string), _ => "low");

    private sealed class CustomColumnAuditDbContext : DbContext
    {
        public CustomColumnAuditDbContext(DbContextOptions<CustomColumnAuditDbContext> options) : base(options) { }

        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditChainAnchor> Anchors => Set<AuditChainAnchor>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyOrionAuditConfigurations(customColumns: new[] { SeverityColumn });
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

    private static async Task<(CustomColumnAuditDbContext ctx, SqliteConnection conn)> NewCustomColumnDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<CustomColumnAuditDbContext>().UseSqlite(conn).Options;
        var ctx = new CustomColumnAuditDbContext(options);
        await ctx.Database.EnsureCreatedAsync();
        return (ctx, conn);
    }

    private static Guid SeqId(byte sequence)
    {
        var bytes = new byte[16];
        bytes[15] = sequence;
        return new Guid(bytes);
    }

    private static AuditLog Row(byte seq, string entityId, AuditAction action, string diff, string? tenantId = null)
        => new()
        {
            Id = SeqId(seq),
            EntityType = OrderType,
            EntityId = entityId,
            Action = action,
            OccurredOnUtc = T0.AddMinutes(seq),
            TenantId = tenantId,
            Diff = diff,
        };

    private static EfCoreAuditIntegrityVerifier Verifier(
        AuditDbContext ctx, IReadOnlyList<CustomColumn>? customColumns = null)
        => new(ctx, TestChainKeys.Provider, customColumns ?? NoCustomColumns);

    // Persists a clean, correctly-stamped stream of `count` rows for one entity THROUGH THE PRODUCTION
    // WRITER, so the per-stream anchor is created/advanced exactly as capture would. Returns the rows.
    private static async Task<List<AuditLog>> SeedCleanStreamAsync(
        AuditDbContext ctx, string entityId, byte startSeq, int count, string? tenantId = null)
    {
        var rows = new List<AuditLog>();
        for (var i = 0; i < count; i++)
        {
            var seq = (byte)(startSeq + i);
            var action = i == 0 ? AuditAction.Inserted : AuditAction.Updated;
            var diff = i == 0 ? "[]" : $"[{{\"op\":\"replace\",\"path\":\"/v\",\"value\":{seq}}}]";
            rows.Add(Row(seq, entityId, action, diff, tenantId));
        }
        ctx.AuditLogs.AddRange(rows);
        await EfCoreAuditHashChainWriter.StampAsync(
            ctx, rows, AuditHashChainScope.PerEntityStream, TestChainKeys.Provider, NoCustomColumns, default);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
        return rows;
    }

    [Fact]
    public async Task Verify_CleanPersistedChain_IsValid()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 5);

        var result = await Verifier(ctx).VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));

        Assert.True(result.IsValid);
        Assert.Equal(5, result.VerifiedRowCount);
    }

    [Fact]
    public async Task Seeding_WritesAnchor_WithTailHashAndCount()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        var rows = await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 4);

        var anchor = await ctx.Anchors.SingleAsync();
        Assert.Equal(OrderType, anchor.EntityType);
        Assert.Equal("o1", anchor.EntityId);
        Assert.Equal(string.Empty, anchor.TenantId);
        Assert.Equal(4, anchor.RowCount);
        Assert.Equal(rows[^1].EntryHash, anchor.LatestEntryHash);
        Assert.Equal(TestChainKeys.ActiveKeyId, anchor.KeyId);
    }

    [Fact]
    public async Task Verify_AfterMutatingStoredRow_FailsWithContentMismatch()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        var rows = await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 4);

        var victim = await ctx.AuditLogs.SingleAsync(a => a.Id == rows[2].Id);
        victim.Diff = "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":424242}]";
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var result = await Verifier(ctx).VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.ContentMismatch, result.Reason);
        Assert.Equal(rows[2].Id, result.BrokenAtId);
    }

    [Fact]
    public async Task Verify_AfterDeletingMiddleRow_FailsWithBrokenLink()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        var rows = await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 5);

        var victim = await ctx.AuditLogs.SingleAsync(a => a.Id == rows[2].Id);
        ctx.AuditLogs.Remove(victim);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var result = await Verifier(ctx).VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.BrokenLink, result.Reason);
        // The break is reported at the row that followed the deleted one.
        Assert.Equal(rows[3].Id, result.BrokenAtId);
    }

    [Fact]
    public async Task Verify_AfterDeletingTailRow_FailsWithTruncated()
    {
        // Tail deletion leaves a consistent prefix; only the anchor reveals the missing tail.
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        var rows = await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 5);

        var tail = await ctx.AuditLogs.SingleAsync(a => a.Id == rows[4].Id);
        ctx.AuditLogs.Remove(tail);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var result = await Verifier(ctx).VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.Truncated, result.Reason);
    }

    [Fact]
    public async Task Verify_AfterDeletingWholeStream_FailsWithTruncated()
    {
        // Every row of the stream removed, but the anchor survives: the stream was deleted wholesale.
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 4);

        var all = await ctx.AuditLogs.Where(a => a.EntityId == "o1").ToListAsync();
        ctx.AuditLogs.RemoveRange(all);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        // Whole-table walk picks up the orphaned anchor and flags it.
        var result = await Verifier(ctx).VerifyChainAsync(AuditChainVerificationRequest.All());

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.Truncated, result.Reason);
        Assert.Equal("o1", result.BrokenEntityId);
    }

    [Fact]
    public async Task Verify_CustomColumnTamper_IsDetected()
    {
        // A registered custom column is bound into the MAC, so editing it after capture breaks verify.
        var customColumns = new[] { SeverityColumn };

        var (ctx, conn) = await NewCustomColumnDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        // Seed two rows, stamping with the custom-column value present (so the MAC binds it).
        var rows = new List<AuditLog>
        {
            Row(1, "o1", AuditAction.Inserted, "[]"),
            Row(2, "o1", AuditAction.Updated, "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":2}]"),
        };
        ctx.AuditLogs.AddRange(rows);
        foreach (var r in rows)
        {
            ctx.Entry(r).Property("Severity").CurrentValue = "low";
        }
        await EfCoreAuditHashChainWriter.StampAsync(
            ctx, rows, AuditHashChainScope.PerEntityStream, TestChainKeys.Provider, customColumns, default);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        // Clean verify passes (MAC recomputed with the same custom-column value).
        var cleanVerifier = new EfCoreAuditIntegrityVerifier(ctx, TestChainKeys.Provider, customColumns);
        var clean = await cleanVerifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));
        Assert.True(clean.IsValid);

        // Tamper the custom column out of band.
        var victim = await ctx.AuditLogs.SingleAsync(a => a.Id == rows[1].Id);
        ctx.Entry(victim).Property("Severity").CurrentValue = "critical";
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var tamperVerifier = new EfCoreAuditIntegrityVerifier(ctx, TestChainKeys.Provider, customColumns);
        var tampered = await tamperVerifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));
        Assert.False(tampered.IsValid);
        Assert.Equal(AuditChainBreakReason.ContentMismatch, tampered.Reason);
        Assert.Equal(rows[1].Id, tampered.BrokenAtId);
    }

    [Fact]
    public async Task Verify_WithWrongKey_FailsContentMismatch()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 3);

        // A verifier configured with a different key (same id, different material) cannot reproduce the
        // MAC, so even an untouched chain fails - the integrity guarantee against key-less forgers.
        var wrongVerifier = new EfCoreAuditIntegrityVerifier(ctx, TestChainKeys.WrongKeyProvider, NoCustomColumns);
        var result = await wrongVerifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.ContentMismatch, result.Reason);
    }

    [Fact]
    public async Task Verify_IsIdempotent_OnAValidChain()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 5);

        var verifier = Verifier(ctx);
        var first = await verifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));
        var second = await verifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));

        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Equal(first.VerifiedRowCount, second.VerifiedRowCount);
    }

    [Fact]
    public async Task StampAsync_AppendingInLaterSave_ContinuesPersistedChain_AndAdvancesAnchor()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        var first = await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 3);

        var append = new List<AuditLog>
        {
            Row(4, "o1", AuditAction.Updated, "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":4}]"),
            Row(5, "o1", AuditAction.Updated, "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":5}]"),
        };
        ctx.AuditLogs.AddRange(append);
        await EfCoreAuditHashChainWriter.StampAsync(
            ctx, append, AuditHashChainScope.PerEntityStream, TestChainKeys.Provider, NoCustomColumns, default);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        // The first appended row must chain onto the previously-persisted head.
        Assert.Equal(first[^1].EntryHash, append[0].PreviousHash);

        var anchor = await ctx.Anchors.SingleAsync();
        Assert.Equal(5, anchor.RowCount);
        Assert.Equal(append[^1].EntryHash, anchor.LatestEntryHash);

        var result = await Verifier(ctx).VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));
        Assert.True(result.IsValid);
        Assert.Equal(5, result.VerifiedRowCount);
    }

    [Fact]
    public async Task Verify_AllStreams_DetectsBreakInOneStream()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 3);
        var second = await SeedCleanStreamAsync(ctx, "o2", startSeq: 10, count: 3);

        var victim = await ctx.AuditLogs.SingleAsync(a => a.Id == second[1].Id);
        victim.Diff = "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":-1}]";
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var result = await Verifier(ctx).VerifyChainAsync(AuditChainVerificationRequest.All());

        Assert.False(result.IsValid);
        Assert.Equal("o2", result.BrokenEntityId);
        Assert.Equal(AuditChainBreakReason.ContentMismatch, result.Reason);
    }

    [Fact]
    public async Task Verify_TenantScoped_VerifiesOnlyThatTenantsChain()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        // Same entity id under two tenants, each an independent chain (and its own anchor).
        await SeedCleanStreamAsync(ctx, "shared", startSeq: 1, count: 3, tenantId: "t1");
        var t2 = await SeedCleanStreamAsync(ctx, "shared", startSeq: 10, count: 3, tenantId: "t2");

        // Two anchors, one per tenant.
        Assert.Equal(2, await ctx.Anchors.CountAsync());

        var victim = await ctx.AuditLogs.SingleAsync(a => a.Id == t2[2].Id);
        victim.Diff = "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":777}]";
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var verifier = Verifier(ctx);

        var t1Result = await verifier.VerifyChainAsync(
            AuditChainVerificationRequest.ForEntity(OrderType, "shared", tenantId: "t1"));
        var t2Result = await verifier.VerifyChainAsync(
            AuditChainVerificationRequest.ForEntity(OrderType, "shared", tenantId: "t2"));

        Assert.True(t1Result.IsValid);   // t1 untouched
        Assert.False(t2Result.IsValid);  // t2 corrupted
    }

    [Fact]
    public async Task Verify_TenantScoped_FirstRowOfSecondTenant_IsItsOwnGenesis()
    {
        // Regression for the original "tenant not in chain key" finding: the first row of t2 must be a
        // genesis (null PreviousHash), not chained onto t1's head, so t2 verifies standalone.
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        await SeedCleanStreamAsync(ctx, "shared", startSeq: 1, count: 2, tenantId: "t1");
        var t2 = await SeedCleanStreamAsync(ctx, "shared", startSeq: 10, count: 2, tenantId: "t2");

        Assert.Null(t2[0].PreviousHash);

        var t2Result = await Verifier(ctx).VerifyChainAsync(
            AuditChainVerificationRequest.ForEntity(OrderType, "shared", tenantId: "t2"));
        Assert.True(t2Result.IsValid);
        Assert.Equal(2, t2Result.VerifiedRowCount);
    }

    [Fact]
    public async Task Verify_EmptyTable_IsValid()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        var result = await Verifier(ctx).VerifyChainAsync(AuditChainVerificationRequest.All());

        Assert.True(result.IsValid);
        Assert.Equal(0, result.VerifiedRowCount);
    }

    [Fact]
    public async Task Verify_RequestValidation_RejectsEntityIdWithoutType()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Verifier(ctx).VerifyChainAsync(new AuditChainVerificationRequest { EntityId = "x" }));
    }

    [Theory]
    [InlineData("   ", "o1")]
    [InlineData("Acme.Order", "   ")]
    public async Task Verify_RequestValidation_RejectsWhitespaceKeys(string entityType, string entityId)
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Verifier(ctx).VerifyChainAsync(
                new AuditChainVerificationRequest { EntityType = entityType, EntityId = entityId }));
    }

    [Fact]
    public void ForEntity_RejectsWhitespaceArguments()
    {
        Assert.Throws<ArgumentException>(() => AuditChainVerificationRequest.ForEntity("   ", "o1"));
        Assert.Throws<ArgumentException>(() => AuditChainVerificationRequest.ForEntity(OrderType, "   "));
    }

    // --- No-tenant (null/empty TenantId) coverage ------------------------------------------------
    // The hash-chain keys streams by (EntityType, EntityId, TenantId) and treats a null tenant as the
    // empty string. Two on-disk shapes of a no-tenant stream must both work:
    //   * NEW data: the write path (StampAsync) canonicalizes the row tenant to "" BEFORE MAC'ing and
    //     persisting, so rows, MAC, and anchor all agree on "".
    //   * HISTORIC data (written before the normalization): the row column physically holds null and the
    //     row was MAC'd with that null (the hasher reads AuditLog.TenantId directly, and a null field is
    //     encoded distinctly from ""), while its anchor was created with "" (the anchor's tenant always
    //     came from the canonical ChainKey). Verification must still find that "" anchor for the null
    //     rows and not split or NRE.
    // These tests pin: (1) a clean no-tenant chain verifies with no NullReferenceException, (2) the write
    // path stores the SAME canonical "" on row and anchor, (3) tail-row deletion is detected via the
    // anchor for a null-tenant stream, and (4) the no-tenant stream is verified exactly once (null rows +
    // "" anchor must dedupe to one stream, not two).

    // Seeds a HISTORIC null-tenant stream exactly as pre-normalization capture left it on disk: rows whose
    // TenantId column is literally null, MAC'd over that null (bypassing StampAsync so the new write-path
    // canonicalization does not touch them), plus a single anchor whose TenantId is "". This is the only
    // faithful way to reproduce the null-row / ""-anchor split that Findings A and B are about - mutating
    // the column AFTER a "" stamp would instead corrupt the MAC. Returns the seeded rows in chain order.
    private static async Task<List<AuditLog>> SeedHistoricNullTenantStreamAsync(
        AuditDbContext ctx, string entityId, byte startSeq, int count)
    {
        var rows = new List<AuditLog>();
        string? previous = null;
        for (var i = 0; i < count; i++)
        {
            var seq = (byte)(startSeq + i);
            var action = i == 0 ? AuditAction.Inserted : AuditAction.Updated;
            var diff = i == 0 ? "[]" : $"[{{\"op\":\"replace\",\"path\":\"/v\",\"value\":{seq}}}]";
            var row = Row(seq, entityId, action, diff, tenantId: null); // TenantId stays null
            row.PreviousHash = previous;
            row.HashKeyId = TestChainKeys.ActiveKeyId;
            // MAC over the null-tenant row, exactly as the pre-fix stamper did.
            row.EntryHash = AuditEntryHasher.ComputeEntryHash(row, previous, TestChainKeys.Key.Span);
            previous = row.EntryHash;
            rows.Add(row);
        }
        ctx.AuditLogs.AddRange(rows);
        // Anchor for the no-tenant stream is "" (the canonical ChainKey tenant), as capture would write it.
        ctx.Anchors.Add(new AuditChainAnchor
        {
            EntityType = OrderType,
            EntityId = entityId,
            TenantId = string.Empty,
            LatestEntryHash = rows[^1].EntryHash!,
            RowCount = count,
            KeyId = TestChainKeys.ActiveKeyId,
        });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
        return rows;
    }

    [Fact]
    public async Task Verify_NoTenantStream_CleanChain_IsValid()
    {
        // Post-normalization happy path: a null-tenant stream seeded through the production writer
        // persists "" on both rows and anchor, so verification walks one stream and never touches a
        // null TenantId (no NRE on stream.TenantId.Length).
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 5, tenantId: null);

        var result = await Verifier(ctx).VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));

        Assert.True(result.IsValid);
        Assert.Equal(5, result.VerifiedRowCount);
    }

    [Fact]
    public async Task Verify_NoTenantStream_WritePath_NormalizesRowAndAnchorToEmpty()
    {
        // The write path (StampAsync) is the single chaining choke point; it must coalesce a null row
        // tenant to "" so the AuditLog row and its AuditChainAnchor store an identical, non-null value.
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 3, tenantId: null);

        var rowTenants = await ctx.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == "o1").Select(a => a.TenantId).ToListAsync();
        Assert.All(rowTenants, t => Assert.Equal(string.Empty, t));

        var anchor = await ctx.Anchors.AsNoTracking().SingleAsync();
        Assert.Equal(string.Empty, anchor.TenantId);
        // Row and anchor agree: the historic null/"" split is gone at the source.
        Assert.All(rowTenants, t => Assert.Equal(anchor.TenantId, t));
    }

    [Fact]
    public async Task Verify_HistoricNullTenant_CleanChain_IsValid_NoNre()
    {
        // Historic rows with a literal NULL tenant (MAC'd over null) + a "" anchor must verify cleanly:
        // the "" anchor is matched via LoadAnchorAsync's null-or-empty branch, and the null rows via
        // LoadStreamAsync's matching branch. Finding A regression: no NRE from a null StreamKey.TenantId.
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        await SeedHistoricNullTenantStreamAsync(ctx, "o1", startSeq: 1, count: 5);

        var result = await Verifier(ctx).VerifyChainAsync(AuditChainVerificationRequest.All());

        Assert.True(result.IsValid);
        Assert.Equal(5, result.VerifiedRowCount);
    }

    [Fact]
    public async Task Verify_HistoricNullTenant_TailDeletion_DetectedViaAnchor()
    {
        // The core Finding B regression: with rows stored as null tenant and the anchor as "",
        // LoadAnchorAsync must still find the anchor (null-or-empty match) so tail truncation is caught.
        // Before the fix the exact-equality match would miss the "" anchor and truncation would pass.
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        var rows = await SeedHistoricNullTenantStreamAsync(ctx, "o1", startSeq: 1, count: 5);

        var tail = await ctx.AuditLogs.SingleAsync(a => a.Id == rows[4].Id);
        ctx.AuditLogs.Remove(tail);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var result = await Verifier(ctx).VerifyChainAsync(AuditChainVerificationRequest.All());

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.Truncated, result.Reason);
        Assert.Equal("o1", result.BrokenEntityId);
    }

    [Fact]
    public async Task Verify_HistoricNullTenant_StreamVerifiedExactlyOnce()
    {
        // Finding A regression: null rows project to a "" StreamKey and the "" anchor projects to the
        // same key, so the row/anchor union must dedupe them into ONE stream. If the anchor key were
        // left null it would be a SECOND key, double-verifying (or mis-counting) the no-tenant stream.
        // VerifiedRowCount == seeded count proves the stream was walked exactly once.
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        await SeedHistoricNullTenantStreamAsync(ctx, "o1", startSeq: 1, count: 4);

        // Sanity: the split genuinely exists at rest - rows are NULL, the anchor is "".
        var distinctRowTenants = await ctx.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == "o1").Select(a => a.TenantId).Distinct().ToListAsync();
        Assert.Equal(new string?[] { null }, distinctRowTenants);
        Assert.Equal(string.Empty, (await ctx.Anchors.AsNoTracking().SingleAsync()).TenantId);

        var result = await Verifier(ctx).VerifyChainAsync(AuditChainVerificationRequest.All());

        Assert.True(result.IsValid);
        Assert.Equal(4, result.VerifiedRowCount); // exactly once: 4, not 8
    }

    [Fact]
    public async Task Verify_HistoricNullTenant_AlongsideRealTenant_BothVerifyIndependently()
    {
        // A no-tenant stream (historic null rows) coexisting with a real-tenant stream for the SAME
        // entity id must each verify as their own chain - the no-tenant rows must not leak into the
        // "t1" stream nor vice-versa, and the multi-tenant behaviour pinned elsewhere must hold.
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        await SeedHistoricNullTenantStreamAsync(ctx, "shared", startSeq: 1, count: 3);
        await SeedCleanStreamAsync(ctx, "shared", startSeq: 10, count: 3, tenantId: "t1");

        // Two anchors: one "" (no-tenant), one "t1".
        Assert.Equal(2, await ctx.Anchors.CountAsync());

        var noTenant = await Verifier(ctx).VerifyChainAsync(
            AuditChainVerificationRequest.ForEntity(OrderType, "shared")); // null tenant -> "" stream
        var t1 = await Verifier(ctx).VerifyChainAsync(
            AuditChainVerificationRequest.ForEntity(OrderType, "shared", tenantId: "t1"));

        Assert.True(noTenant.IsValid);
        Assert.Equal(3, noTenant.VerifiedRowCount);
        Assert.True(t1.IsValid);
        Assert.Equal(3, t1.VerifiedRowCount);
    }
}
