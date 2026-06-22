namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Pure, backend-agnostic engine that stamps <see cref="AuditLog.EntryHash"/> /
/// <see cref="AuditLog.PreviousHash"/> / <see cref="AuditLog.HashKeyId"/> onto a batch of
/// newly-captured rows before they are persisted. Stateless and reflection-free so it is shared by
/// the synchronous interceptor and the async dispatcher and stays Native-AOT clean. The database read
/// that supplies each stream's current chain head (the persisted anchor) and the resolution of custom
/// columns live in the caller (which owns the <c>DbContext</c> and the key); this engine only does the
/// deterministic in-memory MAC chaining.
/// </summary>
public static class AuditHashChainStamper
{
    /// <summary>
    /// Identifies a single chain within the configured <see cref="AuditHashChainScope"/>. For
    /// <see cref="AuditHashChainScope.PerEntityStream"/> this is (EntityType, EntityId, TenantId).
    /// </summary>
    /// <remarks>
    /// TenantId is part of the key so each tenant has its own stream: tenant-scoped verification
    /// filters rows to one tenant, so the first row of one tenant must not chain onto another tenant's
    /// head. A null tenant is normalized to the empty string (see <see cref="AuditTenant.Canonical"/>),
    /// which is just its own (single-tenant) stream.
    /// </remarks>
    public readonly record struct ChainKey(string EntityType, string EntityId, string TenantId);

    /// <summary>
    /// Computes the <see cref="ChainKey"/> for a row under the supplied scope. A null
    /// <see cref="AuditLog.TenantId"/> is normalized to <see cref="string.Empty"/> so it forms a
    /// single, stable stream.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scope"/> is not a defined
    /// <see cref="AuditHashChainScope"/> value. Falling back to a default would silently mis-key
    /// chains, so an unknown scope is a hard error.</exception>
    public static ChainKey KeyFor(AuditLog entry, AuditHashChainScope scope)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return scope switch
        {
            AuditHashChainScope.PerEntityStream
                => new ChainKey(entry.EntityType, entry.EntityId, AuditTenant.Canonical(entry.TenantId)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(scope), scope, "Unknown audit hash chain scope."),
        };
    }

    /// <summary>
    /// Stamps every row in <paramref name="newRows"/> in deterministic capture order, chaining each
    /// onto the prior row in its stream. The first new row of a stream chains onto that stream's
    /// existing persisted head supplied via <paramref name="existingHeadHashes"/> (or onto
    /// <see langword="null"/> when the stream has no prior hashed row, making it the genesis row).
    /// </summary>
    /// <param name="newRows">The rows about to be inserted. Mutated in place: their
    /// <see cref="AuditLog.PreviousHash"/>, <see cref="AuditLog.EntryHash"/>, and
    /// <see cref="AuditLog.HashKeyId"/> are assigned.</param>
    /// <param name="existingHeadHashes">Per-stream current head hash (the <see cref="AuditLog.EntryHash"/>
    /// of the latest already-persisted row in that stream). A stream absent from the map is treated
    /// as having no head (genesis).</param>
    /// <param name="scope">The chain scope determining how rows are grouped into streams.</param>
    /// <param name="keyId">The active key id to stamp on each row (for later key lookup / rotation).</param>
    /// <param name="key">The HMAC key material that MACs each row.</param>
    /// <param name="customColumnsFor">Resolves the (name, canonical-value) pairs of registered custom
    /// columns for a given row, so they are bound into the MAC. Return an empty list when none.</param>
    public static void Stamp(
        IReadOnlyList<AuditLog> newRows,
        IReadOnlyDictionary<ChainKey, string?> existingHeadHashes,
        AuditHashChainScope scope,
        int keyId,
        ReadOnlyMemory<byte> key,
        Func<AuditLog, IReadOnlyList<KeyValuePair<string, string?>>> customColumnsFor)
    {
        ArgumentNullException.ThrowIfNull(newRows);
        ArgumentNullException.ThrowIfNull(existingHeadHashes);
        ArgumentNullException.ThrowIfNull(customColumnsFor);

        if (newRows.Count == 0)
        {
            return;
        }

        // Per-stream running head, seeded from what is already persisted. As each new row is
        // stamped it becomes its stream's new head for the next row in the same batch.
        var runningHead = new Dictionary<ChainKey, string?>();

        // Stamp in the exact order a verifier later reads rows: (OccurredOnUtc, Id). Stamping out of
        // that order would chain rows in one sequence and verify them in another, breaking a chain
        // that is actually intact. OrderBy is a stable sort, but the explicit ThenBy(Id) removes any
        // reliance on input order for rows sharing a timestamp (the common single-save case).
        var ordered = newRows
            .OrderBy(r => r.OccurredOnUtc)
            .ThenBy(r => r.Id)
            .ToList();

        foreach (var row in ordered)
        {
            var chainKey = KeyFor(row, scope);
            if (!runningHead.TryGetValue(chainKey, out var previous))
            {
                previous = existingHeadHashes.TryGetValue(chainKey, out var head) ? head : null;
            }

            var customColumns = customColumnsFor(row);
            row.PreviousHash = previous;
            row.HashKeyId = keyId;
            row.EntryHash = AuditEntryHasher.ComputeEntryHash(row, previous, key.Span, customColumns);
            runningHead[chainKey] = row.EntryHash;
        }
    }

    /// <summary>
    /// Returns the distinct <see cref="ChainKey"/>s present in <paramref name="newRows"/> under the
    /// supplied scope, so the caller can fetch exactly those streams' current anchors from the store.
    /// </summary>
    public static IReadOnlyCollection<ChainKey> DistinctKeys(
        IReadOnlyList<AuditLog> newRows,
        AuditHashChainScope scope)
    {
        ArgumentNullException.ThrowIfNull(newRows);
        var set = new HashSet<ChainKey>();
        foreach (var row in newRows)
        {
            set.Add(KeyFor(row, scope));
        }
        return set;
    }

    /// <summary>
    /// The per-stream running tail after a batch is stamped: the final <see cref="AuditLog.EntryHash"/>
    /// for each stream and how many rows that batch added to it. The caller uses this to advance each
    /// stream's persisted anchor (latest hash + row count) in the same transaction.
    /// </summary>
    public static IReadOnlyDictionary<ChainKey, (string EntryHash, int Added)> SummarizeBatch(
        IReadOnlyList<AuditLog> newRows,
        AuditHashChainScope scope)
    {
        ArgumentNullException.ThrowIfNull(newRows);
        var ordered = newRows
            .OrderBy(r => r.OccurredOnUtc)
            .ThenBy(r => r.Id)
            .ToList();

        var result = new Dictionary<ChainKey, (string EntryHash, int Added)>();
        foreach (var row in ordered)
        {
            var chainKey = KeyFor(row, scope);
            var added = result.TryGetValue(chainKey, out var existing) ? existing.Added : 0;
            // EntryHash was assigned by Stamp; the last row of each stream is its new tail.
            result[chainKey] = (row.EntryHash!, added + 1);
        }
        return result;
    }
}
