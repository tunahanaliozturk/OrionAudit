using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Stamps the keyed-MAC chain onto a batch of new <see cref="AuditLog"/> rows and advances each
/// touched stream's persisted <see cref="AuditChainAnchor"/>, all inside the caller's
/// <c>SaveChanges</c> transaction (the synchronous interceptor or the async dispatcher), so the
/// stamped rows and their anchors commit atomically with the change that produced them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency.</b> The stream's <see cref="AuditChainAnchor"/> is the single point two concurrent
/// appenders contend on. Before reading a stream's head this writer takes a pessimistic row lock on
/// that stream's anchor (provider-appropriate) <em>within the consumer's transaction</em>, stamps
/// <see cref="AuditLog.PreviousHash"/> from the anchor, then updates the anchor's latest hash + count
/// in the same transaction. Same-stream appends therefore serialize on the anchor row, so two
/// transactions cannot stamp the same predecessor hash and corrupt the chain; different streams lock
/// different anchor rows and stay parallel.
/// </para>
/// <para>
/// <b>Provider behaviour.</b> On SQL Server the lock is <c>WITH (UPDLOCK, HOLDLOCK)</c>; on
/// PostgreSQL / MySQL it is <c>SELECT ... FOR UPDATE</c>. On SQLite no explicit row lock is issued
/// because SQLite already serializes write transactions database-wide, which provides the same
/// guarantee. Providers without a recognised lock dialect (e.g. the EF in-memory provider used in some
/// tests) skip the explicit lock and rely on the surrounding transaction; for a brand-new stream whose
/// anchor row does not exist yet, the anchor table's primary key prevents two genesis anchors for the
/// same stream regardless of provider.
/// </para>
/// </remarks>
internal static class EfCoreAuditHashChainWriter
{
    /// <summary>
    /// Stamps <paramref name="newRows"/> in place and advances their stream anchors. Resolves the
    /// active key from <paramref name="keyProvider"/>, locks + reads each touched stream's anchor, and
    /// MACs each row (binding the registered custom-column values resolved from the tracked entity).
    /// </summary>
    /// <exception cref="OrionAuditChainKeyException">The provider's active key id is not registered.</exception>
    public static async Task StampAsync(
        DbContext context,
        IReadOnlyList<AuditLog> newRows,
        AuditHashChainScope scope,
        IAuditChainKeyProvider keyProvider,
        IReadOnlyList<Configuration.CustomColumn> customColumns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(newRows);
        ArgumentNullException.ThrowIfNull(keyProvider);
        ArgumentNullException.ThrowIfNull(customColumns);

        if (newRows.Count == 0)
        {
            return;
        }

        var keyId = keyProvider.ActiveKeyId;
        var key = keyProvider.TryGetKey(keyId)
            ?? throw new OrionAuditChainKeyException(
                $"Audit hash chain active key id {keyId} is not registered with the key provider.");

        var keys = AuditHashChainStamper.DistinctKeys(newRows, scope);

        // Lock + load each touched stream's anchor first, so the head we stamp from is the value no
        // concurrent same-stream transaction can change until we commit.
        var anchors = await LockAndLoadAnchorsAsync(context, keys, cancellationToken).ConfigureAwait(false);

        var heads = new Dictionary<AuditHashChainStamper.ChainKey, string?>(keys.Count);
        foreach (var chainKey in keys)
        {
            heads[chainKey] = anchors.TryGetValue(chainKey, out var anchor) ? anchor.LatestEntryHash : null;
        }

        // Resolve each row's registered custom-column values from the tracked entity's shadow
        // properties, canonicalized to the same invariant string form verification will read back.
        IReadOnlyList<KeyValuePair<string, string?>> CustomColumnsFor(AuditLog row)
            => ReadCustomColumns(context, row, customColumns);

        AuditHashChainStamper.Stamp(newRows, heads, scope, keyId, key, CustomColumnsFor);

        // Advance each stream's anchor to the batch's new tail (latest hash + added count + key id).
        var summaries = AuditHashChainStamper.SummarizeBatch(newRows, scope);
        foreach (var (chainKey, tail) in summaries)
        {
            if (anchors.TryGetValue(chainKey, out var existing))
            {
                existing.LatestEntryHash = tail.EntryHash;
                existing.RowCount += tail.Added;
                existing.KeyId = keyId;
            }
            else
            {
                context.Set<AuditChainAnchor>().Add(new AuditChainAnchor
                {
                    EntityType = chainKey.EntityType,
                    EntityId = chainKey.EntityId,
                    TenantId = chainKey.TenantId,
                    LatestEntryHash = tail.EntryHash,
                    RowCount = tail.Added,
                    KeyId = keyId,
                });
            }
        }
    }

    private static IReadOnlyList<KeyValuePair<string, string?>> ReadCustomColumns(
        DbContext context,
        AuditLog row,
        IReadOnlyList<Configuration.CustomColumn> customColumns)
    {
        if (customColumns.Count == 0)
        {
            return Array.Empty<KeyValuePair<string, string?>>();
        }
        var entry = context.Entry(row);
        var result = new List<KeyValuePair<string, string?>>(customColumns.Count);
        foreach (var column in customColumns)
        {
            var value = entry.Property(column.Name).CurrentValue;
            result.Add(new KeyValuePair<string, string?>(
                column.Name, CustomColumnCanonicalizer.ToCanonicalString(value)));
        }
        return result;
    }

    // Loads the anchors for the touched streams, taking a pessimistic row lock on each existing anchor
    // (provider-appropriate) so a concurrent same-stream append blocks until this transaction commits.
    private static async Task<Dictionary<AuditHashChainStamper.ChainKey, AuditChainAnchor>> LockAndLoadAnchorsAsync(
        DbContext context,
        IReadOnlyCollection<AuditHashChainStamper.ChainKey> keys,
        CancellationToken cancellationToken)
    {
        var lockHint = AnchorLockDialect.For(context);
        var result = new Dictionary<AuditHashChainStamper.ChainKey, AuditChainAnchor>();

        foreach (var key in keys)
        {
            if (lockHint is not null)
            {
                // Acquire the pessimistic lock on the anchor PK before the tracked read. Run on the
                // same connection/transaction EF uses for SaveChanges so the lock is held until commit.
                await context.Database
                    .ExecuteSqlRawAsync(lockHint.Sql, lockHint.Parameters(key), cancellationToken)
                    .ConfigureAwait(false);
            }

            var anchor = await context.Set<AuditChainAnchor>()
                .FirstOrDefaultAsync(
                    a => a.EntityType == key.EntityType
                        && a.EntityId == key.EntityId
                        && a.TenantId == key.TenantId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (anchor is not null)
            {
                result[key] = anchor;
            }
        }

        return result;
    }
}
