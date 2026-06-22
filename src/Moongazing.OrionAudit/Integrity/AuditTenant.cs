namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Single source of truth for the canonical "no tenant" representation used by the tamper-evident
/// chain. A chain stream is keyed by (<see cref="AuditLog.EntityType"/>, <see cref="AuditLog.EntityId"/>,
/// TenantId); a row with no tenant and a row with an empty-string tenant must therefore resolve to the
/// <em>same</em> stream, or the same logical chain would split into two and verification would both
/// double-count it and fail to find its anchor.
/// </summary>
/// <remarks>
/// <para>
/// The canonical value is <see cref="string.Empty"/> (never <see langword="null"/>). It is applied at
/// the write path so freshly captured <see cref="AuditLog"/> rows and their <see cref="AuditChainAnchor"/>
/// always persist the same value, and again on the read/verify path so a <see cref="string"/> key is
/// never null and the row/anchor stream union dedupes correctly.
/// </para>
/// <para>
/// A value converter on <c>AuditLog.TenantId</c> was deliberately rejected for this: EF Core never
/// passes <see langword="null"/> to a value converter (a null column is always a null in the entity and
/// vice-versa), so a converter cannot fold <see langword="null"/> into <c>""</c>. The
/// <c>convertsNulls</c> escape hatch is <c>[EntityFrameworkInternal]</c>, raises an EF1001 warning (fatal
/// under <c>TreatWarningsAsErrors</c>), and is documented as generating broken queries. Normalizing the
/// CLR value before it is persisted is the correct, query-safe boundary.
/// </para>
/// <para>
/// Historic rows written before this normalization may still physically store <see langword="null"/> in
/// the TenantId column. The converter does not rewrite data at rest, so the verifier must remain tolerant
/// of both <see langword="null"/> and <c>""</c> for the no-tenant stream — see the matching logic in
/// <c>EfCoreAuditIntegrityVerifier</c>.
/// </para>
/// </remarks>
internal static class AuditTenant
{
    /// <summary>
    /// Folds a raw tenant id to its canonical persisted form: <see langword="null"/> becomes
    /// <see cref="string.Empty"/>; any other value (including an already-empty string) is returned
    /// unchanged. Whitespace is intentionally preserved — a non-empty tenant is opaque and must MAC and
    /// match byte-for-byte.
    /// </summary>
    /// <param name="tenantId">The raw tenant id from a resolver, a captured row, or a stream key.</param>
    /// <returns>The canonical, never-null tenant id for chain keying and persistence.</returns>
    public static string Canonical(string? tenantId) => tenantId ?? string.Empty;
}
