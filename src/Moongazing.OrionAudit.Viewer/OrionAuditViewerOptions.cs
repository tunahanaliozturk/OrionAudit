using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;

namespace Moongazing.OrionAudit.Viewer;

/// <summary>
/// Configures a <c>MapOrionAuditViewer</c> registration. An access decision is mandatory:
/// call <see cref="RequireAuthorization(string)"/>, <see cref="RequireAuthorization(Action{AuthorizationPolicyBuilder})"/>,
/// or <see cref="AllowAnonymous"/>. <c>MapOrionAuditViewer</c> throws when none of them was called.
/// </summary>
public sealed class OrionAuditViewerOptions
{
    /// <summary>
    /// The access decision the consumer stated, applied to the viewer's endpoint group.
    /// <see langword="null"/> means no decision was stated and the registration must fail.
    /// </summary>
    internal Action<IEndpointConventionBuilder>? AccessDecision { get; private set; }

    /// <summary>Requires the named authorization policy for every viewer endpoint.</summary>
    public OrionAuditViewerOptions RequireAuthorization(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        AccessDecision = group => group.RequireAuthorization(policyName);
        return this;
    }

    /// <summary>
    /// Requires an inline authorization policy for every viewer endpoint. Use this to state a
    /// decision without registering a named policy — including the plain "any authenticated
    /// user" rule: <c>o.RequireAuthorization(p =&gt; p.RequireAuthenticatedUser())</c>.
    /// </summary>
    public OrionAuditViewerOptions RequireAuthorization(Action<AuthorizationPolicyBuilder> configurePolicy)
    {
        ArgumentNullException.ThrowIfNull(configurePolicy);
        AccessDecision = group => group.RequireAuthorization(configurePolicy);
        return this;
    }

    /// <summary>
    /// Opts out of authorization, exposing the viewer to anonymous callers. Intended for local
    /// development only — never for an internet-facing deployment.
    /// </summary>
    public OrionAuditViewerOptions AllowAnonymous()
    {
        AccessDecision = group => group.AllowAnonymous();
        return this;
    }
}
