namespace Moongazing.OrionAudit.Retention;

using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Configuration;

/// <summary>
/// Default <see cref="IAuditArchiver"/> - removes the rows via <c>ExecuteDelete</c>
/// without copying them anywhere. Preserves the v0.7.7 retention sweep behaviour for
/// consumers that have not opted into a custom archiver.
/// </summary>
public sealed class DeleteAuditArchiver : IAuditArchiver
{
    /// <inheritdoc />
    public async Task<int> ArchiveAsync(
        DbContext dbContext,
        IReadOnlyList<AuditLog> rows,
        RetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(rows);
        _ = policy;
        if (rows.Count == 0)
        {
            return 0;
        }
        var ids = rows.Select(r => r.Id).ToList();
        return await dbContext.Set<AuditLog>()
            .Where(a => ids.Contains(a.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
