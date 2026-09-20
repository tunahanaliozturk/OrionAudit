namespace Moongazing.OrionAudit.Retention;

using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Integrity;

/// <summary>
/// Decorates the configured <see cref="IAuditArchiver"/> so that removing rows from a tamper-evident
/// stream also records the removal on that stream's <see cref="AuditChainAnchor"/>. Installed by the
/// retention sweep only when hash-chaining is enabled.
/// </summary>
/// <remarks>
/// <para>
/// Retention deletes the OLDEST rows of a stream. The chain cannot tell that apart from an attacker
/// deleting them: the surviving prefix no longer starts at the genesis (its
/// <see cref="AuditLog.PreviousHash"/> points at a row that is gone) and the walked row count no
/// longer reaches the anchor's. So after the first purge every verification reported tampering - a
/// false positive that made the tamper-evidence feature useless, because a report that always cries
/// wolf is a report nobody reads.
/// </para>
/// <para>
/// The fix re-anchors the chain at the oldest surviving row. After the delete this reads back, per
/// touched stream, how many hashed rows survive and what the new head links to, and writes both onto
/// the anchor. Verification then expects <c>walked + PrunedRowCount == RowCount</c> and expects the
/// surviving genesis to link to <see cref="AuditChainAnchor.PrunedThroughHash"/>. Nothing else is
/// relaxed: <see cref="AuditChainAnchor.RowCount"/> and <see cref="AuditChainAnchor.LatestEntryHash"/>
/// still pin the stream's lifetime total and its tail, a mutated row still fails its keyed MAC, and a
/// deletion that no sweep recorded still breaks the walk.
/// </para>
/// <para>
/// The counts are read back from the database rather than inferred from the batch, so re-presenting a
/// row after a transient failure cannot double-count it, and the ordering used to find the new head is
/// the same server-side <c>(OccurredOnUtc, Id)</c> ordering the verifier walks. The sweep does not take
/// the anchor's write lock: an appender only ever extends the tail, which leaves both the pruned prefix
/// and this checkpoint untouched.
/// </para>
/// </remarks>
internal sealed class ChainPruneArchiver : IAuditArchiver
{
    private readonly IAuditArchiver inner;

    public ChainPruneArchiver(IAuditArchiver inner)
        => this.inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public async Task<int> ArchiveAsync(
        DbContext dbContext,
        IReadOnlyList<AuditLog> rows,
        RetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(rows);

        var removed = await inner.ArchiveAsync(dbContext, rows, policy, cancellationToken).ConfigureAwait(false);
        if (removed == 0)
        {
            // Nothing left the live table, so no chain moved.
            return removed;
        }

        // Only hashed rows ever counted toward an anchor; an unchained prefix is outside the chain.
        var streams = rows
            .Where(r => r.EntryHash is not null)
            .Select(r => new StreamKey(r.EntityType, r.EntityId, AuditTenant.Canonical(r.TenantId)))
            .Distinct()
            .ToList();

        var repaired = false;
        foreach (var stream in streams)
        {
            repaired |= await RecordPruneAsync(dbContext, stream, cancellationToken).ConfigureAwait(false);
        }
        if (repaired)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return removed;
    }

    private static async Task<bool> RecordPruneAsync(
        DbContext dbContext, StreamKey stream, CancellationToken cancellationToken)
    {
        var anchor = await LoadAnchorAsync(dbContext, stream, cancellationToken).ConfigureAwait(false);
        if (anchor is null)
        {
            // The stream was never anchored (chaining switched on after these rows were written), so
            // there is no truncation guard to keep in step.
            return false;
        }

        var surviving = ScopeToStream(dbContext.Set<AuditLog>().AsNoTracking(), stream)
            .Where(a => a.EntryHash != null);

        var survivingCount = await surviving.LongCountAsync(cancellationToken).ConfigureAwait(false);
        // The oldest surviving row IS the chain's new genesis, and the hash it already carries is
        // exactly the watermark verification needs. Ordered the same way the verifier walks the
        // stream so the two agree on which row that is.
        var genesis = await surviving
            .OrderBy(a => a.OccurredOnUtc)
            .ThenBy(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        anchor.PrunedRowCount = anchor.RowCount - survivingCount;
        anchor.PrunedThroughHash = genesis?.PreviousHash;
        return true;
    }

    // Mirrors EfCoreAuditIntegrityVerifier: the no-tenant stream matches both a null and an
    // empty-string tenant, because rows written before the write-path normalization still store null.
    private static IQueryable<AuditLog> ScopeToStream(IQueryable<AuditLog> query, StreamKey stream)
    {
        query = query.Where(a => a.EntityType == stream.EntityType && a.EntityId == stream.EntityId);
        return stream.TenantId.Length == 0
            ? query.Where(a => a.TenantId == null || a.TenantId == "")
            : query.Where(a => a.TenantId == stream.TenantId);
    }

    private static async Task<AuditChainAnchor?> LoadAnchorAsync(
        DbContext dbContext, StreamKey stream, CancellationToken cancellationToken)
    {
        // Tracked: the checkpoint is written back through SaveChanges.
        var query = dbContext.Set<AuditChainAnchor>()
            .Where(a => a.EntityType == stream.EntityType && a.EntityId == stream.EntityId);
        query = stream.TenantId.Length == 0
            ? query.Where(a => a.TenantId == null || a.TenantId == "")
            : query.Where(a => a.TenantId == stream.TenantId);
        return await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private readonly record struct StreamKey(string EntityType, string EntityId, string TenantId);
}
