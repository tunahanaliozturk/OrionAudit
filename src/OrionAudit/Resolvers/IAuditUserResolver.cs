namespace OrionAudit;

/// <summary>
/// Resolves the actor responsible for an audit event. Implementations are registered as scoped
/// services and called by the interceptor on every <c>SaveChangesAsync</c> that captures audit
/// rows. A null return means the event is unattributable (the <c>User*</c> columns stay null).
/// </summary>
public interface IAuditUserResolver
{
    /// <summary>Returns the user attribution for the current ambient context, or null if unknown.</summary>
    /// <param name="serviceProvider">Scoped service provider for resolving collaborators (e.g. <c>IHttpContextAccessor</c>).</param>
    AuditUser? Resolve(IServiceProvider serviceProvider);
}
