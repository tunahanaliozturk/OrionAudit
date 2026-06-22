namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Walks the tamper-evident hash chain over recorded <see cref="AuditLog"/> rows and reports whether
/// it is intact, or the first row at which it broke. Separate from <c>IAuditHistoryStore</c> so the
/// read/maintenance surface stays focused and a consumer can register a verifier independently.
/// </summary>
/// <remarks>
/// Verification is read-only and idempotent: it recomputes each hashed row's
/// <see cref="AuditLog.EntryHash"/> from the row's content plus its <see cref="AuditLog.PreviousHash"/>,
/// and checks each link against the actual preceding row in chain order. Rows written before
/// hash-chaining was enabled (null <see cref="AuditLog.EntryHash"/>) form an unverified prefix that
/// is skipped; verification begins at each stream's first hashed (genesis) row.
/// </remarks>
public interface IAuditIntegrityVerifier
{
    /// <summary>
    /// Verifies the hash chain for the rows selected by <paramref name="request"/> (a single entity
    /// stream, or every stream when unfiltered).
    /// </summary>
    /// <param name="request">Which rows to verify. Use <see cref="AuditChainVerificationRequest.ForEntity"/>
    /// to scope to one entity stream, or <see cref="AuditChainVerificationRequest.All"/> for the whole table.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A valid result, or the first broken link with its row id and reason.</returns>
    Task<AuditChainVerificationResult> VerifyChainAsync(
        AuditChainVerificationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Selects which rows <see cref="IAuditIntegrityVerifier.VerifyChainAsync"/> walks. Scopes to a
/// single entity stream, optionally within one tenant, or to the whole table.
/// </summary>
public sealed record AuditChainVerificationRequest
{
    /// <summary>
    /// Assembly-qualified entity type to verify (matches <see cref="AuditLog.EntityType"/>), or null
    /// to verify every type. When null, <see cref="EntityId"/> must also be null.
    /// </summary>
    public string? EntityType { get; init; }

    /// <summary>
    /// Entity primary key to verify (matches <see cref="AuditLog.EntityId"/>), or null to verify
    /// every entity of <see cref="EntityType"/>.
    /// </summary>
    public string? EntityId { get; init; }

    /// <summary>
    /// Optional tenant scope (matches <see cref="AuditLog.TenantId"/>). Null verifies across tenants.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>Verifies the chain for one entity stream.</summary>
    /// <exception cref="ArgumentException"><paramref name="entityType"/> or <paramref name="entityId"/>
    /// is null, empty, or whitespace-only. A whitespace-only key would silently match no stream (or the
    /// wrong one), so it is rejected up front rather than producing a misleading "valid" result.</exception>
    public static AuditChainVerificationRequest ForEntity(string entityType, string entityId, string? tenantId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        return new AuditChainVerificationRequest { EntityType = entityType, EntityId = entityId, TenantId = tenantId };
    }

    /// <summary>Verifies every stream in the table (optionally within one tenant).</summary>
    public static AuditChainVerificationRequest All(string? tenantId = null)
        => new() { TenantId = tenantId };

    /// <summary>Validates the request, throwing <see cref="ArgumentException"/> on an inconsistent shape.</summary>
    /// <exception cref="ArgumentException"><see cref="EntityId"/> is set without <see cref="EntityType"/>,
    /// or either is present but whitespace-only (which would match no stream).</exception>
    public void Validate()
    {
        if (EntityId is not null && EntityType is null)
        {
            throw new ArgumentException(
                "AuditChainVerificationRequest.EntityId requires EntityType to be set.", nameof(EntityType));
        }
        // A present-but-blank key is never legitimate: it cannot match a real stream and would make a
        // scoped verify silently pass. Reject it so a malformed request fails loudly. (null stays
        // valid - it means "every type" / "every entity".)
        if (EntityType is not null && string.IsNullOrWhiteSpace(EntityType))
        {
            throw new ArgumentException(
                "AuditChainVerificationRequest.EntityType must not be whitespace-only.", nameof(EntityType));
        }
        if (EntityId is not null && string.IsNullOrWhiteSpace(EntityId))
        {
            throw new ArgumentException(
                "AuditChainVerificationRequest.EntityId must not be whitespace-only.", nameof(EntityId));
        }
    }
}
