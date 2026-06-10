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
/// Returning <see langword="null"/> drops attribution entirely (the audit row keeps the
/// pre-enrichment values from the resolver). Throwing aborts the SaveChanges; wrap in
/// try/catch inside the implementation if directory failures should fall back to raw
/// claim values.
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
