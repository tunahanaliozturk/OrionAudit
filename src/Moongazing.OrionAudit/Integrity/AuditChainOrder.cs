namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// The canonical ordering every chain-aware read uses, in one place so the verifier's walk and the
/// retention sweep's selections can never drift apart.
/// </summary>
/// <remarks>
/// <para>
/// A chain's real order is <em>insertion</em> order, recorded per stream in
/// <see cref="AuditLog.ChainSequence"/>. <see cref="AuditLog.OccurredOnUtc"/> alone is only a proxy
/// for it: one timestamp is computed per <c>SaveChanges</c> and stamped on every row of that save,
/// and column precision truncates further, so rows of one stream sharing a timestamp are routine
/// rather than exotic. Breaking those ties on <see cref="AuditLog.Id"/> - a random Guid - ordered
/// them in a way unrelated to the order they were chained in. Timestamp first, sequence as the
/// tie-break, is what both reads need: it is the chain's order, and it is also age order, which is
/// what a retention policy is actually about.
/// </para>
/// <para>
/// <b>Mixed versions.</b> Rows written before the sequence existed carry <see langword="null"/>, and
/// during a rolling deployment an instance still running the old build keeps producing them -
/// <em>after</em> a new instance has already appended a sequenced row to the same stream. Ordering
/// all nulls ahead of all sequenced rows would drag that newer row to the front of its stream and
/// report <c>BrokenLink</c> on an intact chain, permanently, so the timestamp leads and the sequence
/// only settles ties. A null row then sorts by when it was written, which is where the chain put it,
/// whichever build wrote it.
/// </para>
/// <para>
/// Within one tie - rows of a stream sharing a stored timestamp where one is sequenced and one is
/// not - the unsequenced row sorts last, because during an overlap the old build's write is the one
/// arriving late. That tie is the residual case this ordering cannot resolve: it needs two writers on
/// two builds to append to the <em>same</em> stream within one tick of the timestamp column
/// (100ns on SQL Server and SQLite, a microsecond on PostgreSQL and MySQL). A deliberately
/// coarse column - <c>datetime2(0)</c>, say - widens that window to a second and is worth avoiding on
/// a chained audit table.
/// </para>
/// <para>
/// The null group is selected with an explicit <c>0 / 1</c> key rather than relying on <c>ORDER BY</c>
/// null placement, which is provider-dependent (nulls sort first on SQL Server / SQLite, last on
/// PostgreSQL) - the one detail that would otherwise make the boundary behave differently per backend.
/// </para>
/// </remarks>
internal static class AuditChainOrder
{
    /// <summary>
    /// Oldest first: the chain's order, and the order retention's "delete what has aged out"
    /// selections need. The sequence tie-break is what keeps a bounded retention batch removing a
    /// contiguous head of each stream instead of an interior row - retention may only ever prune a
    /// chain's head, because re-anchoring at the oldest survivor cannot repair a hole in the middle.
    /// This is also the order <see cref="AuditChainVerifier.VerifyStream"/> requires.
    /// </summary>
    public static IOrderedQueryable<AuditLog> OldestFirst(this IQueryable<AuditLog> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query
            .OrderBy(a => a.OccurredOnUtc)
            .ThenBy(a => a.ChainSequence == null ? 1 : 0)
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
            .ThenByDescending(a => a.ChainSequence == null ? 1 : 0)
            .ThenByDescending(a => a.ChainSequence)
            .ThenByDescending(a => a.Id);
    }
}
