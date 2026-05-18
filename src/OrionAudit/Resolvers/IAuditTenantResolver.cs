namespace OrionAudit;

/// <summary>
/// Resolves the tenant id for an audit event. Implementations are registered as scoped services
/// and called by the interceptor when capturing audit rows. A null return means single-tenant
/// (the <c>TenantId</c> column stays null and reads are not filtered by tenant).
/// </summary>
public interface IAuditTenantResolver
{
    /// <summary>Returns the tenant id for the current ambient context, or null for single-tenant.</summary>
    /// <param name="serviceProvider">Scoped service provider for resolving collaborators.</param>
    string? Resolve(IServiceProvider serviceProvider);
}
