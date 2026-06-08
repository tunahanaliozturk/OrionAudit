using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Moongazing.OrionAudit;

/// <summary>
/// LINQ extension methods on <see cref="DbContext"/> for querying audit history. Methods
/// automatically apply a tenant filter when an <see cref="IAuditTenantResolver"/> is registered;
/// pass <c>crossTenant: true</c> to bypass the filter.
/// </summary>
public static class AuditQueryExtensions
{
    /// <summary>
    /// Returns an <see cref="IQueryable{T}"/> over <see cref="AuditLog"/> rows filtered to
    /// entities of type <typeparamref name="T"/>. For TPH/polymorphic entities that declare a
    /// base type via <c>[Auditable(typeof(TBase))]</c> or <c>AuditTypeBuilder&lt;T&gt;.UseBaseType&lt;TBase&gt;()</c>,
    /// supplying the base type for <typeparamref name="T"/> returns rows from every subclass
    /// because the v0.7.1 capture path stamps the base type's <see cref="Type.FullName"/> on
    /// the new <see cref="AuditLog.EntityBaseType"/> column. The runtime CLR type stays on
    /// <see cref="AuditLog.EntityType"/>, so consumers can still narrow to a concrete subclass
    /// by calling <c>AuditFor&lt;ConcreteType&gt;()</c> directly.
    /// </summary>
    /// <remarks>
    /// Resolution: a row matches when <see cref="AuditLog.EntityType"/> equals the AQN of
    /// <typeparamref name="T"/> OR <see cref="AuditLog.EntityBaseType"/> equals the FullName
    /// of <typeparamref name="T"/>. Pre-v0.7.1 rows carry no <see cref="AuditLog.EntityBaseType"/>
    /// value and continue to match only via the exact-type predicate, preserving v0.7.0 query
    /// semantics for legacy data.
    /// </remarks>
    public static IQueryable<AuditLog> AuditFor<T>(this DbContext context, bool crossTenant = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        var typeName = typeof(T).AssemblyQualifiedName!;
        var baseTypeName = typeof(T).FullName!;
        return ApplyTenantFilter(
            context.Set<AuditLog>().Where(a => a.EntityType == typeName || a.EntityBaseType == baseTypeName),
            context,
            crossTenant);
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
            .FindExtension<CoreOptionsExtension>()?
            .ApplicationServiceProvider;
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
