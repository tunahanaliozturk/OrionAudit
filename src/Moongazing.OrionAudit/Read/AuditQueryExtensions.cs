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
    /// <para>
    /// The base-type predicate uses <see cref="Type.FullName"/> to stay byte-compatible with
    /// the value the v0.7.1 capture path stamped. In the rare case where two loaded assemblies
    /// define the same namespace-qualified base type, rows from both assemblies satisfy the
    /// predicate. This matches what the v0.7.1 storage layer recorded; strict cross-assembly
    /// disambiguation is considered for a future minor that introduces an AQN variant.
    /// </para>
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
        // Same trap as capture, on the read side: ApplicationServiceProvider is whatever
        // UseOrionAudit(sp) was handed, which under AddDbContextPool / AddDbContextFactory is the
        // ROOT provider - so a scoped IAuditTenantResolver pulled out of it answers with the first
        // request's tenant and this filter would show one tenant another tenant's history. The
        // ambient scope wins here for the same reason it wins in the interceptor.
        var appServiceProvider = AuditScope.CurrentServices
            ?? context.GetService<IDbContextOptions>()
                .FindExtension<CoreOptionsExtension>()?
                .ApplicationServiceProvider;
        if (appServiceProvider is null)
        {
            // No provider to ask - a context built outside DI. There is no resolver to answer
            // stalely either, so there is nothing to scope to and nothing to refuse.
            return query;
        }
        // Before trusting that provider to name a tenant, the same check capture makes before
        // trusting it to name an actor. Without it a pooled registration with scope validation off
        // filtered by a root-cached tenant and returned ANOTHER tenant's history - the same defect
        // as the mis-attributed write, on the path where the consequence is a cross-tenant read.
        // It sits here, in the one place every tenant-scoped read funnels through, rather than at
        // AuditFor/AuditLog: anything routed through this helper later inherits the refusal.
        PooledAttributionGuard.Verify(context, appServiceProvider);
        var resolver = appServiceProvider.GetService<IAuditTenantResolver>();
        if (resolver is null)
        {
            // No resolver registered at all: the application is not multi-tenant, nothing was ever
            // stamped with a tenant, and there is no tenant to scope to. Unfiltered is correct here.
            return query;
        }
        var tenantId = resolver.Resolve(appServiceProvider);
        if (tenantId is null)
        {
            // A resolver IS registered but could not name a tenant for this call - a missing claim,
            // a background thread with no ambient context, a header the gateway dropped. This used
            // to fall through unfiltered, which handed the caller EVERY tenant's audit rows: the
            // read failed open precisely when the caller's identity was unknown. Deny instead, by
            // scoping to the no-tenant stream.
            //
            // Empty result rather than a throw: the library's exceptions (OrionAuditConfigurationException,
            // OrionAuditChainKeyException, the argument guards on the query DSL) all fire at
            // configuration or programming boundaries, never on ambient per-request state, and these
            // extensions sit on request paths (the viewer, operator dashboards) where turning an
            // unresolved tenant into a 500 trades a leak for an outage.
            //
            // The shape of the deny mirrors the write path: AuditTenant.Canonical persists an
            // unresolved tenant as "", and the integrity verifier already matches the no-tenant
            // stream as (null OR ""). Scoping the read to exactly that set is the read-side mirror
            // of what was written - the empty set in any tenant-stamped deployment, and still the
            // full history for a genuinely single-tenant one (a resolver that returns null by
            // design, as IAuditTenantResolver documents). No tenant-stamped row can escape either way.
            return query.Where(a => a.TenantId == null || a.TenantId == "");
        }
        return query.Where(a => a.TenantId == tenantId);
    }
}
