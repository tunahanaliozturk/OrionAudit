using Microsoft.EntityFrameworkCore;

namespace OrionAudit;

/// <summary>EF Core <see cref="ModelBuilder"/> extensions for OrionAudit.</summary>
public static class AuditModelBuilderExtensions
{
    /// <summary>
    /// Applies the <see cref="AuditLogEntityTypeConfiguration"/> to the model. Call from
    /// <c>DbContext.OnModelCreating</c>. Uses the default table name <c>OrionAudit_Log</c>; pass
    /// <paramref name="tableName"/> to override.
    /// </summary>
    public static ModelBuilder ApplyOrionAuditConfigurations(this ModelBuilder modelBuilder, string? tableName = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var config = tableName is null
            ? new AuditLogEntityTypeConfiguration()
            : new AuditLogEntityTypeConfiguration(tableName);
        modelBuilder.ApplyConfiguration(config);
        return modelBuilder;
    }
}
