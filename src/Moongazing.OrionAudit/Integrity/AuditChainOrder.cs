namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// The canonical ordering every chain-aware read uses, in one place so the verifier's walk and the
/// retention sweep's selections can never drift apart.
/// </summary>
/// <remarks>
/// <para>
/// A chain's real order is <em>insertion</em> order, recorded per stream in
/// <see cref="AuditLog.ChainSequence"/>. <see cref="AuditLog.OccurredOnUtc"/> is not it and cannot be
/// made into it. It is stamped near the start of capture while the chain's order is settled much
/// later, by which writer wins the anchor lock, so two concurrent same-stream writers can invert -
/// the one holding the earlier timestamp landing second in the chain. It is also computed once per
/// <c>SaveChanges</c> and stamped on every row of that save, and column precision truncates it
/// further, so ties within one stream are routine rather than exotic; breaking those on
/// <see cref="AuditLog.Id"/>, a random Guid, ordered rows in a way unrelated to how they were chained.
/// </para>
/// <para>
/// <b>Two orders, two jobs.</b> <see cref="ForWalk"/> is the chain's order and is computed in memory,
/// because the only exact signal - <see cref="AuditLog.ChainSequence"/> - cannot express the legacy
/// boundary on its own and SQL cannot express the combination. <see cref="OldestFirst"/> /
/// <see cref="NewestFirst"/> are age orders, which is what retention's selections are about, and stay
/// in SQL where the batching needs them.
/// </para>
/// <para>
/// <b>Mixed versions.</b> Rows written before the sequence existed carry <see langword="null"/>, and
/// during a rolling deployment an instance still running the old build keeps producing them -
/// <em>after</em> a new instance has already appended a sequenced row to the same stream. Ordering
/// all nulls ahead of all sequenced rows would drag that newer row to the front of its stream and
/// report <c>BrokenLink</c> on an intact chain, permanently. <see cref="ForWalk"/> interleaves the two
/// by timestamp instead, which is the only comparison rows from two different builds share, while
/// keeping each build's rows in their own exact order. Where one tie remains - a sequenced and an
/// unsequenced row of one stream sharing a stored timestamp - the unsequenced one goes last, because
/// during an overlap the old build's write is the late one. Hitting that needs two builds appending
/// to the <em>same</em> stream within one tick of the timestamp column (100ns on SQL Server and
/// SQLite, a microsecond on PostgreSQL and MySQL); a deliberately coarse column - <c>datetime2(0)</c>,
/// say - widens it to a second and is worth avoiding on a chained audit table.
/// </para>
/// <para>
/// In the SQL orders the null group is selected with an explicit <c>0 / 1</c> key rather than relying
/// on <c>ORDER BY</c> null placement, which is provider-dependent (nulls sort first on SQL Server /
/// SQLite, last on PostgreSQL) - the one detail that would otherwise make them behave differently per
/// backend.
/// </para>
/// </remarks>
internal static class AuditChainOrder
{
    /// <summary>
    /// One stream's rows in the order they were chained, which is the order
    /// <see cref="AuditChainVerifier.VerifyStream"/> walks them in. Sequenced rows are ordered by
    /// <see cref="AuditLog.ChainSequence"/>; unsequenced ones keep the order they arrived in (the
    /// database's <see cref="OldestFirst"/>); the timestamp only decides how the two interleave.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This cannot be done in SQL, which is why it is done here. <see cref="AuditLog.OccurredOnUtc"/>
    /// is stamped near the start of capture, but the chain's order is decided much later, by which
    /// writer wins the anchor lock - so for two concurrent same-stream writers the two orders can
    /// <em>invert</em>: the writer that read the earlier clock value loses the race and lands second
    /// in the chain. Ordering by timestamp then reads that stream backwards and reports
    /// <c>BrokenLink</c> on a chain nobody touched. <see cref="AuditLog.ChainSequence"/> is assigned
    /// under the anchor lock and is therefore the chain's order by construction, so among rows that
    /// have one it is used alone and no timestamp comparison can distort it.
    /// </para>
    /// <para>
    /// A stream can also hold rows written before the sequence existed, including ones written
    /// <em>during</em> a rolling deployment by an instance still on the old build, which land after
    /// sequenced rows. Those carry no sequence and the only signal shared with a sequenced row is the
    /// timestamp, so the merge below uses it for exactly that comparison and nothing else: each
    /// side's internal order is preserved whatever the timestamps say. A stream with no unsequenced
    /// rows is therefore ordered purely by sequence, and a stream with no sequenced rows comes back
    /// exactly as the database returned it - so a chain written before any of this verifies bit for
    /// bit as it always did, including the database's own tie-breaking on
    /// <see cref="AuditLog.Id"/>, which differs from .NET's for <c>uniqueidentifier</c>.
    /// </para>
    /// </remarks>
    /// <param name="rows">The stream's rows as loaded, ideally already in <see cref="OldestFirst"/>
    /// order so that the unsequenced ones carry the database's own ordering.</param>
    public static List<AuditLog> ForWalk(IReadOnlyList<AuditLog> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var sequenced = new List<AuditLog>(rows.Count);
        var unsequenced = new List<AuditLog>();
        foreach (var row in rows)
        {
            (row.ChainSequence is null ? unsequenced : sequenced).Add(row);
        }

        if (sequenced.Count == 0)
        {
            return unsequenced;     // a pre-upgrade chain: exactly the order it was handed in
        }

        // Unique per stream (the writer assigns them from the anchor's row count under its lock), so
        // there are no ties to break and nothing here depends on the incoming order.
        sequenced.Sort(static (left, right) => left.ChainSequence!.Value.CompareTo(right.ChainSequence!.Value));

        if (unsequenced.Count == 0)
        {
            return sequenced;       // the ordinary case once the column exists
        }

        // Merge. Each side keeps its own order; the timestamp only chooses which side to take from,
        // because that is the only comparison two different builds' rows have in common. On a tie the
        // sequenced row goes first: during an overlap the old build's write is the one arriving late.
        var merged = new List<AuditLog>(rows.Count);
        var s = 0;
        var u = 0;
        while (s < sequenced.Count && u < unsequenced.Count)
        {
            merged.Add(sequenced[s].OccurredOnUtc <= unsequenced[u].OccurredOnUtc
                ? sequenced[s++]
                : unsequenced[u++]);
        }
        while (s < sequenced.Count)
        {
            merged.Add(sequenced[s++]);
        }
        while (u < unsequenced.Count)
        {
            merged.Add(unsequenced[u++]);
        }
        return merged;
    }

    /// <summary>
    /// Oldest first, for retention's "delete what has aged out" selections and as the load order
    /// <see cref="ForWalk"/> refines. Age leads because that is what a retention policy is about, and
    /// the sequence tie-break keeps rows of one stream that share a timestamp in the order they were
    /// chained. It is deliberately <em>not</em> what guarantees a sweep prunes a contiguous head -
    /// age and chain order can disagree outright when two concurrent writers invert, which no
    /// ordering can reconcile. <see cref="Retention.ChainPruneArchiver"/> narrows the batch instead.
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
