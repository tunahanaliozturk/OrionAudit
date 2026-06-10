using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Publishing;
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
            ScanAndRegister(options);
        }

        options.ConfigurationBuilder.RegisterCustomColumns(options.CustomColumns);
        var configuration = options.ConfigurationBuilder.Build();
        services.TryAddSingleton(configuration);
        services.TryAddSingleton(options.SnapshotPolicy);
        services.TryAddSingleton(options.RetentionPolicy);
        services.TryAddSingleton(options.SweepOptions);
        services.TryAddScoped<IAuditReconstructor>(sp => new AuditReconstructor(
            sp.GetRequiredService<TDbContext>(),
            sp.GetService<System.Text.Json.Serialization.JsonSerializerContext>()));
        services.TryAddSingleton(TimeProvider.System);
        if (options.JsonContext is not null)
        {
            services.TryAddSingleton(options.JsonContext);
        }

        if (options.RetentionPolicy is not RetentionPolicy.NonePolicy)
        {
            // v0.7.8: register the default archiver so consumers that DO NOT supply a
            // custom IAuditArchiver get the v0.7.7 straight-delete behaviour. TryAdd
            // means a consumer that already registered (e.g. CopyToTableAuditArchiver)
            // wins without explicit removal.
            services.TryAddSingleton<IAuditArchiver, DeleteAuditArchiver>();
            services.AddHostedService<AuditRetentionHostedService<TDbContext>>();
        }

        if (options.AsyncCaptureEnabled)
        {
            // The interceptor's presence-check on AsyncCaptureOptions is how it switches into
            // async mode. The dispatcher is registered as a concrete singleton so both the
            // IAuditDispatcher resolution and the hosted service share one instance.
            services.TryAddSingleton(options.AsyncCaptureOptions);
            services.TryAddSingleton<Capture.AuditDispatcher<TDbContext>>();
            services.TryAddSingleton<Capture.IAuditDispatcher>(sp =>
                sp.GetRequiredService<Capture.AuditDispatcher<TDbContext>>());
            services.AddHostedService<Capture.AuditDispatcherHostedService<TDbContext>>();
        }
        else
        {
            // Always-resolvable so test code can call FlushPendingAsync unconditionally.
            services.TryAddSingleton<Capture.IAuditDispatcher, Capture.NoOpAuditDispatcher>();
        }

        if (options.UserResolverType is not null)
        {
            services.TryAddScoped(typeof(IAuditUserResolver), options.UserResolverType);
        }
        if (options.TenantResolverType is not null)
        {
            services.TryAddScoped(typeof(IAuditTenantResolver), options.TenantResolverType);
        }

        RegisterEventPublisher(services, options);

        return services;
    }

    // Publisher registration order:
    //   1) UseEventPublisher<TPublisher>() wins if set
    //   2) Otherwise UseChannelEventPublisher(...) registers ChannelAuditEventPublisher with the
    //      consumer's delegate and options
    //   3) Otherwise the NullAuditEventPublisher singleton is registered so existing consumers
    //      see zero behaviour change
    // All three are registered as singletons. ChannelAuditEventPublisher implements IAsyncDisposable
    // so the DI container drains it on shutdown.
    private static void RegisterEventPublisher(IServiceCollection services, OrionAuditOptions options)
    {
        if (options.EventPublisherType is not null)
        {
            services.TryAddSingleton(typeof(IAuditEventPublisher), options.EventPublisherType);
            return;
        }

        if (options.ChannelPublisherHandler is { } handler)
        {
            services.TryAddSingleton<IAuditEventPublisher>(sp =>
                new ChannelAuditEventPublisher(
                    handler,
                    options.ChannelPublisherOptions,
                    sp.GetService<ILogger<ChannelAuditEventPublisher>>()));
            return;
        }

        services.TryAddSingleton<IAuditEventPublisher>(NullAuditEventPublisher.Instance);
    }

    // ScanAndRegister wraps the reflective Discover call. AddOrionAudit only reaches here when
    // the consumer explicitly called o.ScanAssembly(...), which is itself
    // [RequiresUnreferencedCode]-annotated — so consumers who hit this path have already
    // accepted the AOT-unsafe surface. Suppressing the warning propagation here keeps
    // AddOrionAudit itself trim-clean for the common case.
    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute'",
        Justification = "Only reached when o.ScanAssembly was called, which is itself [RequiresUnreferencedCode]-annotated.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute'",
        Justification = "Only reached when o.ScanAssembly was called, which is itself [RequiresDynamicCode]-annotated.")]
    private static void ScanAndRegister(OrionAuditOptions options)
    {
        foreach (var type in AuditableTypeDiscovery.Discover(options.ScanAssemblies))
        {
            options.ConfigurationBuilder.Audit(type);
        }
    }
}
