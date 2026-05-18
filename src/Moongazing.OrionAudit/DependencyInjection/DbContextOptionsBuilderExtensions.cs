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
    /// <param name="builder">The options builder.</param>
    /// <param name="serviceProvider">
    /// The <strong>scoped</strong> service provider passed by EF Core's
    /// <c>services.AddDbContext&lt;T&gt;((sp, o) =&gt; ...)</c> overload. The single-argument
    /// <c>AddDbContext&lt;T&gt;(o =&gt; ...)</c> overload is not compatible — the interceptor
    /// must resolve scoped collaborators from the per-request scope.
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

    /// <summary>Strongly-typed convenience overload.</summary>
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
