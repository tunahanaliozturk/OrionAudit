using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionAudit.Viewer;

/// <summary><see cref="IEndpointRouteBuilder"/> extensions that mount the OrionAudit viewer.</summary>
public static class OrionAuditViewerEndpointExtensions
{
    /// <summary>
    /// Mounts the audit viewer — a JSON API and a built-in static UI — under
    /// <paramref name="pathPrefix"/>, reading audit data from <typeparamref name="TDbContext"/>.
    /// Authorization is required unless <see cref="OrionAuditViewerOptions.AllowAnonymous"/> is called.
    /// </summary>
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

        var prefix = pathPrefix.TrimEnd('/');
        var group = endpoints.MapGroup(prefix);

        OrionAuditViewerApi.Map<TDbContext>(group);
        OrionAuditViewerStaticFiles.Map(group);

        if (options.AnonymousAllowed)
        {
            group.AllowAnonymous();
        }
        else if (options.AuthorizationPolicy is { } policy)
        {
            group.RequireAuthorization(policy);
        }
        else
        {
            group.RequireAuthorization();   // default: any authenticated user
        }

        return group;
    }
}
