using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionAudit;

/// <summary>EF Core <see cref="ModelBuilder"/> extensions for OrionAudit.</summary>
public static class AuditModelBuilderExtensions
{
    /// <summary>
    /// Applies the OrionAudit entity-type configurations to the model. Call from
    /// <c>DbContext.OnModelCreating</c>. Always maps <see cref="AuditLog"/>, the
    /// <see cref="SnapshotCursor"/> companion table, and the
    /// <see cref="AuditCaptureQueueEntry"/> companion table (the latter two are harmless when
    /// their feature is not configured — they simply stay empty).
    /// </summary>
    /// <param name="modelBuilder">The EF Core model builder.</param>
    /// <param name="auditLogTableName">Override the default <c>OrionAudit_Log</c> table name.</param>
    /// <param name="snapshotCursorTableName">Override the default <c>OrionAudit_Snapshot_Cursors</c> table name.</param>
    /// <param name="columnHints">Provider-specific column-type hints for <c>Diff</c> and <c>Snapshot</c> (default: <see cref="OrionAuditColumnHints.Auto"/>).</param>
    /// <param name="captureQueueTableName">Override the default <c>OrionAudit_Capture_Queue</c> table name.</param>
    public static ModelBuilder ApplyOrionAuditConfigurations(
        this ModelBuilder modelBuilder,
        string? auditLogTableName = null,
        string? snapshotCursorTableName = null,
        OrionAuditColumnHints columnHints = OrionAuditColumnHints.Auto,
        string? captureQueueTableName = null)
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

        var queue = captureQueueTableName is null
            ? new AuditCaptureQueueEntityTypeConfiguration()
            : new AuditCaptureQueueEntityTypeConfiguration(captureQueueTableName);
        modelBuilder.ApplyConfiguration(queue);

        return modelBuilder;
    }
}
