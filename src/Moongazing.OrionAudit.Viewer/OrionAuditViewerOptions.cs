namespace Moongazing.OrionAudit.Viewer;

/// <summary>
/// Configures a <c>MapOrionAuditViewer</c> registration. Authorization is required by default;
/// call <see cref="AllowAnonymous"/> to opt out (dev use only).
/// </summary>
public sealed class OrionAuditViewerOptions
{
    internal string? AuthorizationPolicy { get; private set; }
    internal bool AnonymousAllowed { get; private set; }

    /// <summary>Requires the named authorization policy for every viewer endpoint.</summary>
    public OrionAuditViewerOptions RequireAuthorization(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        AuthorizationPolicy = policyName;
        AnonymousAllowed = false;
        return this;
    }

    /// <summary>
    /// Opts out of authorization, exposing the viewer to anonymous callers. Intended for local
    /// development only — never for an internet-facing deployment.
    /// </summary>
    public OrionAuditViewerOptions AllowAnonymous()
    {
        AnonymousAllowed = true;
        AuthorizationPolicy = null;
        return this;
    }
}
