using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.Tests.Integrity;

public class EfCoreAuditIntegrityVerifierTests
{
    private const string OrderType = "Acme.Order, Acme";
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

    // Persists a clean, correctly-stamped stream of `count` rows for one entity, returning the rows.
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
        AuditHashChainStamper.Stamp(
            rows, new Dictionary<AuditHashChainStamper.ChainKey, string?>(), AuditHashChainScope.PerEntityStream);
        ctx.AuditLogs.AddRange(rows);
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

        var verifier = new EfCoreAuditIntegrityVerifier(ctx);
        var result = await verifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));

        Assert.True(result.IsValid);
        Assert.Equal(5, result.VerifiedRowCount);
    }

    [Fact]
    public async Task Verify_AfterMutatingStoredRow_FailsWithContentMismatch()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        var rows = await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 4);

        // Mutate a persisted row's content out of band (simulating a direct DB edit), leaving its
        // stored hash untouched.
        var victim = await ctx.AuditLogs.SingleAsync(a => a.Id == rows[2].Id);
        victim.Diff = "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":424242}]";
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var verifier = new EfCoreAuditIntegrityVerifier(ctx);
        var result = await verifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.ContentMismatch, result.Reason);
        Assert.Equal(rows[2].Id, result.BrokenAtId);
    }

    [Fact]
    public async Task Verify_AfterDeletingRow_FailsWithBrokenLink()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        var rows = await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 5);

        var victim = await ctx.AuditLogs.SingleAsync(a => a.Id == rows[2].Id);
        ctx.AuditLogs.Remove(victim);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var verifier = new EfCoreAuditIntegrityVerifier(ctx);
        var result = await verifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.BrokenLink, result.Reason);
        // The break is reported at the row that followed the deleted one.
        Assert.Equal(rows[3].Id, result.BrokenAtId);
    }

    [Fact]
    public async Task Verify_IsIdempotent_OnAValidChain()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 5);

        var verifier = new EfCoreAuditIntegrityVerifier(ctx);
        var first = await verifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));
        var second = await verifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));

        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Equal(first.VerifiedRowCount, second.VerifiedRowCount);
    }

    [Fact]
    public async Task StampAsync_AppendingInLaterSave_ContinuesPersistedChain()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        var first = await SeedCleanStreamAsync(ctx, "o1", startSeq: 1, count: 3);

        // Append two more rows via the production writer, which reads the persisted head and chains.
        var append = new List<AuditLog>
        {
            Row(4, "o1", AuditAction.Updated, "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":4}]"),
            Row(5, "o1", AuditAction.Updated, "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":5}]"),
        };
        ctx.AuditLogs.AddRange(append);
        await EfCoreAuditHashChainWriter.StampAsync(ctx, append, AuditHashChainScope.PerEntityStream, default);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        // The first appended row must chain onto the previously-persisted head.
        Assert.Equal(first[^1].EntryHash, append[0].PreviousHash);

        var verifier = new EfCoreAuditIntegrityVerifier(ctx);
        var result = await verifier.VerifyChainAsync(AuditChainVerificationRequest.ForEntity(OrderType, "o1"));
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

        // Break o2's middle row.
        var victim = await ctx.AuditLogs.SingleAsync(a => a.Id == second[1].Id);
        victim.Diff = "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":-1}]";
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var verifier = new EfCoreAuditIntegrityVerifier(ctx);
        var result = await verifier.VerifyChainAsync(AuditChainVerificationRequest.All());

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
        // Same entity id under two tenants, each an independent chain.
        await SeedCleanStreamAsync(ctx, "shared", startSeq: 1, count: 3, tenantId: "t1");
        var t2 = await SeedCleanStreamAsync(ctx, "shared", startSeq: 10, count: 3, tenantId: "t2");

        // Corrupt t2 only.
        var victim = await ctx.AuditLogs.SingleAsync(a => a.Id == t2[2].Id);
        victim.Diff = "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":777}]";
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var verifier = new EfCoreAuditIntegrityVerifier(ctx);

        var t1Result = await verifier.VerifyChainAsync(
            AuditChainVerificationRequest.ForEntity(OrderType, "shared", tenantId: "t1"));
        var t2Result = await verifier.VerifyChainAsync(
            AuditChainVerificationRequest.ForEntity(OrderType, "shared", tenantId: "t2"));

        Assert.True(t1Result.IsValid);   // t1 untouched
        Assert.False(t2Result.IsValid);  // t2 corrupted
    }

    [Fact]
    public async Task Verify_EmptyTable_IsValid()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;

        var verifier = new EfCoreAuditIntegrityVerifier(ctx);
        var result = await verifier.VerifyChainAsync(AuditChainVerificationRequest.All());

        Assert.True(result.IsValid);
        Assert.Equal(0, result.VerifiedRowCount);
    }

    [Fact]
    public async Task Verify_RequestValidation_RejectsEntityIdWithoutType()
    {
        var (ctx, conn) = await NewDbAsync();
        await using var _ = conn;
        await using var __ = ctx;
        var verifier = new EfCoreAuditIntegrityVerifier(ctx);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            verifier.VerifyChainAsync(new AuditChainVerificationRequest { EntityId = "x" }));
    }
}
