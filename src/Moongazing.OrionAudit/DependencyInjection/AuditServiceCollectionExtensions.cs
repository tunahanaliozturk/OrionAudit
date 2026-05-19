using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Read;
using Moongazing.OrionAudit.Retention;

namespace Moongazing.OrionAudit;

/// <summary><see cref="IServiceCollection"/> extensions to wire OrionAudit.</summary>
public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers the audit configuration, reconstructor, and optional resolvers for the
    /// supplied <typeparamref name="TDbContext"/>. Call before
    /// <c>services.AddDbContext&lt;TDbContext&gt;(...)</c>.
    /// </summary>
    public static IServiceCollection AddOrionAudit<TDbContext>(
        this IServiceCollection services,
        Action<OrionAuditOptions> configure)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new OrionAuditOptions();
        configure(options);

        if (options.ScanAssemblies.Count > 0)
        {
            foreach (var type in AuditableTypeDiscovery.Discover(options.ScanAssemblies))
            {
                options.ConfigurationBuilder.Audit(type);
            }
        }

        var configuration = options.ConfigurationBuilder.Build();
        services.TryAddSingleton(configuration);
        services.TryAddSingleton(options.SnapshotPolicy);
        services.TryAddSingleton(options.RetentionPolicy);
        services.TryAddSingleton(options.SweepOptions);
        services.TryAddScoped<IAuditReconstructor>(sp => new AuditReconstructor(sp.GetRequiredService<TDbContext>()));
        services.TryAddSingleton(TimeProvider.System);

        if (options.RetentionPolicy is not RetentionPolicy.NonePolicy)
        {
            services.AddHostedService<AuditRetentionHostedService<TDbContext>>();
        }

        if (options.UserResolverType is not null)
        {
            services.TryAddScoped(typeof(IAuditUserResolver), options.UserResolverType);
        }
        if (options.TenantResolverType is not null)
        {
            services.TryAddScoped(typeof(IAuditTenantResolver), options.TenantResolverType);
        }

        return services;
    }
}
