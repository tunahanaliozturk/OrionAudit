namespace Moongazing.OrionAudit.Store;

/// <summary>
/// One page of audit-history rows returned by <see cref="IAuditHistoryStore.QueryAsync"/>,
/// together with the paging metadata a caller needs to request the next page.
/// </summary>
/// <remarks>
/// <see cref="TotalCount"/> is the number of rows that satisfy the query's filters BEFORE
/// paging, so a UI can render "showing 1-100 of 4 213" and compute the page count. Stores that
/// cannot cheaply count the full match set may set it equal to <see cref="Skip"/> plus the
/// returned <see cref="Items"/> count when <see cref="HasMore"/> is false; the in-memory and
/// EF Core stores in this package always report the exact total.
/// </remarks>
public sealed class AuditHistoryPage
{
    /// <summary>Initializes a page from its rows and paging metadata.</summary>
    /// <param name="items">The rows on this page, already ordered per the query.</param>
    /// <param name="totalCount">Total rows matching the filters before paging.</param>
    /// <param name="skip">The <see cref="AuditHistoryQuery.Skip"/> this page was taken at.</param>
    /// <param name="take">The effective page size this page was taken with.</param>
    public AuditHistoryPage(IReadOnlyList<AuditLog> items, int totalCount, int skip, int take)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        TotalCount = totalCount;
        Skip = skip;
        Take = take;
    }

    /// <summary>The rows on this page, ordered per <see cref="AuditHistoryQuery.Order"/>.</summary>
    public IReadOnlyList<AuditLog> Items { get; }

    /// <summary>Total number of rows matching the query's filters, ignoring paging.</summary>
    public int TotalCount { get; }

    /// <summary>The offset this page started at (echoes <see cref="AuditHistoryQuery.Skip"/>).</summary>
    public int Skip { get; }

    /// <summary>The page size this page was taken with (echoes <see cref="AuditHistoryQuery.EffectiveTake"/>).</summary>
    public int Take { get; }

    /// <summary>True when rows beyond this page remain (<see cref="Skip"/> + returned count &lt; <see cref="TotalCount"/>).</summary>
    public bool HasMore => Skip + Items.Count < TotalCount;

    /// <summary>An empty page for a given paging position. Useful for short-circuiting on a no-match query.</summary>
    public static AuditHistoryPage Empty(int skip, int take)
        => new(Array.Empty<AuditLog>(), totalCount: 0, skip, take);
}
