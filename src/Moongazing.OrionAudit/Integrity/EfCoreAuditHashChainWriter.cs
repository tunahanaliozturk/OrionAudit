using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Fetches each touched stream's current chain head from a <see cref="DbContext"/> and stamps the
/// hash chain onto a batch of new <see cref="AuditLog"/> rows via <see cref="AuditHashChainStamper"/>.
/// Called from inside the capture transaction (synchronous interceptor) and the dispatcher
/// transaction (async capture), before <c>SaveChanges</c>, so the stamped rows persist atomically
/// with the change that produced them.
/// </summary>
internal static class EfCoreAuditHashChainWriter
{
    /// <summary>
    /// Stamps <paramref name="newRows"/> in place. Reads the per-stream head hashes already
    /// persisted in <paramref name="context"/> (rows are not yet flushed, so the running batch does
    /// not see itself), then chains the new rows on top.
    /// </summary>
    public static async Task StampAsync(
        DbContext context,
        IReadOnlyList<AuditLog> newRows,
        AuditHashChainScope scope,
        CancellationToken cancellationToken)
    {
        if (newRows.Count == 0)
        {
            return;
        }

        var keys = AuditHashChainStamper.DistinctKeys(newRows, scope);
        var heads = await LoadHeadHashesAsync(context, keys, cancellationToken).ConfigureAwait(false);
        AuditHashChainStamper.Stamp(newRows, heads, scope);
    }

    // For each (EntityType, EntityId) stream touched by the batch, find the EntryHash of the latest
    // already-persisted hashed row, ordered (OccurredOnUtc, Id) to match capture/verify order. Only
    // rows that already carry a hash participate; an unhashed historical prefix is ignored so the
    // first new row of a never-before-hashed stream becomes its genesis (PreviousHash = null).
    private static async Task<IReadOnlyDictionary<AuditHashChainStamper.ChainKey, string?>> LoadHeadHashesAsync(
        DbContext context,
        IReadOnlyCollection<AuditHashChainStamper.ChainKey> keys,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<AuditHashChainStamper.ChainKey, string?>();

        // One small query per touched stream. A single save almost always touches a handful of
        // entities, so this is a few indexed point-lookups (IX_OrionAudit_EntityLookup covers the
        // EntityType+EntityId+OccurredOnUtc ordering). Keeping it per-stream avoids a GROUP BY whose
        // translation differs across providers and keeps each query trivially index-served.
        foreach (var key in keys)
        {
            var headHash = await context.Set<AuditLog>()
                .AsNoTracking()
                .Where(a => a.EntityType == key.EntityType
                    && a.EntityId == key.EntityId
                    && a.EntryHash != null)
                .OrderByDescending(a => a.OccurredOnUtc)
                .ThenByDescending(a => a.Id)
                .Select(a => a.EntryHash)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            result[key] = headHash;
        }

        return result;
    }
}
