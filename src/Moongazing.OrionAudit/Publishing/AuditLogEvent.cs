namespace Moongazing.OrionAudit.Publishing;

/// <summary>
/// Wire-shape projection of an <see cref="AuditLog"/> row, handed to
/// <see cref="IAuditEventPublisher.PublishAsync"/> after capture has prepared the row but before
/// <c>SaveChanges</c> returns. The shape mirrors <see cref="AuditLog"/> but stays decoupled from
/// the EF entity so downstream consumers (broker bindings, search indexers, webhooks) do not
/// take a dependency on the persisted entity type.
/// </summary>
/// <param name="AuditLogId">The <see cref="AuditLog.Id"/> the publisher's event corresponds to.</param>
/// <param name="EntityType">Assembly-qualified name of the audited entity type.</param>
/// <param name="EntityKey">Serialized primary key of the audited entity.</param>
/// <param name="Action">String form of <see cref="AuditAction"/>: <c>Inserted</c>, <c>Updated</c>, <c>Deleted</c>, or <c>SoftDeleted</c>.</param>
/// <param name="At">UTC timestamp at which the change was captured.</param>
/// <param name="TenantId">Optional tenant id (null in single-tenant apps).</param>
/// <param name="UserId">Optional user id (null when unattributed).</param>
/// <param name="CorrelationId">Optional W3C trace id / <see cref="AuditScope"/> correlation id.</param>
/// <param name="Diff">
/// JSON Patch (RFC 6902) operations array for <c>Updated</c> rows in sync-capture mode; null in
/// async-capture mode because the dispatcher computes the diff after capture (the publisher fires
/// from the dispatcher's transaction in that mode and the diff is available there). Inserted and
/// Deleted rows may also be null when the consumer's snapshot policy does not stamp a diff.
/// </param>
public sealed record AuditLogEvent(
    Guid AuditLogId,
    string EntityType,
    string EntityKey,
    string Action,
    DateTimeOffset At,
    string? TenantId,
    string? UserId,
    string? CorrelationId,
    string? Diff);
