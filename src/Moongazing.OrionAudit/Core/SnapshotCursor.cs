namespace Moongazing.OrionAudit;

/// <summary>
/// Tracks how many updates have occurred since the last snapshot for an audited entity, plus the
/// last snapshot's UTC timestamp. Read and written inside the interceptor's transaction so the
/// snapshot policy decision is consistent with the audit rows it produces.
/// </summary>
public sealed class SnapshotCursor
{
    /// <summary>Assembly-qualified name of the audited entity type.</summary>
    public string EntityType { get; set; } = default!;

    /// <summary>The audited entity's primary key in canonical <see cref="AuditKey"/> form.</summary>
    public string EntityId { get; set; } = default!;

    /// <summary>Optional tenant id when multi-tenancy is in play (composite-PK partition).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Number of Update audit rows written since the last snapshot.</summary>
    public int UpdatesSinceLast { get; set; }

    /// <summary>UTC timestamp of the last snapshot, or null if no snapshot has been written yet.</summary>
    public DateTime? LastSnapshotUtc { get; set; }
}
