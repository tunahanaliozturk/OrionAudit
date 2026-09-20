using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Integrity;
using Moongazing.OrionAudit.Retention;

namespace Moongazing.OrionAudit.IntegrationTests;

/// <summary>
/// Retention and tamper-evidence on the same stream. The retention sweep deletes the OLDEST rows of a
/// chain, which used to make every verification after the first purge report tampering: the surviving
/// prefix no longer started at the genesis and the walked count no longer reached the anchor's. A
/// tamper report that always fires is a tamper report nobody reads, so pruned history must verify -
/// while a genuine mutation, and a deletion no sweep recorded, must still fail.
/// </summary>
public class RetentionHashChainTests
{
    // Fixed 32-byte key (base64) so MACs are reproducible across runs.
    private const string KeyId1Base64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    private sealed class ChainRetentionDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditChainAnchor> Anchors => Set<AuditChainAnchor>();
        public ChainRetentionDb(DbContextOptions<ChainRetentionDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private sealed class FrozenClock : TimeProvider
    {
        private DateTimeOffset now;
        public FrozenClock(DateTimeOffset start) => now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now = now.Add(by);
    }

    /// <summary>
    /// Deletes the rows it is offered and then fails, standing in for the cancellation, transient
    /// database error or process exit that can land between the delete and the checkpoint.
    /// </summary>
    private sealed class DeleteThenFailArchiver : IAuditArchiver
    {
        public async Task<int> ArchiveAsync(
            DbContext dbContext, IReadOnlyList<AuditLog> rows, RetentionPolicy policy, CancellationToken ct)
        {
            var ids = rows.Select(r => r.Id).ToList();
            await dbContext.Set<AuditLog>().Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct);
            throw new InvalidOperationException("archiver failed after removing the rows");
        }
    }

    private static async Task<(ServiceProvider sp, SqliteConnection conn, FrozenClock clock)> BuildAsync(
        Action<OrionAuditOptions>? extraOptions = null,
        Action<ServiceCollection>? extraServices = null)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();

        var clock = new FrozenClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLogging();
        extraServices?.Invoke(services);
        services.AddOrionAudit<ChainRetentionDb>(o =>
        {
            o.Audit<Note>();
            o.UseHashChain(h => h.UseKey(1, KeyId1Base64));
            o.RetainCount(3);
            extraOptions?.Invoke(o);
        });
        services.AddSingleton(conn);
        services.AddDbContext<ChainRetentionDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ChainRetentionDb>().Database.EnsureCreatedAsync();
        }
        return (sp, conn, clock);
    }

    /// <summary>One Inserted + five Updated rows on a single chained stream.</summary>
    private static async Task<Guid> SeedSixChainedRowsAsync(ServiceProvider sp, FrozenClock clock)
    {
        Guid noteId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ChainRetentionDb>();
            var note = new Note { Body = "init" };
            ctx.Notes.Add(note);
            await ctx.SaveChangesAsync();
            noteId = note.Id;
        }
        for (var i = 1; i <= 5; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await using var scope = sp.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<ChainRetentionDb>();
            var fresh = await ctx.Notes.FirstAsync();
            fresh.Body = $"v{i}";
            await ctx.SaveChangesAsync();
        }
        return noteId;
    }

    private static async Task<AuditChainVerificationResult> VerifyAsync(ServiceProvider sp, Guid noteId)
    {
        await using var scope = sp.CreateAsyncScope();
        var verifier = scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
        return await verifier.VerifyChainAsync(
            AuditChainVerificationRequest.ForEntity(typeof(Note).AssemblyQualifiedName!, noteId.ToString()));
    }

    private static async Task<int> SweepAsync(ServiceProvider sp)
        => await ActivatorUtilities.CreateInstance<AuditRetentionHostedService<ChainRetentionDb>>(sp)
            .SweepOnceAsync();

    [Fact]
    public async Task Verification_PassesAfterRetentionPrunesTheChainHead()
    {
        var (sp, conn, clock) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        var noteId = await SeedSixChainedRowsAsync(sp, clock);
        Assert.True((await VerifyAsync(sp, noteId)).IsValid, "chain should verify before any purge");

        Assert.Equal(3, await SweepAsync(sp));   // 6 rows, RetainCount(3) -> 3 pruned from the head

        var afterPurge = await VerifyAsync(sp, noteId);
        Assert.True(afterPurge.IsValid,
            $"pruned history must not read as tampering, but got {afterPurge.Reason}: {afterPurge.Detail}");
        Assert.Equal(3, afterPurge.VerifiedRowCount);

        // The prune is recorded on the anchor rather than forgiven blindly: the stream's lifetime
        // total is intact and the pruned prefix is accounted for.
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ChainRetentionDb>();
        var anchor = await ctx.Anchors.SingleAsync();
        Assert.Equal(6, anchor.RowCount);
        Assert.Equal(3, anchor.PrunedRowCount);
        Assert.NotNull(anchor.PrunedThroughHash);
    }

    [Fact]
    public async Task Verification_RepeatedSweeps_KeepThePrunedChainVerifiable()
    {
        var (sp, conn, clock) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        var noteId = await SeedSixChainedRowsAsync(sp, clock);
        await SweepAsync(sp);

        // More activity, then a second purge: the checkpoint has to advance, not just be set once.
        for (var i = 6; i <= 8; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await using var scope = sp.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<ChainRetentionDb>();
            var fresh = await ctx.Notes.FirstAsync();
            fresh.Body = $"v{i}";
            await ctx.SaveChangesAsync();
        }
        Assert.Equal(3, await SweepAsync(sp));

        var result = await VerifyAsync(sp, noteId);
        Assert.True(result.IsValid,
            $"a twice-pruned chain must still verify, but got {result.Reason}: {result.Detail}");
        Assert.Equal(3, result.VerifiedRowCount);
    }

    [Fact]
    public async Task Verification_StillDetectsAMutatedRow_AfterRetentionPrunedTheHead()
    {
        var (sp, conn, clock) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        var noteId = await SeedSixChainedRowsAsync(sp, clock);
        await SweepAsync(sp);

        // Rewrite a surviving row's captured diff. Pruning the head must not buy an attacker the
        // right to edit what is left.
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ChainRetentionDb>();
            var survivor = await ctx.AuditLogs
                .Where(a => a.EntityId == noteId.ToString())
                .OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id)
                .FirstAsync();
            survivor.Diff = "[{\"op\":\"replace\",\"path\":\"/Body\",\"value\":\"forged\"}]";
            await ctx.SaveChangesAsync();
        }

        var result = await VerifyAsync(sp, noteId);
        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.ContentMismatch, result.Reason);
    }

    [Fact]
    public async Task Verification_StillDetectsARowDeletedOutsideRetention_AfterAPurge()
    {
        var (sp, conn, clock) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        var noteId = await SeedSixChainedRowsAsync(sp, clock);
        await SweepAsync(sp);

        // Delete the tail by hand. The anchor still pins the stream's true tail and lifetime count,
        // so this is truncation no matter how much of the head was legitimately pruned.
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ChainRetentionDb>();
            var tail = await ctx.AuditLogs
                .Where(a => a.EntityId == noteId.ToString())
                .OrderByDescending(a => a.OccurredOnUtc).ThenByDescending(a => a.Id)
                .FirstAsync();
            ctx.AuditLogs.Remove(tail);
            await ctx.SaveChangesAsync();
        }

        var result = await VerifyAsync(sp, noteId);
        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.Truncated, result.Reason);
    }

    /// <summary>
    /// The delete and the checkpoint that explains it must commit together. If the rows can go while
    /// the checkpoint stays behind, the stream is left in exactly the permanent false-tamper state
    /// this feature exists to remove - reached by a crash instead of by design.
    /// </summary>
    [Fact]
    public async Task Sweep_ThatFailsAfterTheDelete_LeavesTheRowsAndTheChainIntact()
    {
        var (sp, conn, clock) = await BuildAsync(
            extraServices: s => s.AddSingleton<IAuditArchiver, DeleteThenFailArchiver>());
        await using var _conn = conn;
        await using var _sp = sp;

        var noteId = await SeedSixChainedRowsAsync(sp, clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SweepAsync(sp));

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ChainRetentionDb>();
            Assert.Equal(6, await ctx.AuditLogs.CountAsync());   // the delete rolled back with the failure
            var anchor = await ctx.Anchors.SingleAsync();
            Assert.Equal(0, anchor.PrunedRowCount);
            Assert.Null(anchor.PrunedThroughHash);
        }

        var result = await VerifyAsync(sp, noteId);
        Assert.True(result.IsValid,
            $"a failed sweep must leave the chain exactly as it was, but got {result.Reason}: {result.Detail}");
    }
}
