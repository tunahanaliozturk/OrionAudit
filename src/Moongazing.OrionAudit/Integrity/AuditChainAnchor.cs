namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Persisted head of one tamper-evident chain stream: the latest <see cref="AuditLog.EntryHash"/> and
/// the number of hashed rows in that stream, keyed by (<see cref="EntityType"/>,
/// <see cref="EntityId"/>, <see cref="TenantId"/>). Exactly one anchor row exists per stream once that
/// stream has been chained.
/// </summary>
/// <remarks>
/// <para>
/// The anchor solves two problems the per-row chain alone cannot:
/// </para>
/// <list type="number">
/// <item>
/// <b>Concurrency.</b> Two transactions appending to the same stream both read the stream head before
/// either commits; without serialization they would stamp the same <see cref="LatestEntryHash"/> as
/// <see cref="AuditLog.PreviousHash"/> and corrupt the chain under normal concurrent writes. The
/// writer takes a row lock on this anchor inside the write transaction - the consumer's when they
/// opened one, otherwise one the write path opens around the stamp and commits with the rows (see
/// <see cref="ChainWriteTransaction"/>) - so same-stream appends serialize on it while different
/// streams stay parallel.
/// </item>
/// <item>
/// <b>Truncation / deletion.</b> A consistent prefix of a chain still verifies, so deleting the tail
/// row(s) - or an entire stream - is otherwise invisible. The anchor records the expected tail hash
/// and row count, so verification flags a stream whose walked tail/count disagrees with the anchor,
/// and flags a stream that has been removed entirely while its anchor still exists.
/// </item>
/// </list>
/// </remarks>
/// <remarks>
/// Deliberately not <c>sealed</c>: EF Core's proxy plugin rejects <em>every</em> sealed entity
/// type in the model, so sealing this would make <c>UseLazyLoadingProxies()</c> throw at model
/// build for any consumer that maps OrionAudit's tables.
/// </remarks>
public class AuditChainAnchor
{
    /// <summary>Assembly-qualified entity type of the stream (matches <see cref="AuditLog.EntityType"/>).</summary>
    public string EntityType { get; set; } = default!;

    /// <summary>Entity primary key of the stream (matches <see cref="AuditLog.EntityId"/>).</summary>
    public string EntityId { get; set; } = default!;

    /// <summary>
    /// Tenant of the stream. <see cref="string.Empty"/> for a null/single-tenant row, so the column is
    /// never null (it is part of the primary key).
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The <see cref="AuditLog.EntryHash"/> of the most recent hashed row in this stream.</summary>
    public string LatestEntryHash { get; set; } = default!;

    /// <summary>The number of hashed rows in this stream. Compared against the walked count to detect truncation.</summary>
    public long RowCount { get; set; }

    /// <summary>The <see cref="AuditLog.HashKeyId"/> of the most recent row, recorded for rotation visibility.</summary>
    public int KeyId { get; set; }

    /// <summary>
    /// How many of this stream's hashed rows were legitimately removed from the HEAD of the chain by
    /// the retention sweep. Zero until the stream is first pruned.
    /// </summary>
    /// <remarks>
    /// Retention deletes the oldest rows, which the chain alone cannot distinguish from an attacker
    /// deleting them: the surviving prefix no longer starts at the genesis, and the walked row count
    /// no longer reaches <see cref="RowCount"/>. Without this checkpoint every verification after the
    /// first purge reported tampering, which made the tamper-evidence feature useless. The retention
    /// sweep records the prune here, so verification checks
    /// <c>walked + PrunedRowCount == RowCount</c> instead of <c>walked == RowCount</c>.
    /// <see cref="RowCount"/> itself stays the stream's lifetime hashed-row total, so a genuine
    /// deletion that nothing recorded still fails the check.
    /// </remarks>
    public long PrunedRowCount { get; set; }

    /// <summary>
    /// The <see cref="AuditLog.EntryHash"/> of the newest pruned row - equivalently, the
    /// <see cref="AuditLog.PreviousHash"/> carried by the oldest surviving row. Null while the stream
    /// has never been pruned.
    /// </summary>
    /// <remarks>
    /// This is the chain's re-anchoring point. Verification expects the oldest surviving row to link
    /// to exactly this hash instead of to nothing, so the pruned prefix reads as intentional while a
    /// surviving genesis that links anywhere else is still a broken link.
    /// </remarks>
    public string? PrunedThroughHash { get; set; }
}
