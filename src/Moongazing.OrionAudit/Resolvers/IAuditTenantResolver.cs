namespace Moongazing.OrionAudit;

/// <summary>
/// Resolves the tenant id for an audit event. Implementations are registered as scoped services
/// and called by the interceptor when capturing audit rows, and by the read-side query extensions
/// when scoping a read.
/// </summary>
/// <remarks>
/// A null return means "no tenant": the captured row's <c>TenantId</c> is persisted as the canonical
/// empty string, and a read is scoped to that same no-tenant stream. A null therefore never widens a
/// read to other tenants - registering a resolver that cannot name a tenant denies tenant-stamped
/// rows rather than handing back all of them.
/// </remarks>
public interface IAuditTenantResolver
{
    /// <summary>Returns the tenant id for the current ambient context, or null when there is none.</summary>
    /// <param name="serviceProvider">Scoped service provider for resolving collaborators.</param>
    string? Resolve(IServiceProvider serviceProvider);
}
