namespace Moongazing.OrionAudit.Retention;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moongazing.OrionAudit.Configuration;

/// <summary>
/// <see cref="IAuditArchiver"/> implementation that COPIES expiring rows into an archive
/// table represented by <typeparamref name="TArchiveRow"/> and THEN deletes them from the
/// live audit table - both inside one transaction so a partial failure cannot lose data.
/// Pairs with the OrionGuard <c>CopyToTableOutboxArchiver&lt;TArchiveRow&gt;</c> pattern.
/// </summary>
/// <typeparam name="TArchiveRow">
/// Consumer-owned archive entity. The caller supplies a <c>map</c> delegate that builds
/// one <typeparamref name="TArchiveRow"/> per source <see cref="AuditLog"/> row; the
/// archive table can carry whatever columns the cold store needs (e.g. a JSON-encoded
/// snapshot blob, an originating shard id).
/// </typeparam>
public sealed class CopyToTableAuditArchiver<TArchiveRow> : IAuditArchiver
    where TArchiveRow : class
{
    private readonly Func<AuditLog, TArchiveRow> map;

    /// <summary>
    /// Construct with a mapping function that translates a live <see cref="AuditLog"/>
    /// row into the archive-table entity.
    /// </summary>
    public CopyToTableAuditArchiver(Func<AuditLog, TArchiveRow> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        this.map = map;
    }

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

        // Wrap copy + delete in a single transaction. The IDbContextTransaction is owned
        // here so the archiver controls atomicity; the outer sweep does not start a
        // transaction of its own.
        IDbContextTransaction? tx = null;
        try
        {
            tx = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // In-memory / no-transaction providers (xUnit fixtures, InMemory) raise this.
            // Fall through with tx = null and rely on the provider's natural ordering.
        }

        try
        {
            var archiveRows = rows.Select(map).ToList();
            dbContext.Set<TArchiveRow>().AddRange(archiveRows);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var ids = rows.Select(r => r.Id).ToList();
            var deleted = await dbContext.Set<AuditLog>()
                .Where(a => ids.Contains(a.Id))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            if (tx is not null)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return deleted;
        }
        catch
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (tx is not null)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
