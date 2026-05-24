using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit;

/// <summary><see cref="DbContext"/> extensions for bulk legacy-history import.</summary>
public static class AuditImportExtensions
{
    /// <summary>
    /// Creates a fresh <see cref="AuditImportBuilder"/> for this DbContext. Set
    /// <see cref="AuditImportOptions.ImportBatch"/> before calling <c>SaveAsync</c>.
    /// </summary>
    public static AuditImportBuilder CreateAuditImport(
        this DbContext context,
        Action<AuditImportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var opts = new AuditImportOptions();
        configure?.Invoke(opts);

        // IAuditConfiguration is on the application service provider — reach it through the
        // CoreOptionsExtension, the same way AuditQueryExtensions / ApplyOrionAuditConfigurations(this) do.
        var appServices = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()?
            .ApplicationServiceProvider;
        var configuration = appServices?.GetService<IAuditConfiguration>()
            ?? throw new OrionAuditConfigurationException(
                "AuditImport requires AddOrionAudit<TContext>(...) to be configured on the container.");

        return new AuditImportBuilder(
            context,
            opts,
            configuration,
            appServices?.GetService<System.Text.Json.Serialization.JsonSerializerContext>());
    }
}
