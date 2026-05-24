using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit.Configuration;

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
    /// <param name="customColumns">
    /// Custom columns to materialise as <see cref="AuditLog"/> shadow properties. When using
    /// <c>AddOrionAudit</c>, prefer the <see cref="ApplyOrionAuditConfigurations(ModelBuilder, DbContext, string?, string?, OrionAuditColumnHints, string?)"/>
    /// DbContext-aware overload which auto-picks-up registered columns from DI.
    /// </param>
    public static ModelBuilder ApplyOrionAuditConfigurations(
        this ModelBuilder modelBuilder,
        string? auditLogTableName = null,
        string? snapshotCursorTableName = null,
        OrionAuditColumnHints columnHints = OrionAuditColumnHints.Auto,
        string? captureQueueTableName = null,
        IReadOnlyList<CustomColumn>? customColumns = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var auditLog = new AuditLogEntityTypeConfiguration(
            auditLogTableName ?? AuditLogEntityTypeConfiguration.DefaultTableName,
            columnHints,
            customColumns ?? Array.Empty<CustomColumn>());
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

    /// <summary>
    /// DbContext-aware overload that picks up <see cref="CustomColumn"/>s registered via
    /// <c>AddOrionAudit</c>. Prefer this over the parameter-list overload when using
    /// <c>AddColumn</c> — it keeps <c>OnModelCreating</c> bodies short and avoids the trap of
    /// registering a column on <c>OrionAuditOptions</c> but forgetting to map it on the model.
    /// </summary>
    public static ModelBuilder ApplyOrionAuditConfigurations(
        this ModelBuilder modelBuilder,
        DbContext context,
        string? auditLogTableName = null,
        string? snapshotCursorTableName = null,
        OrionAuditColumnHints columnHints = OrionAuditColumnHints.Auto,
        string? captureQueueTableName = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        // EF's context.GetService<T>() only sees the EF internal service provider.
        // IAuditConfiguration is registered on the application service provider — reach it
        // through the CoreOptionsExtension's ApplicationServiceProvider, which is plumbed in
        // by the (sp, o) AddDbContext overload that wires UseOrionAudit.
        var appServices = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()?
            .ApplicationServiceProvider;
        var customs = appServices?.GetService<IAuditConfiguration>()?.CustomColumns
            ?? Array.Empty<CustomColumn>();

        return ApplyOrionAuditConfigurations(
            modelBuilder, auditLogTableName, snapshotCursorTableName, columnHints,
            captureQueueTableName, customs);
    }
}
