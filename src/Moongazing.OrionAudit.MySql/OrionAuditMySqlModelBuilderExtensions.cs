namespace Moongazing.OrionAudit.MySql;

using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;

/// <summary>
/// MySQL / MariaDB convenience overload of
/// <c>AuditModelBuilderExtensions.ApplyOrionAuditConfigurations(ModelBuilder, DbContext, ...)</c>.
/// Picks the appropriate column hint by default and forwards through.
/// </summary>
public static class OrionAuditMySqlModelBuilderExtensions
{
    /// <summary>
    /// Apply the OrionAudit configurations against a MySQL / MariaDB-backed DbContext.
    /// Defaults to the <see cref="OrionAuditColumnHints.MySqlJson"/> hint (native
    /// <c>JSON</c> column on MySQL 5.7+ and MariaDB 10.2+), which preserves shape validation
    /// and lets the consumer write <c>JSON_EXTRACT</c> queries against the diff column. Pass
    /// <paramref name="useLongText"/> = <see langword="true"/> for legacy MySQL builds without
    /// native JSON validation; the column maps to <c>LONGTEXT</c>.
    /// </summary>
    /// <param name="modelBuilder">The EF Core model builder.</param>
    /// <param name="dbContext">The owning <see cref="DbContext"/> (used to discover the configuration registered via <c>AddOrionAudit</c>).</param>
    /// <param name="useLongText">When true, use <see cref="OrionAuditColumnHints.MySqlLongText"/> instead of the default <see cref="OrionAuditColumnHints.MySqlJson"/>.</param>
    /// <param name="auditLogTableName">Optional override for the audit log table name.</param>
    /// <param name="captureQueueTableName">Optional override for the capture queue table name.</param>
    /// <param name="snapshotCursorTableName">Optional override for the snapshot cursor table name.</param>
    /// <returns>The same <paramref name="modelBuilder"/> for chaining.</returns>
    public static ModelBuilder ApplyOrionAuditMySqlConfigurations(
        this ModelBuilder modelBuilder,
        DbContext dbContext,
        bool useLongText = false,
        string? auditLogTableName = null,
        string? captureQueueTableName = null,
        string? snapshotCursorTableName = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(dbContext);

        var hint = useLongText
            ? OrionAuditColumnHints.MySqlLongText
            : OrionAuditColumnHints.MySqlJson;

        return modelBuilder.ApplyOrionAuditConfigurations(
            context: dbContext,
            auditLogTableName: auditLogTableName,
            snapshotCursorTableName: snapshotCursorTableName,
            columnHints: hint,
            captureQueueTableName: captureQueueTableName);
    }
}
