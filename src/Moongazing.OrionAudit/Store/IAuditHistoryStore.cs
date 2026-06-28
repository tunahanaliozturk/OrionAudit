namespace Moongazing.OrionAudit.Store;

/// <summary>
/// Storage-agnostic read/maintenance surface over recorded <see cref="AuditLog"/> rows. Lets a
/// consumer query audit history by common dimensions (entity / subject, action, time range) with
/// paging and ordering, and compact a long change-history into a bounded snapshot — without
/// binding to a specific persistence backend.
/// </summary>
/// <remarks>
/// <para>
/// This abstraction exists alongside, not in place of, the EF-Core <c>DbContext</c> query
/// extensions (<see cref="AuditQueryExtensions"/>). Those extensions are the fast path for
/// consumers whose audit table lives in their own context; <see cref="IAuditHistoryStore"/> is
/// the path for code that must stay decoupled from where the audit rows live — an archive
/// service, a dedicated audit DB, or an in-memory test double.
/// </para>
/// <para>
/// Not every backend can support every operation (a write-only cold archive cannot page; an
/// append-only log cannot compact in place). Rather than forcing every implementer to stub
/// unsupported members, derive from <see cref="AuditHistoryStoreBase"/>, which supplies a default
/// that throws <see cref="NotSupportedException"/> for each operation, and override only what the
/// backend can honour. This mirrors the family's existing capability-default pattern (for
/// example <c>DeleteAuditArchiver</c> as the default <c>IAuditArchiver</c>).
/// </para>
/// </remarks>
public interface IAuditHistoryStore
{
    /// <summary>
    /// Returns one page of audit-history rows matching <paramref name="query"/>'s filters,
    /// ordered and paged per the query. Implementations call <see cref="AuditHistoryQuery.Validate"/>
    /// first, so a malformed query surfaces an <see cref="ArgumentException"/> consistently across
    /// backends.
    /// </summary>
    /// <param name="query">The filter / paging / ordering specification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A page of matching rows plus paging metadata.</returns>
    /// <exception cref="NotSupportedException">The backend cannot satisfy queries.</exception>
    /// <exception cref="ArgumentException">The query fails <see cref="AuditHistoryQuery.Validate"/>.</exception>
    Task<AuditHistoryPage> QueryAsync(AuditHistoryQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregates audit history into grouped counts: applies <paramref name="query"/>'s filters, then
    /// groups the matching rows by <see cref="AuditAggregationQuery.GroupBy"/> and returns one
    /// <see cref="AuditAggregateBucket"/> (key + count) per distinct group. The returned result is
    /// always bounded by the number of distinct buckets, not the table size. Where the grouping
    /// executes, and whether the full row set is materialised, is backend-dependent: a relational store
    /// pushes the grouping down to a server-side <c>GROUP BY</c> (the bundled <c>EfCoreAuditHistoryStore</c>
    /// does), whereas an in-memory or non-relational store may enumerate the matching rows in process.
    /// Implementations call <see cref="AuditAggregationQuery.Validate"/> first.
    /// </summary>
    /// <param name="query">The filter plus grouping specification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One bucket per distinct group key, each with its row count. Empty when nothing matches.</returns>
    /// <exception cref="NotSupportedException">The backend cannot aggregate.</exception>
    /// <exception cref="ArgumentException">The query fails <see cref="AuditAggregationQuery.Validate"/>.</exception>
    Task<IReadOnlyList<AuditAggregateBucket>> AggregateAsync(AuditAggregationQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compacts the audit history of a single entity: folds rows older than the requested retained
    /// tail into one compacted snapshot row that carries the entity's reconstructed state at the
    /// compaction boundary, removes the folded rows, and leaves the snapshot plus the bounded tail.
    /// The latest state stays fully reconstructable. A no-op when the history is too short to gain
    /// anything.
    /// </summary>
    /// <param name="request">Which entity to compact and how large a tail to retain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of how many rows were folded and the resulting shape.</returns>
    /// <exception cref="NotSupportedException">The backend cannot compact in place.</exception>
    /// <exception cref="ArgumentException">The request fails <see cref="AuditCompactionRequest.Validate"/>.</exception>
    Task<AuditCompactionResult> CompactAsync(AuditCompactionRequest request, CancellationToken cancellationToken = default);
}
