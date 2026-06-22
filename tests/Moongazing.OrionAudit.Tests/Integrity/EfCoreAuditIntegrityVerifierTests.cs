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
}
