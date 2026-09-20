namespace Moongazing.OrionAudit.Retention;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
/// The fix re-anchors the chain at the oldest surviving row: the removal and the checkpoint that
/// explains it are written in ONE transaction, so the two can never disagree. Verification then
/// expects <c>walked + PrunedRowCount == RowCount</c> and expects the surviving genesis to link to
/// <see cref="AuditChainAnchor.PrunedThroughHash"/>. Nothing else is relaxed:
/// <see cref="AuditChainAnchor.RowCount"/> and <see cref="AuditChainAnchor.LatestEntryHash"/> still
/// pin the stream's lifetime total and its tail, a mutated row still fails its keyed MAC, and a
/// deletion that no sweep recorded still breaks the walk.
/// </para>
/// <para>
/// <b>Atomicity.</b> The delete and the checkpoint commit together. Without that, a cancellation, a
/// transient database failure or a process exit between them would permanently pair deleted rows with
/// a stale checkpoint - which is the same permanent false-tamper state this class exists to remove,
/// just reached by a different route. A provider with no transaction support (the EF in-memory
/// provider) raises <see cref="InvalidOperationException"/> on begin; that is caught and the work runs
/// unwrapped, matching <see cref="CopyToTableAuditArchiver{TArchiveRow}"/>. That archiver in turn
/// detects this transaction as ambient and joins it rather than starting its own.
/// </para>
/// <para>
/// <b>Concurrency.</b> The pruned total is accumulated from the rows each batch actually removed, and
/// never derived by subtracting a separately-read survivor count from
/// <see cref="AuditChainAnchor.RowCount"/>. That column belongs to the append path, which advances it
/// under the anchor's write lock; a read-modify-write against it would skew whenever an append
/// committed between the sweep's two reads. Retention writes only the columns it owns, so the two
/// writers never contend - which is why this needs neither a lock of its own nor an isolation-level
/// assumption.
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

        // Only hashed rows ever counted toward an anchor; an unchained prefix is outside the chain.
        var chained = rows.Where(r => r.EntryHash is not null).ToList();
        if (chained.Count == 0)
        {
            return await inner.ArchiveAsync(dbContext, rows, policy, cancellationToken).ConfigureAwait(false);
        }

        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // No-transaction providers (EF in-memory) raise this. Fall through unwrapped, exactly as
            // CopyToTableAuditArchiver does, and rely on the provider's natural ordering.
        }

        try
        {
            // Read each touched stream's anchor BEFORE the rows leave, in the same order the chain
            // writer touches anchor-then-rows, so retention and the append path never approach the
            // two in opposite orders.
            var anchors = await LoadAnchorsAsync(dbContext, chained, cancellationToken).ConfigureAwait(false);

            var removed = await inner.ArchiveAsync(dbContext, rows, policy, cancellationToken).ConfigureAwait(false);
            if (removed > 0 && anchors.Count > 0)
            {
                await RecordPrunesAsync(dbContext, chained, anchors, cancellationToken).ConfigureAwait(false);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return removed;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<Dictionary<StreamKey, AuditChainAnchor>> LoadAnchorsAsync(
        DbContext dbContext, List<AuditLog> chained, CancellationToken cancellationToken)
    {
        var result = new Dictionary<StreamKey, AuditChainAnchor>();
        foreach (var stream in chained.Select(StreamOf).Distinct())
        {
            // Tracked: the checkpoint is written back through SaveChanges.
            var query = dbContext.Set<AuditChainAnchor>()
                .Where(a => a.EntityType == stream.EntityType && a.EntityId == stream.EntityId);
            query = stream.TenantId.Length == 0
                ? query.Where(a => a.TenantId == null || a.TenantId == "")
                : query.Where(a => a.TenantId == stream.TenantId);

            var anchor = await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (anchor is not null)
            {
                // A stream with no anchor was never chained (chaining switched on after those rows
                // were written), so there is no truncation guard to keep in step.
                result[stream] = anchor;
            }
        }
        return result;
    }

    private static async Task RecordPrunesAsync(
        DbContext dbContext,
        List<AuditLog> chained,
        Dictionary<StreamKey, AuditChainAnchor> anchors,
        CancellationToken cancellationToken)
    {
        // Which of the batch's chained rows actually left. The IAuditArchiver contract allows an
        // implementation to be handed a row it has already removed (an idempotent retry), and a
        // custom archiver may remove only some of what it was offered; counting those would
        // permanently overstate the pruned total, so ask the table rather than assume. Safe inside
        // the transaction: a concurrent append only ever adds new ids, it cannot resurrect these.
        var offered = chained.Select(r => r.Id).ToList();
        var notRemoved = new HashSet<Guid>(await dbContext.Set<AuditLog>().AsNoTracking()
            .Where(a => offered.Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        foreach (var group in chained.Where(r => !notRemoved.Contains(r.Id)).GroupBy(StreamOf))
        {
            if (!anchors.TryGetValue(group.Key, out var anchor))
            {
                continue;
            }
            var stream = group.Key;

            // ACCUMULATE what this batch removed; never derive the pruned total by subtracting a
            // separately-read survivor count from RowCount. RowCount belongs to the append path,
            // which advances it under the anchor's write lock. A read-modify-write against it skews
            // whenever an append commits between the two reads - the survivor count then includes
            // the new row while the anchor still holds the pre-append total, recording one fewer
            // pruned row than there were, and verification reports truncation on an intact chain.
            // An increment touches only the columns retention owns, so appends move RowCount,
            // retention moves PrunedRowCount, and the two stay consistent with no lock and no
            // isolation-level assumption.
            anchor.PrunedRowCount += group.LongCount();

            // The oldest surviving row IS the chain's new genesis, and the hash it already carries is
            // exactly the watermark verification needs. Ordered the same way the verifier walks the
            // stream so the two agree on which row that is. This read is race-tolerant by nature: an
            // append lands at the tail, so it cannot change which row is the head - except when the
            // stream was emptied, where the appended row IS the head and its PreviousHash is the
            // retained tail, which is the same watermark the fallback below would pick.
            var genesis = await ScopeToStream(dbContext.Set<AuditLog>().AsNoTracking(), stream)
                .Where(a => a.EntryHash != null)
                .OldestFirst()
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            // Nothing survives: the whole chain up to the anchored tail was pruned, so the tail IS
            // the last pruned hash. Clearing the watermark here would strand the stream - the anchor
            // deliberately keeps the deleted tail in LatestEntryHash, so the next append to this
            // entity chains onto it, and verification would then expect that new row's PreviousHash
            // to be null and report a broken link on a chain nobody touched.
            //
            // The explicit null test rather than ??: a surviving genesis whose PreviousHash is
            // legitimately null (only an unchained prefix was pruned, the true genesis remains) must
            // keep that null, not inherit the tail.
            anchor.PrunedThroughHash = genesis is not null ? genesis.PreviousHash : anchor.LatestEntryHash;
        }
    }

    private static StreamKey StreamOf(AuditLog row)
        => new(row.EntityType, row.EntityId, AuditTenant.Canonical(row.TenantId));

    // Mirrors EfCoreAuditIntegrityVerifier: the no-tenant stream matches both a null and an
    // empty-string tenant, because rows written before the write-path normalization still store null.
    private static IQueryable<AuditLog> ScopeToStream(IQueryable<AuditLog> query, StreamKey stream)
    {
        query = query.Where(a => a.EntityType == stream.EntityType && a.EntityId == stream.EntityId);
        return stream.TenantId.Length == 0
            ? query.Where(a => a.TenantId == null || a.TenantId == "")
            : query.Where(a => a.TenantId == stream.TenantId);
    }

    private readonly record struct StreamKey(string EntityType, string EntityId, string TenantId);
}
