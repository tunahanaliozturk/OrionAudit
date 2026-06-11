namespace Moongazing.OrionAudit.Retention;

using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Configuration;

/// <summary>
/// <see cref="IAuditArchiver"/> used internally when
/// <see cref="RetentionSweepOptions.DryRun"/> is <see langword="true"/>. Counts rows that
/// would have been removed without touching the live table or the consumer's archive
/// destination. The hosted service wraps the configured archiver in this when dry-run is
/// on so all eligibility decisions still apply but the sweep produces no side effects.
/// </summary>
internal sealed class DryRunAuditArchiver : IAuditArchiver
{
    /// <inheritdoc />
    public Task<int> ArchiveAsync(
        DbContext dbContext,
        IReadOnlyList<AuditLog> rows,
        RetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        // Telemetry counter is incremented at the hosted service's call site so the
        // wrapper signals "would have removed" exactly once per sweep cycle. Returning
        // the row count here lets the caller report the would-have-deleted total back to
        // the operator.
        return Task.FromResult(rows.Count);
    }
}
