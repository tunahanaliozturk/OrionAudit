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
    /// The plan produced by <see cref="Plan"/>: the compacted snapshot row to insert and the rows
    /// to delete. A no-op plan carries a null <see cref="SnapshotRow"/> and an empty
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

        /// <summary>The compacted snapshot row to insert, or null when the plan is a no-op.</summary>
        public AuditLog? SnapshotRow { get; }

        /// <summary>The rows to delete from the live audit table (the folded history).</summary>
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
                    rowsRemoved: RowsToRemove.Count,
                    // After: every original row minus the folded rows, plus the one snapshot row.
                    rowsAfter: RowsBefore - RowsToRemove.Count + 1,
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
        var snapshotRow = BuildSnapshotRow(boundary, state);

        return new CompactionPlan(snapshotRow, folded, rowCount, retainTail);
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
            state = JsonNode.Parse(snapshotJson)?.AsObject()
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

    // The compacted snapshot row inherits the boundary row's identity dimensions (so it slots into
    // the same entity/tenant history) and timestamp (so chronological ordering against the retained
    // tail is preserved). Its Action records the entity's last folded action so a delete that was
    // folded still reads as a terminal state, while its Snapshot carries the reconstructed state
    // and its Diff is empty (the row IS the state, not a delta).
    private static AuditLog BuildSnapshotRow(AuditLog boundary, JsonObject state)
        => new()
        {
            Id = Guid.NewGuid(),
            EntityType = boundary.EntityType,
            EntityBaseType = boundary.EntityBaseType,
            EntityId = boundary.EntityId,
            // A folded delete stays a terminal state; anything else compacts to an Updated state row.
            Action = boundary.Action is AuditAction.Deleted or AuditAction.SoftDeleted
                ? boundary.Action
                : AuditAction.Updated,
            OccurredOnUtc = boundary.OccurredOnUtc,
            UserId = boundary.UserId,
            UserDisplay = boundary.UserDisplay,
            UserType = boundary.UserType,
            TenantId = boundary.TenantId,
            CorrelationId = boundary.CorrelationId,
            Diff = "[]",
            Snapshot = state.ToJsonString(),
            Error = null,
        };
}
