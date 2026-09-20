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
internal static class AuditHashChainStamper
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
    /// <param name="existingRowCounts">Per-stream count of rows already in the chain (the stream's
    /// <see cref="AuditChainAnchor.RowCount"/>), used to continue <see cref="AuditLog.ChainSequence"/>
    /// where the persisted stream left off. When <see langword="null"/> no sequence is assigned and
    /// the rows stay on the legacy <c>(OccurredOnUtc, Id)</c> walk order, so a caller that cannot
    /// supply the stream's length is not silently given colliding sequence numbers.</param>
    public static void Stamp(
        IReadOnlyList<AuditLog> newRows,
        IReadOnlyDictionary<ChainKey, string?> existingHeadHashes,
        AuditHashChainScope scope,
        int keyId,
        ReadOnlyMemory<byte> key,
        Func<AuditLog, IReadOnlyList<KeyValuePair<string, string?>>> customColumnsFor,
        IReadOnlyDictionary<ChainKey, long>? existingRowCounts = null)
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
        // Per-stream running sequence, seeded from the stream's persisted length. Assigning it here
        // (rather than in the caller) is what makes stamping order and walk order the same order:
        // whatever tie-break this loop applies within the batch is recorded on the rows themselves.
        var runningSequence = existingRowCounts is null ? null : new Dictionary<ChainKey, long>();

        // Deterministic in-batch order. (OccurredOnUtc, Id) is arbitrary for rows of one stream that
        // share a timestamp - which every multi-row save produces - but it does not need to match
        // anything external any more: the order chosen here IS the chain order, because it is written
        // to ChainSequence and read back by the verifier. OrderBy is a stable sort, but the explicit
        // ThenBy(Id) removes any reliance on input order.
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

            if (runningSequence is not null)
            {
                if (!runningSequence.TryGetValue(chainKey, out var sequence))
                {
                    sequence = existingRowCounts!.TryGetValue(chainKey, out var start) ? start : 0;
                }
                row.ChainSequence = sequence;
                runningSequence[chainKey] = sequence + 1;
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
    /// The result is sorted, and every caller must take the streams' anchor locks in that order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why sorted.</b> A batch spanning several streams holds each anchor lock it has taken while
    /// it goes after the next one. Two concurrent batches that both touch streams A and B, and
    /// approach them in opposite orders, then hold A and B respectively and each wait on the other -
    /// a deadlock the database can only resolve by killing one of them. Input order alone decided
    /// that order before: the keys came out of a <see cref="HashSet{T}"/> whose enumeration follows
    /// insertion, so it was the order the rows happened to be captured in, which two callers have no
    /// reason to share. A single global order across all callers removes the cycle by construction.
    /// EF's own command ordering cannot help here, because these locks are raw statements that run
    /// before any of the batch's commands.
    /// </para>
    /// <para>
    /// The comparison is ordinal over every component of the key, so it is total - two distinct keys
    /// differ somewhere - and it is culture-independent, which a lock order has to be: two processes
    /// under different locales must agree.
    /// </para>
    /// </remarks>
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
        var ordered = set.ToList();
        ordered.Sort(CompareKeys);
        return ordered;
    }

    // Ordinal over (EntityType, EntityId, TenantId) - the whole key, so distinct keys never compare
    // equal and the order is total rather than "usually different".
    private static int CompareKeys(ChainKey left, ChainKey right)
    {
        var byType = string.CompareOrdinal(left.EntityType, right.EntityType);
        if (byType != 0)
        {
            return byType;
        }
        var byId = string.CompareOrdinal(left.EntityId, right.EntityId);
        return byId != 0 ? byId : string.CompareOrdinal(left.TenantId, right.TenantId);
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
