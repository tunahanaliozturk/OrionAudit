using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Moongazing.OrionAudit.AspNetCore;

/// <summary>
/// Claim-driven <see cref="IAuditUserResolver"/>. Reads the current <see cref="ClaimsPrincipal"/>
/// from <see cref="IHttpContextAccessor"/> and projects it onto
/// <see cref="AuditUser"/> using an ordered list of configurable claim types. Optional
/// <see cref="IAuditUserEnricher"/> registration adds an enrichment pass for IdP / LDAP
/// lookups that fill in display name or type from a directory.
/// </summary>
public sealed class ClaimAuditUserResolver : IAuditUserResolver
{
    private readonly ClaimAuditUserResolverOptions options;

    /// <summary>Construct with the configured options snapshot.</summary>
    public ClaimAuditUserResolver(IOptions<ClaimAuditUserResolverOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
    }

    /// <inheritdoc />
    public AuditUser? Resolve(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var accessor = serviceProvider.GetService<IHttpContextAccessor>();
        var user = accessor?.HttpContext?.User;
        if (user is null)
        {
            return null;
        }
        if (options.RequireAuthenticated && user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var id = TryFirstClaim(user, options.IdClaimTypes);
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var display = TryFirstClaim(user, options.DisplayNameClaimTypes);

        string type = options.DefaultUserType;
        if (!string.IsNullOrEmpty(options.TypeClaimType))
        {
            var typed = user.FindFirst(options.TypeClaimType)?.Value;
            if (!string.IsNullOrEmpty(typed))
            {
                type = typed;
            }
        }

        var resolved = new AuditUser(id, display, type);

        // Optional enrichment pass (registered separately as IAuditUserEnricher).
        // Consumers cache directory lookups inside their implementation because the
        // interceptor is on the SaveChanges hot path.
        var enricher = serviceProvider.GetService<IAuditUserEnricher>();
        return enricher is null ? resolved : enricher.Enrich(resolved, serviceProvider);
    }

    private static string? TryFirstClaim(ClaimsPrincipal user, IEnumerable<string> claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = user.FindFirst(claimType)?.Value;
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        return null;
    }
}
