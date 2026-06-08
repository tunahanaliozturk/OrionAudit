namespace Moongazing.OrionAudit;

/// <summary>
/// Persisted record of a single Insert / Update / Delete against an audited entity. Written by
/// <c>AuditSaveChangesInterceptor</c> in the same transaction as the originating entity change.
/// </summary>
public sealed class AuditLog
{
    /// <summary>Unique row id (auto-assigned).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Assembly-qualified name of the audited entity type.</summary>
    public string EntityType { get; set; } = default!;

    /// <summary>
    /// Optional name of the base type for TPH / polymorphic capture. When the captured entity's
    /// configuration declares a base type via <c>[Auditable(typeof(TBase))]</c> or
    /// <c>AuditTypeBuilder.UseBaseType&lt;TBase&gt;()</c>, this column carries the base type's
    /// <see cref="Type.FullName"/>; otherwise it stays <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// v0.7.1 ships the capture-side stamping and the schema column. Inheritance-aware querying
    /// (so <c>AuditFor&lt;Document&gt;()</c> returns rows for every subclass) lands in v0.7.2.
    /// </remarks>
    public string? EntityBaseType { get; set; }

    /// <summary>Serialized primary key of the audited entity (<c>key.ToString()</c>).</summary>
    public string EntityId { get; set; } = default!;

    /// <summary>What kind of change this row records.</summary>
    public AuditAction Action { get; set; }

    /// <summary>UTC timestamp at which the change was captured.</summary>
    public DateTime OccurredOnUtc { get; set; }

    /// <summary>Optional user id (from <see cref="IAuditUserResolver"/>); null when unattributed.</summary>
    public string? UserId { get; set; }

    /// <summary>Optional human-readable user display name.</summary>
    public string? UserDisplay { get; set; }

    /// <summary>Classification: <c>"user"</c>, <c>"system"</c>, <c>"job"</c>, etc.</summary>
    public string? UserType { get; set; }

    /// <summary>Optional tenant id (from <see cref="IAuditTenantResolver"/>); null for single-tenant apps.</summary>
    public string? TenantId { get; set; }

    /// <summary>Optional W3C trace context id (<c>Activity.Current?.Id</c>) at capture time.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>JSON Patch operations array (RFC 6902) describing the change. Empty array if diff failed.</summary>
    public string Diff { get; set; } = "[]";

    /// <summary>
    /// Last-known full entity JSON. Populated for <see cref="AuditAction.Deleted"/> in v0.1.0
    /// to enable reconstruction; null otherwise.
    /// </summary>
    public string? Snapshot { get; set; }

    /// <summary>
    /// Non-null when diff computation failed. The row is still written so the audit chain is not
    /// broken; operators see the error via telemetry and can investigate.
    /// </summary>
    public string? Error { get; set; }
}
