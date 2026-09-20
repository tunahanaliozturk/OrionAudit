using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Moongazing.OrionAudit;

/// <summary>
/// The single answer to "can this provider still be trusted to say who / which tenant?", shared by
/// the write path (<c>AuditSaveChangesInterceptor</c>) and the read path
/// (<c>AuditQueryExtensions.ApplyTenantFilter</c>).
/// </summary>
/// <remarks>
/// <para>
/// One implementation on purpose. The two paths resolve the same resolvers through the same
/// captured provider, and when only capture knew that a pooled registration is unattributable, the
/// read side went on filtering by a root-cached tenant and could hand one tenant another tenant's
/// history — a worse outcome than the mis-attributed write, and the two paths disagreeing about
/// what a pooled registration means is what allowed it. Anything that scopes audit rows to a tenant
/// or an actor calls this, and inherits the refusal for free.
/// </para>
/// <para>
/// <c>MaxPoolSize</c> is the positive, public signal EF Core sets for exactly
/// <c>AddDbContextPool</c> / <c>AddPooledDbContextFactory</c> (<c>AddPoolingOptions</c> →
/// <c>CoreOptionsExtension.WithMaxPoolSize</c>); it is null for <c>AddDbContext</c>. The root
/// provider itself is NOT detectable: Microsoft DI hands the same
/// <c>ServiceProviderEngineScope</c> type to the root container and every child scope, and
/// <c>IsRootScope</c> is internal — which is precisely why <c>ValidateScopes</c> exists, and why
/// non-pooled <c>AddDbContextFactory</c> cannot be caught here and is covered by documentation and
/// <c>ValidateScopes</c> instead.
/// </para>
/// </remarks>
internal static class PooledAttributionGuard
{
    /// <summary>
    /// Throws when <paramref name="services"/> cannot answer for the current request: the context is
    /// pooled (so <paramref name="services"/> is the root provider EF Core built the options from),
    /// an actor / tenant resolver is registered, and no ambient scope was pushed to supply the real
    /// one. Returns quietly in every other case — including a pushed
    /// <see cref="AuditScope.CurrentServices"/>, which is the supported way to make pooling work.
    /// </summary>
    /// <remarks>
    /// The ambient check lives in here rather than at the call sites so the two paths cannot drift
    /// on what satisfies the guard. There is deliberately no memoized "already checked" flag: the
    /// answer depends on ambient per-call state, a cached one would go stale the moment a caller
    /// stops pushing a scope, and the lookups below are two dictionary probes against a save that is
    /// about to hit the database anyway.
    /// </remarks>
    internal static void Verify(DbContext context, IServiceProvider services)
    {
        if (AuditScope.CurrentServices is not null)
        {
            return;
        }

        var pooled = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()?.MaxPoolSize is not null;
        if (!pooled || !HasAttributionResolver(services, out var inner))
        {
            return;
        }

        throw inner is null
            ? new OrionAuditConfigurationException(Message)
            : new OrionAuditConfigurationException(Message, inner);
    }

    // Is an actor / tenant resolver registered at all? Asked through IServiceProviderIsService so
    // the common answer costs no instantiation and, on a root provider under ValidateScopes, does
    // not throw before we can report anything. The resolve-and-catch fallback is for containers
    // that do not supply it: a scope-validation failure there is itself proof that a scoped
    // resolver is registered, so it becomes the inner exception of our message rather than the
    // opaque one the consumer would otherwise see.
    //
    // Both resolvers, on both paths. A pooled registration is either attributable or it is not, and
    // a read that answered differently from the write that produced the rows is the disagreement
    // this type exists to prevent.
    private static bool HasAttributionResolver(IServiceProvider services, out Exception? inner)
    {
        inner = null;
        var isService = services.GetService<IServiceProviderIsService>();
        if (isService is not null)
        {
            return isService.IsService(typeof(IAuditUserResolver))
                || isService.IsService(typeof(IAuditTenantResolver));
        }

        try
        {
            return services.GetService<IAuditUserResolver>() is not null
                || services.GetService<IAuditTenantResolver>() is not null;
        }
        catch (InvalidOperationException ex)
        {
            inner = ex;
            return true;
        }
    }

    private const string Message =
        "OrionAudit: this DbContext is registered with AddDbContextPool / AddPooledDbContextFactory, " +
        "so its DbContextOptions - and the IServiceProvider captured by UseOrionAudit(sp) - are built " +
        "once from the ROOT provider. A registered IAuditUserResolver / IAuditTenantResolver resolved " +
        "from it returns the first request's instance on every later save or query, which would stamp " +
        "every audit row with the first request's user and tenant, and filter reads by that same stale " +
        "tenant. Pick one: " +
        "(1) register the context with services.AddDbContext<TContext>((sp, o) => o.Use...().UseOrionAudit(sp)), " +
        "whose lambda runs per scope; " +
        "(2) keep pooling and make the request scope ambient - in ASP.NET Core, " +
        "app.Use(async (http, next) => { using (AuditScope.PushServices(http.RequestServices)) await next(); }), " +
        "or push the scope you created around a background unit of work; " +
        "(3) keep pooling and register no IAuditUserResolver / IAuditTenantResolver, leaving rows unattributed.";
}
