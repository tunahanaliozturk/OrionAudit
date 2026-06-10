namespace Moongazing.OrionAudit;

/// <summary>
/// Optional post-resolution enrichment hook for <see cref="IAuditUserResolver"/>
/// implementations. Registered as scoped; called once per resolved
/// <see cref="AuditUser"/> so consumers can fill in display name / type / additional
/// metadata from an identity provider (Azure AD, Okta, LDAP, etc.).
/// </summary>
/// <remarks>
/// <para>
/// The contract is intentionally synchronous so it composes with the synchronous
/// <see cref="IAuditUserResolver.Resolve"/> path called by the interceptor on every
/// <c>SaveChangesAsync</c>. For directory lookups that involve network I/O (LDAP, Graph
/// API), consumers MUST cache results in-process; the audit interceptor is on the hot path.
/// A typical implementation is a 5-minute <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>
/// keyed by <see cref="AuditUser.Id"/>.
/// </para>
/// <para>
/// Returning <see langword="null"/> drops attribution entirely - the audit row's User
/// columns are left null, and the pre-enrichment values from the resolver are NOT
/// preserved (the enricher's return value wins). To fall back to the raw claim values on
/// directory failure, the implementation MUST catch the exception and return the
/// original <paramref name="user"/> instead. Throwing aborts the entire
/// <c>SaveChangesAsync</c>; wrap in try/catch inside the enricher if directory failures
/// should be treated as best-effort rather than fatal.
/// </para>
/// </remarks>
public interface IAuditUserEnricher
{
    /// <summary>
    /// Return an enriched <see cref="AuditUser"/> (or the original) given the user produced
    /// by the resolver. Implementations are free to look up the directory entry, replace
    /// the display name, or change the type classification.
    /// </summary>
    AuditUser? Enrich(AuditUser user, IServiceProvider serviceProvider);
}
