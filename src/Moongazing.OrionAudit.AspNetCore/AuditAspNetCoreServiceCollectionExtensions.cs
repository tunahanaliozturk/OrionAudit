using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Moongazing.OrionAudit.AspNetCore;

/// <summary>DI helpers for the ASP.NET Core integration package.</summary>
public static class AuditAspNetCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="HttpContextAuditUserResolver"/> as the default <see cref="IAuditUserResolver"/>
    /// and ensures <see cref="IHttpContextAccessor"/> is available. Idempotent.
    /// </summary>
    public static IServiceCollection AddOrionAuditAspNetCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.TryAddScoped<IAuditUserResolver, HttpContextAuditUserResolver>();
        return services;
    }

    /// <summary>
    /// Replaces the default <see cref="HttpContextAuditUserResolver"/> with the
    /// configurable <see cref="ClaimAuditUserResolver"/> that tries an ordered list of
    /// claim types for id / display name / type. Use this when your tenant's claim
    /// shape differs from the OIDC <c>sub</c> + <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>
    /// defaults baked into the simple resolver (e.g. Azure AD's <c>oid</c>, single-tenant
    /// upn-only providers, employee-id-as-username schemes).
    /// </summary>
    /// <remarks>
    /// Register an <see cref="IAuditUserEnricher"/> as scoped service after this call to
    /// enrich the resolved <see cref="AuditUser"/> from an IdP / LDAP directory. The
    /// enricher is synchronous; its implementation MUST cache directory lookups because
    /// the audit interceptor is on the SaveChanges hot path.
    /// </remarks>
    public static IServiceCollection AddOrionAuditClaimResolver(
        this IServiceCollection services,
        Action<ClaimAuditUserResolverOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        // Wire IOptions<ClaimAuditUserResolverOptions> unconditionally so the resolver's
        // constructor can resolve it even when the caller passes no configure callback.
        services.AddOptions<ClaimAuditUserResolverOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }
        // Reject any previously-wired resolver (typically HttpContextAuditUserResolver) so
        // the claim-driven one becomes the active surface. RemoveAll keeps the call
        // idempotent across repeat AddOrionAuditClaimResolver invocations.
        services.RemoveAll<IAuditUserResolver>();
        services.AddScoped<IAuditUserResolver, ClaimAuditUserResolver>();
        return services;
    }
}
