namespace Moongazing.OrionAudit.Store;

/// <summary>Ordering applied to an <see cref="AuditHistoryQuery"/> result by <see cref="AuditLog.OccurredOnUtc"/>.</summary>
public enum AuditHistoryOrder
{
    /// <summary>Newest change first (descending <see cref="AuditLog.OccurredOnUtc"/>). The default.</summary>
    NewestFirst = 0,

    /// <summary>Oldest change first (ascending <see cref="AuditLog.OccurredOnUtc"/>).</summary>
    OldestFirst = 1,
}

/// <summary>
/// Storage-agnostic description of an audit-history read: filter dimensions plus paging and
/// ordering. Passed to <see cref="IAuditHistoryStore.QueryAsync"/>. Every filter is optional;
/// a default-constructed query (no filters) returns the whole history, newest first, capped
/// by <see cref="Take"/>.
/// </summary>
/// <remarks>
/// <para>
/// The query is deliberately a plain data record rather than an <c>IQueryable</c> predicate so
/// it can travel across a store boundary that is not backed by a LINQ provider (a REST-fronted
/// archive, a document store, an in-memory test double). Stores that DO have a LINQ provider
/// translate the populated filters into a server-side <c>WHERE</c>; stores that cannot simply
/// throw <see cref="NotSupportedException"/> from <see cref="AuditHistoryStoreBase"/>.
/// </para>
/// <para>
/// All string filters compare with <see cref="StringComparison.Ordinal"/> semantics in the
/// in-memory store, matching the exact-match column predicates the EF Core store emits.
/// </para>
/// </remarks>
public sealed record AuditHistoryQuery
{
    /// <summary>
    /// Maximum number of rows a single page may return when <see cref="Take"/> is left unset.
    /// Bounds an unfiltered query so a store never materialises an unbounded result by default.
    /// </summary>
    public const int DefaultPageSize = 100;

    /// <summary>
    /// Assembly-qualified name of the audited entity type to match (compared against
    /// <see cref="AuditLog.EntityType"/>). Null matches every type. To match a polymorphic
    /// base type across subclasses, use <see cref="EntityBaseType"/> instead.
    /// </summary>
    public string? EntityType { get; init; }

    /// <summary>
    /// Polymorphic base-type <see cref="System.Type.FullName"/> to match (compared against
    /// <see cref="AuditLog.EntityBaseType"/>). When set, rows whose
    /// <see cref="AuditLog.EntityBaseType"/> equals this value match, returning every subclass
    /// captured under the base type. Null leaves the base-type dimension unfiltered.
    /// </summary>
    public string? EntityBaseType { get; init; }

    /// <summary>
    /// Serialized primary key of a single entity to match (compared against
    /// <see cref="AuditLog.EntityId"/>). Null matches every entity of the selected type(s).
    /// </summary>
    public string? EntityId { get; init; }

    /// <summary>
    /// Restrict to a single <see cref="AuditAction"/> (Inserted / Updated / Deleted / SoftDeleted).
    /// Null leaves the action dimension unfiltered. Mutually combine with <see cref="EntityType"/>
    /// to ask, for example, "every delete of an Order".
    /// </summary>
    public AuditAction? Action { get; init; }

    /// <summary>User id (subject) to match (compared against <see cref="AuditLog.UserId"/>). Null leaves it unfiltered.</summary>
    public string? UserId { get; init; }

    /// <summary>Tenant id to match (compared against <see cref="AuditLog.TenantId"/>). Null leaves it unfiltered.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Inclusive lower bound on <see cref="AuditLog.OccurredOnUtc"/>. Null leaves the range open
    /// at the low end. Must be a UTC instant; the value is compared as-is.
    /// </summary>
    public DateTime? FromUtc { get; init; }

    /// <summary>
    /// Inclusive upper bound on <see cref="AuditLog.OccurredOnUtc"/>. Null leaves the range open
    /// at the high end. Must be a UTC instant; the value is compared as-is.
    /// </summary>
    public DateTime? ToUtc { get; init; }

    /// <summary>Number of matching rows to skip before the page begins. Defaults to 0. Negative values are rejected.</summary>
    public int Skip { get; init; }

    /// <summary>
    /// Maximum number of rows the page returns. When null, <see cref="DefaultPageSize"/> applies.
    /// Values below 1 are rejected so a page always carries at least one slot.
    /// </summary>
    public int? Take { get; init; }

    /// <summary>Ordering applied before paging. Defaults to <see cref="AuditHistoryOrder.NewestFirst"/>.</summary>
    public AuditHistoryOrder Order { get; init; } = AuditHistoryOrder.NewestFirst;

    /// <summary>
    /// Validates the paging and time-range invariants, throwing <see cref="ArgumentException"/>
    /// when they are violated. Stores call this before executing so every backend reports the
    /// same diagnostics for a malformed query.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="Skip"/> is negative, <see cref="Take"/> is below 1, or <see cref="FromUtc"/>
    /// is later than <see cref="ToUtc"/>.
    /// </exception>
    public void Validate()
    {
        if (Skip < 0)
        {
            throw new ArgumentException($"AuditHistoryQuery.Skip must be non-negative (got {Skip}).");
        }
        if (Take is { } take && take < 1)
        {
            throw new ArgumentException($"AuditHistoryQuery.Take must be at least 1 when set (got {take}).");
        }
        if (FromUtc is { } from && ToUtc is { } to && to < from)
        {
            throw new ArgumentException(
                $"AuditHistoryQuery time range is inverted: ToUtc ({to:O}) is earlier than FromUtc ({from:O}).");
        }
    }

    /// <summary>The effective page size: <see cref="Take"/> when set, otherwise <see cref="DefaultPageSize"/>.</summary>
    public int EffectiveTake => Take ?? DefaultPageSize;
}
