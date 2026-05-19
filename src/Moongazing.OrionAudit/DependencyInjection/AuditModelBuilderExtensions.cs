using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionAudit;

/// <summary>EF Core <see cref="ModelBuilder"/> extensions for OrionAudit.</summary>
public static class AuditModelBuilderExtensions
{
    /// <summary>
    /// Applies the OrionAudit entity-type configurations to the model. Call from
    /// <c>DbContext.OnModelCreating</c>. Always maps <see cref="AuditLog"/>; also maps the
    /// <see cref="SnapshotCursor"/> companion table (harmless when periodic snapshotting is
    /// not configured — the table simply stays empty).
    /// </summary>
    /// <param name="modelBuilder">The EF Core model builder.</param>
    /// <param name="auditLogTableName">Override the default <c>OrionAudit_Log</c> table name.</param>
    /// <param name="snapshotCursorTableName">Override the default <c>OrionAudit_Snapshot_Cursors</c> table name.</param>
    /// <param name="columnHints">Provider-specific column-type hints for <c>Diff</c> and <c>Snapshot</c> (default: <see cref="OrionAuditColumnHints.Auto"/>).</param>
    public static ModelBuilder ApplyOrionAuditConfigurations(
        this ModelBuilder modelBuilder,
        string? auditLogTableName = null,
        string? snapshotCursorTableName = null,
        OrionAuditColumnHints columnHints = OrionAuditColumnHints.Auto)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var auditLog = new AuditLogEntityTypeConfiguration(
            auditLogTableName ?? AuditLogEntityTypeConfiguration.DefaultTableName,
            columnHints);
        modelBuilder.ApplyConfiguration(auditLog);

        var cursor = snapshotCursorTableName is null
            ? new SnapshotCursorEntityTypeConfiguration()
            : new SnapshotCursorEntityTypeConfiguration(snapshotCursorTableName);
        modelBuilder.ApplyConfiguration(cursor);

        return modelBuilder;
    }
}
