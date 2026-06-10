namespace Moongazing.OrionAudit.Retention;

using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Configuration;

/// <summary>
/// Strategy hook invoked by the retention sweep BEFORE rows are removed from the live
/// audit table. The default implementation, <see cref="DeleteAuditArchiver"/>, performs a
/// straight <c>ExecuteDelete</c>. Consumers that need to ship expiring rows to a separate
/// archive store (S3, cold table, Parquet) register a custom archiver that copies the
/// rows out and then deletes them inside one transaction.
/// </summary>
/// <remarks>
/// <para>
/// The archiver receives the candidate rows AFTER the policy selected them but BEFORE
/// the delete commit. Throwing from an archiver aborts the sweep cycle without deleting
/// any row, so the next cycle can retry on a clean slate. Implementations MUST be
/// idempotent: a sweep may re-present the same row id after a transient failure.
/// </para>
/// <para>
/// The contract intentionally takes <see cref="DbContext"/> (rather than a typed context)
/// so the archiver can run against any consumer DbContext that owns the audit table. The
/// <c>policy</c> argument is informational only; archivers typically tag the archived
/// rows with the policy that retired them.
/// </para>
/// </remarks>
public interface IAuditArchiver
{
    /// <summary>
    /// Archive (and remove) the supplied rows. Returns the number of rows that were
    /// removed from the live audit table. The sweep treats the returned count as the
    /// "deleted" total reported on telemetry.
    /// </summary>
    /// <param name="dbContext">The audit DbContext (shares the sweep's transaction scope).</param>
    /// <param name="rows">Rows selected by the retention policy for removal.</param>
    /// <param name="policy">The policy under which the rows were selected (informational).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> ArchiveAsync(
        DbContext dbContext,
        IReadOnlyList<AuditLog> rows,
        RetentionPolicy policy,
        CancellationToken cancellationToken);
}
