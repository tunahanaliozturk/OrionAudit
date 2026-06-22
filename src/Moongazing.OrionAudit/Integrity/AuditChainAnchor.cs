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
/// writer takes a row lock on this anchor inside the consumer's <c>SaveChanges</c> transaction, so
/// same-stream appends serialize on it while different streams stay parallel.
/// </item>
/// <item>
/// <b>Truncation / deletion.</b> A consistent prefix of a chain still verifies, so deleting the tail
/// row(s) - or an entire stream - is otherwise invisible. The anchor records the expected tail hash
/// and row count, so verification flags a stream whose walked tail/count disagrees with the anchor,
/// and flags a stream that has been removed entirely while its anchor still exists.
/// </item>
/// </list>
/// </remarks>
public sealed class AuditChainAnchor
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
}
