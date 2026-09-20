namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// The canonical orderings every chain-aware read uses, in one place so the verifier's walk and the
/// retention sweep's selections can never drift apart.
/// </summary>
/// <remarks>
/// <para>
/// A chain's real order is <em>insertion</em> order, recorded per stream in
/// <see cref="AuditLog.ChainSequence"/>. <see cref="AuditLog.OccurredOnUtc"/> is only a proxy for it:
/// one timestamp is computed per <c>SaveChanges</c> and stamped on every row of that save, and column
/// precision truncates further, so rows of one stream sharing a timestamp are routine rather than
/// exotic. Breaking those ties on <see cref="AuditLog.Id"/> - a random Guid - ordered them in a way
/// unrelated to the order they were chained in.
/// </para>
/// <para>
/// <b>The legacy boundary.</b> Rows written before the sequence existed carry <see langword="null"/>.
/// They are ordered ahead of every sequenced row of the same stream and among themselves by the
/// original <c>(OccurredOnUtc, Id)</c>, so a chain written before this change walks in exactly the
/// order it always did, and the sequenced rows appended after the upgrade continue it. The null group
/// is selected with an explicit <c>0 / 1</c> key rather than relying on <c>ORDER BY</c> null
/// placement, which is provider-dependent (nulls sort first on SQL Server / SQLite, last on
/// PostgreSQL) - the one detail that would have made the boundary behave differently per backend.
/// </para>
/// </remarks>
internal static class AuditChainOrder
{
    /// <summary>
    /// One stream's rows in chain order: sequenced rows by their sequence, after the legacy prefix.
    /// This is the order <see cref="AuditChainVerifier.VerifyStream"/> requires.
    /// </summary>
    public static IOrderedQueryable<AuditLog> InChainOrder(this IQueryable<AuditLog> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query
            .OrderBy(a => a.ChainSequence == null ? 0 : 1)
            .ThenBy(a => a.ChainSequence)
            .ThenBy(a => a.OccurredOnUtc)
            .ThenBy(a => a.Id);
    }

    /// <summary>
    /// Oldest first, for retention's "delete what has aged out" selections. Age leads because that is
    /// what the policy is about; the sequence is the tie-break, so a bounded batch removes a
    /// contiguous head of each stream instead of an interior row. Retention may only ever prune a
    /// chain's head - re-anchoring at the oldest survivor cannot repair a hole in the middle.
    /// </summary>
    public static IOrderedQueryable<AuditLog> OldestFirst(this IQueryable<AuditLog> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query
            .OrderBy(a => a.OccurredOnUtc)
            .ThenBy(a => a.ChainSequence == null ? 0 : 1)
            .ThenBy(a => a.ChainSequence)
            .ThenBy(a => a.Id);
    }

    /// <summary>
    /// The exact reverse of <see cref="OldestFirst"/>, for "keep the latest N" selections that walk
    /// the same order backwards and skip the survivors.
    /// </summary>
    public static IOrderedQueryable<AuditLog> NewestFirst(this IQueryable<AuditLog> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query
            .OrderByDescending(a => a.OccurredOnUtc)
            .ThenByDescending(a => a.ChainSequence == null ? 0 : 1)
            .ThenByDescending(a => a.ChainSequence)
            .ThenByDescending(a => a.Id);
    }
}
