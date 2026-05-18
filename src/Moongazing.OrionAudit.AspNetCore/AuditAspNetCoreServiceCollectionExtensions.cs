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
}
