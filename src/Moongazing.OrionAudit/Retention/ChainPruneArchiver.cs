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

            // Hold back anything that would leave a hole rather than shorten a head.
            var prunable = TrimToContiguousHeads(rows, chained, anchors);

            var removed = await inner.ArchiveAsync(dbContext, prunable.Rows, policy, cancellationToken).ConfigureAwait(false);
            if (removed > 0 && anchors.Count > 0)
            {
                await RecordPrunesAsync(dbContext, prunable.Chained, anchors, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Narrows a batch to the rows that can leave without holing a chain: per stream, the longest run
    /// that starts at the stream's current head and follows its links unbroken. Everything after the
    /// first gap stays for a later sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sweep selects by age, which is what a retention policy means, and age is not chain order:
    /// <see cref="AuditLog.OccurredOnUtc"/> is stamped near the start of capture while a stream's
    /// chain order is settled later, by which writer wins the anchor lock, so two concurrent writers
    /// on one stream can invert. The cutoff can then fall between an inverted pair, or a batch bounded
    /// by <c>MaxRowsPerSweep</c> can end between them, and the sweep removes the chain-later row while
    /// keeping the chain-earlier one. That is a deletion from the middle, which re-anchoring cannot
    /// repair - the anchor records one watermark, not a set of holes - so verification reports a break
    /// on a trail retention itself pruned. Rare, and that is exactly what makes it worth removing: a
    /// tamper report that fires occasionally and wrongly is the kind operators learn to wave through.
    /// </para>
    /// <para>
    /// The test is the chain itself rather than a column that stands in for it - each row's
    /// <see cref="AuditLog.PreviousHash"/> against the one before it, starting from the head the
    /// anchor points at. So it needs no query and no sequence, holds for streams written before
    /// <see cref="AuditLog.ChainSequence"/> existed, and a fork or a gap simply stops the run instead
    /// of being reasoned about.
    /// </para>
    /// <para>
    /// Holding rows back is always safe: retention already leaves rows behind whenever a batch hits
    /// its bound, and this is self-healing. A row held back because the row before it in the chain was
    /// too new is swept as soon as that one ages out as well, and the two go together as a contiguous
    /// head. Nothing is deleted that the policy did not ask for - which is why this trims the batch
    /// rather than extending it over the blocking row, since that row is by definition still inside
    /// the retention window.
    /// </para>
    /// </remarks>
    private static (IReadOnlyList<AuditLog> Rows, List<AuditLog> Chained) TrimToContiguousHeads(
        IReadOnlyList<AuditLog> rows,
        List<AuditLog> chained,
        Dictionary<StreamKey, AuditChainAnchor> anchors)
    {
        var heldBack = new HashSet<Guid>();

        foreach (var group in chained.GroupBy(StreamOf))
        {
            if (!anchors.TryGetValue(group.Key, out var anchor))
            {
                // No anchor: the stream was never chained, so there is no truncation guard to keep in
                // step and nothing to protect. Matches how RecordPrunesAsync skips it.
                continue;
            }

            // PrunedThroughHash is what the current head links back to - null while the stream has
            // never been pruned, which is what a genesis row carries.
            var expected = anchor.PrunedThroughHash;
            var contiguous = true;
            foreach (var row in AuditChainOrder.ForWalk(group.ToList()))
            {
                if (contiguous && string.Equals(row.PreviousHash, expected, StringComparison.Ordinal))
                {
                    expected = row.EntryHash;
                    continue;
                }
                // The first row that does not continue the run ends it, and everything from there on
                // stays: removing any of it would take rows out of the middle of the chain.
                contiguous = false;
                heldBack.Add(row.Id);
            }
        }

        if (heldBack.Count == 0)
        {
            return (rows, chained);
        }

        return (
            rows.Where(r => !heldBack.Contains(r.Id)).ToList(),
            chained.Where(r => !heldBack.Contains(r.Id)).ToList());
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

            // The watermark is the hash of the NEWEST row this sweep removed - equivalently, the
            // PreviousHash the oldest survivor already carries. Taken from the removed rows rather
            // than by asking the table for the oldest survivor: "oldest" would have to be decided by
            // an ORDER BY, and no SQL ordering is the chain's order (AuditLog.OccurredOnUtc inverts
            // against it whenever two concurrent writers race for the anchor lock). The rows are in
            // hand here and AuditChainOrder.ForWalk puts them in the same order the verifier walks,
            // so the two cannot disagree about which row that is - and it costs a query less.
            //
            // When the sweep empties the stream this is the retained tail, which is what the anchor
            // must keep: it deliberately leaves the deleted tail in LatestEntryHash so the next
            // append to this entity chains onto it, and a cleared watermark would make verification
            // expect that new row's PreviousHash to be null and report a break on a chain nobody
            // touched.
            anchor.PrunedThroughHash = AuditChainOrder.ForWalk(group.ToList())[^1].EntryHash;
        }
    }

    private static StreamKey StreamOf(AuditLog row)
        => new(row.EntityType, row.EntityId, AuditTenant.Canonical(row.TenantId));

    private readonly record struct StreamKey(string EntityType, string EntityId, string TenantId);
}
