using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace OrionAudit;

/// <summary>
/// LINQ extension methods on <see cref="DbContext"/> for querying audit history. Methods
/// automatically apply a tenant filter when an <see cref="IAuditTenantResolver"/> is registered;
/// pass <c>crossTenant: true</c> to bypass the filter.
/// </summary>
public static class AuditQueryExtensions
{
    /// <summary>Returns an <see cref="IQueryable{T}"/> over <see cref="AuditLog"/> rows filtered to entities of type <typeparamref name="T"/>.</summary>
    public static IQueryable<AuditLog> AuditFor<T>(this DbContext context, bool crossTenant = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        var typeName = typeof(T).AssemblyQualifiedName!;
        return ApplyTenantFilter(context.Set<AuditLog>().Where(a => a.EntityType == typeName), context, crossTenant);
    }

    /// <summary>Returns an unfiltered <see cref="IQueryable{T}"/> over the entire audit table.</summary>
    public static IQueryable<AuditLog> AuditLog(this DbContext context, bool crossTenant = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ApplyTenantFilter(context.Set<AuditLog>(), context, crossTenant);
    }

    private static IQueryable<AuditLog> ApplyTenantFilter(IQueryable<AuditLog> query, DbContext context, bool crossTenant)
    {
        if (crossTenant)
        {
            return query;
        }
        var appServiceProvider = context.GetService<IDbContextOptions>()
            .Extensions
            .OfType<CoreOptionsExtension>()
            .FirstOrDefault()?.ApplicationServiceProvider;
        if (appServiceProvider is null)
        {
            return query;
        }
        var resolver = appServiceProvider.GetService<IAuditTenantResolver>();
        if (resolver is null)
        {
            return query;
        }
        var tenantId = resolver.Resolve(appServiceProvider);
        if (tenantId is null)
        {
            return query;
        }
        return query.Where(a => a.TenantId == tenantId);
    }
}
