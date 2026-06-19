using System.Text.Json.Nodes;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Store;

/// <summary>
/// Pure, backend-agnostic engine that computes how to compact one entity's audit history. It
/// folds the rows older than the retained tail into a single compacted snapshot row and reports
/// which rows to remove. Operates entirely over <see cref="AuditLog"/> JSON (via
/// <see cref="DiffEngine"/>) — no reflection, no CLR entity type — so it is trim-safe and
/// Native-AOT clean, and every <see cref="IAuditHistoryStore"/> can share one implementation.
/// </summary>
public static class AuditHistoryCompactor
{
    /// <summary>
    /// The plan produced by <see cref="Plan"/>: the compacted snapshot row to update in place and
    /// the older rows to delete. A no-op plan carries a null <see cref="SnapshotRow"/> and an empty
    /// <see cref="RowsToRemove"/>.
    /// </summary>
    public sealed class CompactionPlan
    {
        internal CompactionPlan(AuditLog? snapshotRow, IReadOnlyList<AuditLog> rowsToRemove, int rowsBefore, int retainedTail)
        {
            SnapshotRow = snapshotRow;
            RowsToRemove = rowsToRemove;
            RowsBefore = rowsBefore;
            RetainedTail = retainedTail;
        }

        /// <summary>
        /// The compacted snapshot row, or null when the plan is a no-op. This is the folded
        /// boundary row mutated <em>in place</em>: it keeps the boundary's <see cref="AuditLog.Id"/>
        /// and <see cref="AuditLog.OccurredOnUtc"/> so it deterministically tie-breaks BEFORE the
        /// retained tail at the same timestamp. Because it reuses an existing row's identity, a
        /// store must <em>update</em> this row in place, not insert it as a new row (see
        /// <see cref="RowsToRemove"/>, which never includes the boundary).
        /// </summary>
        public AuditLog? SnapshotRow { get; }

        /// <summary>
        /// The rows to delete from the live audit table (the folded history), excluding the
        /// boundary row, which is preserved and updated in place as <see cref="SnapshotRow"/>.
        /// </summary>
        public IReadOnlyList<AuditLog> RowsToRemove { get; }

        /// <summary>Row count before compaction.</summary>
        public int RowsBefore { get; }

        /// <summary>The number of most-recent rows retained verbatim after the snapshot.</summary>
        public int RetainedTail { get; }

        /// <summary>True when this plan would actually fold any rows.</summary>
        public bool IsEffective => SnapshotRow is not null && RowsToRemove.Count > 0;

        /// <summary>Builds the <see cref="AuditCompactionResult"/> that describes applying this plan.</summary>
        public AuditCompactionResult ToResult()
            => IsEffective
                ? new AuditCompactionResult(
                    rowsBefore: RowsBefore,
                    // The boundary row is folded too but stays as the in-place snapshot rather than
                    // being deleted, so the reported removed/after counts add it back explicitly.
                    rowsRemoved: RowsToRemove.Count + 1,
                    // After: every original row minus all folded rows, plus the one snapshot row
                    // (the in-place boundary). Equivalent to RowsBefore - RowsToRemove.Count.
                    rowsAfter: RowsBefore - RowsToRemove.Count,
                    snapshotWritten: true)
                : AuditCompactionResult.NoOp(RowsBefore);
    }

    /// <summary>
    /// Computes a compaction plan for <paramref name="history"/> (the full row set for ONE entity)
    /// retaining the most-recent <paramref name="retainTail"/> rows verbatim. The input may be in
    /// any order; the engine sorts a defensive copy by <see cref="AuditLog.OccurredOnUtc"/> then
    /// <see cref="AuditLog.Id"/> for a stable boundary.
    /// </summary>
    /// <param name="history">All audit rows for a single entity.</param>
    /// <param name="retainTail">Number of most-recent rows to keep verbatim. Must be non-negative.</param>
    /// <returns>A plan; a no-op when there is nothing worth folding.</returns>
    /// <exception cref="OrionAuditException">
    /// The folded history cannot be replayed into a coherent snapshot (corrupted diff chain).
    /// </exception>
    public static CompactionPlan Plan(IReadOnlyList<AuditLog> history, int retainTail)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (retainTail < 0)
        {
            throw new ArgumentException($"retainTail must be non-negative (got {retainTail}).", nameof(retainTail));
        }

        var rowCount = history.Count;

        // Folding is only worthwhile when at least two rows would collapse into the single
        // snapshot row. We fold (rowCount - retainTail) rows; that must be >= 2 to shrink history.
        var foldCount = rowCount - retainTail;
        if (foldCount < 2)
        {
            return new CompactionPlan(snapshotRow: null, Array.Empty<AuditLog>(), rowCount, retainTail);
        }

        var ordered = history
            .OrderBy(r => r.OccurredOnUtc)
            .ThenBy(r => r.Id)
            .ToList();

        var folded = ordered.Take(foldCount).ToList();
        var boundary = folded[^1];

        var state = ReplayFolded(folded);
        var snapshotRow = WriteSnapshotInPlace(boundary, state);

        // The boundary is folded too, but it is preserved as the in-place snapshot row (keeping its
        // Id and OccurredOnUtc so it sorts ahead of the retained tail at an equal timestamp), so it
        // is excluded from the rows the store deletes. Only the strictly-older rows are removed.
        var rowsToRemove = folded.Take(foldCount - 1).ToList();

        return new CompactionPlan(snapshotRow, rowsToRemove, rowCount, retainTail);
    }

    // Replays the folded rows into a single JSON state, starting from the freshest usable
    // snapshot among them (Insert or Update carrying a Snapshot) and applying later diffs. Mirrors
    // the AuditReconstructor replay contract but stays at the JSON layer (no deserialization),
    // because compaction only needs to PERSIST state, never hand a typed entity back.
    private static JsonObject ReplayFolded(List<AuditLog> folded)
    {
        var snapshotIndex = -1;
        for (var i = folded.Count - 1; i >= 0; i--)
        {
            if (folded[i].Snapshot is not null && folded[i].Action is AuditAction.Updated or AuditAction.Inserted)
            {
                snapshotIndex = i;
                break;
            }
        }

        JsonObject state;
        int startIndex;
        if (snapshotIndex >= 0 && folded[snapshotIndex].Snapshot is { } snapshotJson)
        {
            // Parse defensively: a null parse (literal "null") OR a non-object JSON node (array or
            // scalar) both mean "not a usable snapshot object". AsObject() throws
            // InvalidOperationException for the non-object case, so guard the type explicitly and
            // route every failure through the documented OrionAuditException path.
            state = JsonNode.Parse(snapshotJson) as JsonObject
                ?? throw new OrionAuditException(
                    $"Compaction: snapshot on row {folded[snapshotIndex].Id} did not deserialize as a JSON object.");
            startIndex = snapshotIndex + 1;
        }
        else
        {
            state = new JsonObject();
            startIndex = 0;
        }

        for (var i = startIndex; i < folded.Count; i++)
        {
            var row = folded[i];
            if (string.IsNullOrEmpty(row.Diff) || row.Diff == "[]")
            {
                continue;
            }
            try
            {
                state = DiffEngine.Apply(state, row.Diff);
            }
            catch (Exception ex)
            {
                throw new OrionAuditException(
                    $"Compaction: failed to replay audit row {row.Id}: {ex.Message}", ex);
            }
        }

        return state;
    }

    // Turns the boundary row INTO the compacted snapshot, in place. The boundary keeps its Id and
    // OccurredOnUtc so it deterministically sorts ahead of the retained tail at an equal timestamp
    // (the store updates this row rather than inserting a fresh, randomly-keyed one). Its Snapshot
    // carries the reconstructed state and its Diff is emptied (the row IS the state, not a delta).
    //
    // Action: a folded delete stays a terminal Deleted/SoftDeleted state. Anything else becomes
    // Inserted, NOT Updated: a folded base state is effectively the insert of that state, and
    // AuditReconstructor.Replay REQUIRES a history to begin with an Inserted row. Writing the
    // compacted base as Inserted is what keeps a compacted insert/update history reconstructable.
    private static AuditLog WriteSnapshotInPlace(AuditLog boundary, JsonObject state)
    {
        boundary.Action = boundary.Action is AuditAction.Deleted or AuditAction.SoftDeleted
            ? boundary.Action
            : AuditAction.Inserted;
        boundary.Diff = "[]";
        boundary.Snapshot = state.ToJsonString();
        boundary.Error = null;
        return boundary;
    }
}
