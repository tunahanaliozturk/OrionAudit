namespace Moongazing.OrionAudit.Store;

/// <summary>
/// The dimension an <see cref="AuditAggregationQuery"/> groups by. Each row of audit history is
/// bucketed by the chosen column and counted, so a caller can summarise activity ("how many changes
/// per action", "edits per day") without pulling every row across the store boundary.
/// </summary>
public enum AuditAggregateBy
{
    /// <summary>Group by <see cref="AuditLog.Action"/> (Inserted / Updated / Deleted / SoftDeleted).</summary>
    Action = 0,

    /// <summary>Group by <see cref="AuditLog.EntityType"/> (assembly-qualified audited type name).</summary>
    EntityType = 1,

    /// <summary>Group by <see cref="AuditLog.UserId"/> (the acting subject; null user folds into one "no user" bucket).</summary>
    UserId = 2,

    /// <summary>Group by <see cref="AuditLog.TenantId"/> (null tenant folds into one "no tenant" bucket).</summary>
    TenantId = 3,

    /// <summary>
    /// Group by a calendar bucket of <see cref="AuditLog.OccurredOnUtc"/> (UTC). The bucket width is
    /// chosen by <see cref="AuditAggregationQuery.TimeBucket"/>; the default
    /// <see cref="AuditTimeBucket.Day"/> gives a per-day activity histogram.
    /// </summary>
    TimeBucket = 4,
}

/// <summary>The calendar width of a <see cref="AuditAggregateBy.TimeBucket"/> grouping. UTC throughout.</summary>
public enum AuditTimeBucket
{
    /// <summary>One bucket per UTC calendar hour (truncated to the start of the hour).</summary>
    Hour = 0,

    /// <summary>One bucket per UTC calendar day (truncated to midnight). The default.</summary>
    Day = 1,

    /// <summary>One bucket per UTC calendar month (truncated to the first of the month).</summary>
    Month = 2,
}

/// <summary>
/// Storage-agnostic description of a read-side aggregation over audit history: the same filter
/// dimensions an <see cref="AuditHistoryQuery"/> carries, plus the grouping key (and, for a time
/// grouping, the bucket width). Passed to <see cref="IAuditHistoryStore.AggregateAsync"/>, which
/// returns one <see cref="AuditAggregateBucket"/> per distinct key with its row count. The aggregation
/// runs server-side (a <c>GROUP BY</c> on a relational backend) so the whole table is never
/// materialised. Added in v0.11.0.
/// </summary>
/// <remarks>
/// The filter surface is shared with <see cref="AuditHistoryQuery"/> by composition: the
/// <see cref="Filter"/> property carries the entity / subject / action / time / correlation /
/// changed-path predicates, and only its filter fields are honoured here (paging and ordering on the
/// embedded filter are ignored, since an aggregation returns grouped counts, not paged rows).
/// </remarks>
public sealed record AuditAggregationQuery
{
    /// <summary>
    /// The filter predicates to apply before grouping. Defaults to an unfiltered query (aggregate the
    /// whole history). Only the filter fields are used; <see cref="AuditHistoryQuery.Skip"/>,
    /// <see cref="AuditHistoryQuery.Take"/>, <see cref="AuditHistoryQuery.Order"/>, and
    /// <see cref="AuditHistoryQuery.SortBy"/> are ignored.
    /// </summary>
    public AuditHistoryQuery Filter { get; init; } = new();

    /// <summary>The dimension to group and count by. Defaults to <see cref="AuditAggregateBy.Action"/>.</summary>
    public AuditAggregateBy GroupBy { get; init; } = AuditAggregateBy.Action;

    /// <summary>
    /// When <see cref="GroupBy"/> is <see cref="AuditAggregateBy.TimeBucket"/>, the calendar width of
    /// each bucket. Ignored for every other grouping. Defaults to <see cref="AuditTimeBucket.Day"/>.
    /// </summary>
    public AuditTimeBucket TimeBucket { get; init; } = AuditTimeBucket.Day;

    /// <summary>Validates the embedded <see cref="Filter"/>'s invariants. Stores call this before executing.</summary>
    /// <exception cref="ArgumentException">The embedded filter fails <see cref="AuditHistoryQuery.Validate"/>.</exception>
    public void Validate() => Filter.Validate();
}

/// <summary>
/// One group of an audit-history aggregation: the distinct key value for the chosen
/// <see cref="AuditAggregateBy"/> dimension and the number of rows that fell into it.
/// </summary>
public sealed class AuditAggregateBucket
{
    /// <summary>Initializes a bucket from its key and count.</summary>
    /// <param name="key">
    /// The group key as a string. For <see cref="AuditAggregateBy.Action"/> it is the
    /// <see cref="AuditAction"/> name; for a time bucket it is the bucket-start instant formatted as a
    /// round-trip (<c>"O"</c>) UTC timestamp; for a null user / tenant it is <see langword="null"/>.
    /// </param>
    /// <param name="count">The number of audit rows in this group.</param>
    public AuditAggregateBucket(string? key, long count)
    {
        Key = key;
        Count = count;
    }

    /// <summary>The distinct group-key value (null for a "no user" / "no tenant" bucket).</summary>
    public string? Key { get; }

    /// <summary>The number of audit rows in this group.</summary>
    public long Count { get; }

    /// <summary>
    /// The bucket-start instant when this bucket came from a <see cref="AuditAggregateBy.TimeBucket"/>
    /// grouping; <see langword="null"/> for every non-time grouping. The same value <see cref="Key"/>
    /// carries in round-trip string form, surfaced typed for convenience.
    /// </summary>
    public DateTime? BucketStartUtc { get; init; }
}
