using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionAudit.Viewer;

/// <summary><see cref="IEndpointRouteBuilder"/> extensions that mount the OrionAudit viewer.</summary>
public static class OrionAuditViewerEndpointExtensions
{
    internal const string MissingAccessDecisionMessage =
        "MapOrionAuditViewer requires an explicit access decision. The audit trail exposes every " +
        "recorded change of every audited entity — including other users' actions and values that " +
        "redaction exists to protect — so it will not mount on the implicit \"any authenticated " +
        "user\" default. State the decision at the call site, for example:\n" +
        "  o => o.RequireAuthorization(\"AuditViewers\")                    (a policy you registered with AddAuthorization)\n" +
        "  o => o.RequireAuthorization(p => p.RequireRole(\"Auditor\"))      (an inline policy)\n" +
        "  o => o.RequireAuthorization(p => p.RequireAuthenticatedUser())  (the former default, stated explicitly)\n" +
        "  o => o.AllowAnonymous()                                         (no authorization at all — local development only)";

    /// <summary>
    /// Mounts the audit viewer — a JSON API and a built-in static UI — under
    /// <paramref name="pathPrefix"/>, reading audit data from <typeparamref name="TDbContext"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="configure"/> stated no access decision. Audit data is sensitive, so the
    /// viewer refuses to register until the caller picks one:
    /// <see cref="OrionAuditViewerOptions.RequireAuthorization(string)"/>,
    /// <see cref="OrionAuditViewerOptions.RequireAuthorization(Action{Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder})"/>,
    /// or <see cref="OrionAuditViewerOptions.AllowAnonymous"/>.
    /// </exception>
    public static IEndpointConventionBuilder MapOrionAuditViewer<TDbContext>(
        this IEndpointRouteBuilder endpoints,
        string pathPrefix,
        Action<OrionAuditViewerOptions>? configure = null)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathPrefix);

        var options = new OrionAuditViewerOptions();
        configure?.Invoke(options);

        var accessDecision = options.AccessDecision
            ?? throw new InvalidOperationException(MissingAccessDecisionMessage);

        var prefix = pathPrefix.TrimEnd('/');
        var group = endpoints.MapGroup(prefix);

        OrionAuditViewerApi.Map<TDbContext>(group);
        OrionAuditViewerStaticFiles.Map(group);

        accessDecision(group);

        return group;
    }
}
