using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit;

/// <summary>EF Core <see cref="DbContextOptionsBuilder"/> extensions for OrionAudit.</summary>
public static class DbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Wires the <see cref="AuditSaveChangesInterceptor"/> into the DbContext's interceptor pipeline.
    /// Call inside <c>services.AddDbContext&lt;T&gt;((sp, o) =&gt; ...)</c> after the provider-specific
    /// <c>Use*</c> call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Which registration you use decides where attribution comes from.</strong> The
    /// interceptor keeps <paramref name="serviceProvider"/> for the life of the options object, and
    /// EF Core builds those options with a different lifetime per registration:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <c>services.AddDbContext&lt;T&gt;((sp, o) =&gt; ...)</c> — <c>DbContextOptions</c> are
    /// <strong>scoped</strong>, so the lambda runs once per scope and <c>sp</c> <em>is</em> the
    /// request scope. Registered <see cref="IAuditUserResolver"/> / <see cref="IAuditTenantResolver"/>
    /// implementations resolve per request and attribution is correct with nothing else to do.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>services.AddDbContextPool&lt;T&gt;(...)</c> and
    /// <c>services.AddPooledDbContextFactory&lt;T&gt;(...)</c> — <c>DbContextOptions</c> are
    /// <strong>singleton</strong>: the lambda runs once, from the <strong>root</strong> provider, and
    /// <c>sp</c> stays the root for the life of the process. A scoped resolver pulled out of it is the
    /// first request's instance on every later save. Push the request scope with
    /// <see cref="AuditScope.PushServices"/> and capture uses that instead; if you do not, and a
    /// resolver is registered, the first audited save throws
    /// <see cref="OrionAuditConfigurationException"/> rather than write a wrong trail.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>services.AddDbContextFactory&lt;T&gt;(...)</c> — the same root-provider capture, because
    /// <c>optionsLifetime</c> defaults to <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton"/>;
    /// a factory-made context is also not tied to any scope in the first place. Use
    /// <see cref="AuditScope.PushServices"/>, or pass
    /// <c>optionsLifetime: ServiceLifetime.Scoped</c> to get the per-scope behaviour above. Unlike
    /// pooling this one cannot be detected at runtime (Microsoft DI does not let a library tell its
    /// root provider apart from a scope), so it is on the caller — run with <c>ValidateScopes</c>
    /// enabled and a scoped resolver will fail loudly.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// The single-argument <c>AddDbContext&lt;T&gt;(o =&gt; ...)</c> overload is not compatible at
    /// all — there is no provider to hand in.
    /// </para>
    /// </remarks>
    /// <param name="builder">The options builder.</param>
    /// <param name="serviceProvider">
    /// The service provider EF Core passes to the options lambda. It is the per-request scope only
    /// for <c>AddDbContext&lt;T&gt;((sp, o) =&gt; ...)</c>; see the remarks for what the pooled and
    /// factory registrations need instead.
    /// </param>
    public static DbContextOptionsBuilder UseOrionAudit(
        this DbContextOptionsBuilder builder,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        builder.AddInterceptors(new AuditSaveChangesInterceptor(serviceProvider));
        return builder;
    }

    /// <summary>
    /// Strongly-typed convenience overload. The attribution rules per registration are identical —
    /// see <see cref="UseOrionAudit(DbContextOptionsBuilder, IServiceProvider)"/>.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseOrionAudit<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        IServiceProvider serviceProvider)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        builder.AddInterceptors(new AuditSaveChangesInterceptor(serviceProvider));
        return builder;
    }
}
