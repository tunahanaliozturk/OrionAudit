namespace Moongazing.OrionAudit.Store;

/// <summary>
/// Describes a snapshot-compaction request for a single entity's audit history: which entity,
/// and how many of the most-recent rows to retain verbatim. Passed to
/// <see cref="IAuditHistoryStore.CompactAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Compaction collapses a long Insert-then-many-Updates history into a single compacted
/// snapshot row (carrying the entity's reconstructed state at the compaction boundary) plus a
/// bounded <em>retained tail</em> of the most-recent rows kept verbatim. The rows older than the
/// tail are folded into the snapshot and removed, bounding storage growth while keeping the
/// latest state fully reconstructable.
/// </para>
/// <para>
/// The retained tail exists so recent, fine-grained change detail (who changed what, field by
/// field) survives compaction for the window operators care about most, while ancient history
/// collapses to a single state row.
/// </para>
/// </remarks>
public sealed record AuditCompactionRequest
{
    /// <summary>Assembly-qualified name of the audited entity type (matches <see cref="AuditLog.EntityType"/>).</summary>
    public required string EntityType { get; init; }

    /// <summary>Serialized primary key of the entity whose history is compacted (matches <see cref="AuditLog.EntityId"/>).</summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// Number of most-recent rows to retain verbatim AFTER the compacted snapshot. Must be
    /// non-negative. Zero collapses the entire history into a single snapshot row with no tail.
    /// When the entity has fewer historical rows than the tail plus a snapshot would require,
    /// compaction is a no-op (nothing to collapse).
    /// </summary>
    public required int RetainTail { get; init; }

    /// <summary>
    /// Optional tenant id used to scope the compaction to one tenant's rows when the same
    /// entity id can appear under multiple tenants. Null compacts across tenants for the entity.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>Validates the request, throwing <see cref="ArgumentException"/> on a malformed value.</summary>
    /// <exception cref="ArgumentException"><see cref="RetainTail"/> is negative.</exception>
    public void Validate()
    {
        if (RetainTail < 0)
        {
            throw new ArgumentException($"AuditCompactionRequest.RetainTail must be non-negative (got {RetainTail}).");
        }
        ArgumentException.ThrowIfNullOrEmpty(EntityType);
        ArgumentException.ThrowIfNullOrEmpty(EntityId);
    }
}

/// <summary>
/// Outcome of an <see cref="IAuditHistoryStore.CompactAsync"/> call: how many rows were folded
/// away and how the post-compaction history is shaped.
/// </summary>
public sealed class AuditCompactionResult
{
    /// <summary>Initializes a compaction result.</summary>
    /// <param name="rowsBefore">Row count for the entity before compaction.</param>
    /// <param name="rowsRemoved">Rows folded into the snapshot and deleted.</param>
    /// <param name="rowsAfter">Row count for the entity after compaction (snapshot + retained tail).</param>
    /// <param name="snapshotWritten">Whether a compacted snapshot row was written.</param>
    public AuditCompactionResult(int rowsBefore, int rowsRemoved, int rowsAfter, bool snapshotWritten)
    {
        RowsBefore = rowsBefore;
        RowsRemoved = rowsRemoved;
        RowsAfter = rowsAfter;
        SnapshotWritten = snapshotWritten;
    }

    /// <summary>Number of audit rows the entity had before compaction.</summary>
    public int RowsBefore { get; }

    /// <summary>Number of rows folded into the compacted snapshot and removed from the live table.</summary>
    public int RowsRemoved { get; }

    /// <summary>Number of audit rows the entity has after compaction (the snapshot plus the retained tail).</summary>
    public int RowsAfter { get; }

    /// <summary>True when a compacted snapshot row was written (false on a no-op compaction).</summary>
    public bool SnapshotWritten { get; }

    /// <summary>A no-op result for an entity whose history was too short to compact.</summary>
    public static AuditCompactionResult NoOp(int rowCount)
        => new(rowsBefore: rowCount, rowsRemoved: 0, rowsAfter: rowCount, snapshotWritten: false);
}
