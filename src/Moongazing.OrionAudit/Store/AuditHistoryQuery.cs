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
/// The row dimension an <see cref="AuditHistoryQuery"/> sorts by before paging. The
/// <see cref="AuditLog.Id"/> is always appended as a stable tie-break so paging is deterministic
/// even when two rows share the sort key.
/// </summary>
public enum AuditHistorySortField
{
    /// <summary>Sort by <see cref="AuditLog.OccurredOnUtc"/> (chronological). The default; preserves pre-v0.11 behaviour.</summary>
    OccurredOn = 0,

    /// <summary>Sort by <see cref="AuditLog.EntityType"/>, then by <see cref="AuditLog.OccurredOnUtc"/> within a type.</summary>
    EntityType = 1,

    /// <summary>Sort by <see cref="AuditLog.Action"/>, then by <see cref="AuditLog.OccurredOnUtc"/> within an action.</summary>
    Action = 2,

    /// <summary>
    /// Sort by <see cref="AuditLog.UserId"/>, then by <see cref="AuditLog.OccurredOnUtc"/> within a user.
    /// <see cref="AuditLog.UserId"/> is nullable; rows with no user always sort <em>last</em> regardless
    /// of <see cref="AuditHistoryOrder"/> direction (a fixed NULLS LAST contract), so paging over this
    /// field is stable and portable across providers whose default null placement differs.
    /// </summary>
    UserId = 3,
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
    /// Match a <em>set</em> of audited entity types (compared against <see cref="AuditLog.EntityType"/>):
    /// a row matches when its <see cref="AuditLog.EntityType"/> is any value in this collection.
    /// Null or empty leaves this dimension unfiltered. Composes with the single-value
    /// <see cref="EntityType"/> as an additional AND constraint, so both must hold when both are set
    /// (typically you set one or the other). Added in v0.11.0.
    /// </summary>
    public IReadOnlyCollection<string>? EntityTypes { get; init; }

    /// <summary>
    /// Match a <em>set</em> of <see cref="AuditAction"/> values (for example "inserts or deletes"):
    /// a row matches when its <see cref="AuditLog.Action"/> is any value in this collection. Null or
    /// empty leaves this dimension unfiltered. Composes with the single-value <see cref="Action"/> as
    /// an additional AND constraint. Added in v0.11.0.
    /// </summary>
    public IReadOnlyCollection<AuditAction>? Actions { get; init; }

    /// <summary>
    /// Correlation / trace id to match (compared against <see cref="AuditLog.CorrelationId"/>). Null
    /// leaves it unfiltered. Lets a caller pull every audit row written under one logical operation
    /// (an HTTP request, a background job run). Added in v0.11.0.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// User classification to match (compared against <see cref="AuditLog.UserType"/>: <c>"user"</c>,
    /// <c>"system"</c>, <c>"job"</c>, ...). Null leaves it unfiltered. Added in v0.11.0.
    /// </summary>
    public string? UserType { get; init; }

    /// <summary>
    /// Value-change predicate: keep only rows whose change touched this JSON Patch path (compared
    /// against the RFC 6902 operations in <see cref="AuditLog.Diff"/>). A row matches when its
    /// <see cref="AuditLog.Diff"/> contains an operation whose <c>"path"</c> equals this value or is
    /// nested beneath it (so <c>"/address"</c> also matches a change to <c>"/address/city"</c>). The
    /// value must be a valid RFC 6901 JSON Pointer beginning with <c>'/'</c>. Null leaves the dimension
    /// unfiltered. Added in v0.11.0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The match is a substring/prefix test over the stored <see cref="AuditLog.Diff"/> JSON, chosen so
    /// it can run server-side on every backend (a relational <c>LIKE</c>) without parsing JSON in the
    /// database. The candidate tokens are anchored to a patch operation's <c>"path"</c> member by
    /// including the <c>"op":"&lt;verb&gt;",</c> prefix that always immediately precedes it in the
    /// captured JSON (the operation object is serialized op-then-path). Anchoring this way means a path
    /// string that appears inside an operation's <c>"value"</c> — for example an audited property
    /// literally named <c>path</c> whose value serializes as <c>"value":{"path":"/status"}</c> — does
    /// not falsely match, because that occurrence is not preceded by an <c>"op":"&lt;verb&gt;",</c>
    /// member.
    /// </para>
    /// <para>
    /// Each anchored token is closed by either a quote (exact match, <c>"path":"/p"</c>) or a slash
    /// (nested-prefix, <c>"path":"/p/</c>), so a sibling whose name merely starts with the same text
    /// (<c>/postalCode</c> does not match a query for <c>/post</c>) is never matched. Whitespace-free
    /// canonical JSON is assumed, which is what the capture path emits via <c>System.Text.Json</c>.
    /// </para>
    /// </remarks>
    public string? ChangedPath { get; init; }

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
    /// The dimension to sort by before paging. Defaults to <see cref="AuditHistorySortField.OccurredOn"/>,
    /// which preserves the pre-v0.11 chronological order. <see cref="Order"/> chooses the direction
    /// (ascending / descending) applied to the chosen field; <see cref="AuditLog.OccurredOnUtc"/> and
    /// then <see cref="AuditLog.Id"/> are always appended as stable tie-breaks. Added in v0.11.0.
    /// </summary>
    public AuditHistorySortField SortBy { get; init; } = AuditHistorySortField.OccurredOn;

    /// <summary>
    /// Validates the paging and time-range invariants, throwing <see cref="ArgumentException"/>
    /// when they are violated. Stores call this before executing so every backend reports the
    /// same diagnostics for a malformed query.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="Skip"/> is negative, <see cref="Take"/> is below 1, <see cref="FromUtc"/> or
    /// <see cref="ToUtc"/> is not a UTC instant (<see cref="DateTimeKind.Utc"/>), or
    /// <see cref="FromUtc"/> is later than <see cref="ToUtc"/>.
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
        // The bounds compare directly against OccurredOnUtc (a UTC instant), so a Local or
        // Unspecified DateTime would silently shift the filter window. Enforce the documented
        // UTC-only contract rather than reinterpreting the caller's wall-clock time.
        if (FromUtc is { } fromKind && fromKind.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                $"AuditHistoryQuery.FromUtc must be a UTC instant (DateTimeKind.Utc), but its Kind is {fromKind.Kind}.");
        }
        if (ToUtc is { } toKind && toKind.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                $"AuditHistoryQuery.ToUtc must be a UTC instant (DateTimeKind.Utc), but its Kind is {toKind.Kind}.");
        }
        if (FromUtc is { } from && ToUtc is { } to && to < from)
        {
            throw new ArgumentException(
                $"AuditHistoryQuery time range is inverted: ToUtc ({to:O}) is earlier than FromUtc ({from:O}).");
        }
        // A JSON Pointer (RFC 6901) is either the empty string (whole document) or a sequence of
        // "/"-prefixed reference tokens. The audit diff never targets the whole document, so a usable
        // predicate must start with '/'; rejecting anything else turns a typo ("name" for "/name") into
        // a clear error rather than a silently-empty result. Beyond the leading slash, the token escape
        // grammar is validated too: '~' is an escape introducer and must be followed by '0' (literal
        // '~') or '1' (literal '/'); a dangling or mis-followed '~' is malformed, so we reject it rather
        // than emit a LIKE token that can never match the persisted, correctly-escaped JSON.
        if (ChangedPath is { } path && !IsValidJsonPointer(path))
        {
            throw new ArgumentException(
                $"AuditHistoryQuery.ChangedPath must be a valid RFC 6901 JSON Pointer beginning with '/' (got \"{path}\").");
        }
    }

    // RFC 6901: a non-empty JSON Pointer is "/" ( reference-token "/" )* where each reference-token
    // is a run of unescaped chars and the escape sequences "~0" / "~1". We require a leading '/'
    // (the audit diff never points at the whole document) and that every '~' be immediately followed
    // by '0' or '1'. Any other character is a legal literal inside a token, so no further restriction
    // is imposed.
    private static bool IsValidJsonPointer(string pointer)
    {
        if (pointer.Length == 0 || pointer[0] != '/')
        {
            return false;
        }
        for (var i = 0; i < pointer.Length; i++)
        {
            if (pointer[i] != '~')
            {
                continue;
            }
            if (i + 1 >= pointer.Length || (pointer[i + 1] != '0' && pointer[i + 1] != '1'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The effective page size: <see cref="Take"/> when set, otherwise <see cref="DefaultPageSize"/>.</summary>
    public int EffectiveTake => Take ?? DefaultPageSize;

    /// <summary>
    /// True when <see cref="EntityTypes"/> carries at least one value, i.e. the set filter is active.
    /// Exposed so a custom <see cref="IAuditHistoryStore"/> can branch on the set filter the same way
    /// the in-box stores do.
    /// </summary>
    public bool HasEntityTypesFilter => EntityTypes is { Count: > 0 };

    /// <summary>
    /// True when <see cref="Actions"/> carries at least one value, i.e. the set filter is active.
    /// Exposed for the same reason as <see cref="HasEntityTypesFilter"/>.
    /// </summary>
    public bool HasActionsFilter => Actions is { Count: > 0 };

    // The RFC 6902 op verbs a stored diff can carry. Capture emits only add / remove / replace
    // (Json6902.Compute), but an imported or replayed patch may carry move / copy / test, and those
    // also have a "path" target worth matching. Each verb anchors a ChangedPath token so a "path"
    // member is only matched when it belongs to a real operation object, never to operation value
    // content that happens to contain a "path" key.
    private static readonly string[] PatchOpVerbs =
        { "add", "remove", "replace", "move", "copy", "test" };

    /// <summary>
    /// The op-anchored exact-path tokens a <see cref="ChangedPath"/> match looks for in a row's
    /// <see cref="AuditLog.Diff"/>: one <c>"op":"&lt;verb&gt;","path":"/p"</c> per RFC 6902 verb
    /// (canonical, whitespace-free JSON). A row whose diff contains any of these substrings changed
    /// exactly the requested path. Empty when no path filter is set. Exposed so a custom store can reuse
    /// the exact match semantics the in-box stores apply.
    /// </summary>
    /// <remarks>
    /// Anchoring on the <c>"op":"&lt;verb&gt;",</c> prefix (which always precedes <c>"path"</c> in the
    /// serialized operation object) is what keeps a path that appears inside an operation's
    /// <c>"value"</c> from falsely matching. See the remarks on <see cref="ChangedPath"/>.
    /// </remarks>
    public IReadOnlyList<string> ChangedPathExactTokens
        => ChangedPath is { } p
            ? BuildAnchoredTokens(EscapeForJsonString(p), suffix: "\"")
            : Array.Empty<string>();

    /// <summary>
    /// The op-anchored nested-prefix tokens a <see cref="ChangedPath"/> match also looks for: one
    /// <c>"op":"&lt;verb&gt;","path":"/p/</c> per RFC 6902 verb (note the trailing slash, no closing
    /// quote) so a change to a descendant (<c>/p/child</c>) matches a query for <c>/p</c>. Empty when no
    /// path filter is set. Exposed for the same reason as <see cref="ChangedPathExactTokens"/>.
    /// </summary>
    public IReadOnlyList<string> ChangedPathPrefixTokens
        => ChangedPath is { } p
            ? BuildAnchoredTokens(EscapeForJsonString(p), suffix: "/")
            : Array.Empty<string>();

    private static string[] BuildAnchoredTokens(string escapedPath, string suffix)
    {
        var tokens = new string[PatchOpVerbs.Length];
        for (var i = 0; i < PatchOpVerbs.Length; i++)
        {
            tokens[i] = $"\"op\":\"{PatchOpVerbs[i]}\",\"path\":\"{escapedPath}{suffix}";
        }
        return tokens;
    }

    // The diff is stored as System.Text.Json output, so a path segment containing a quote or
    // backslash is escaped there too. Mirror the minimal escaping STJ applies to a JSON string's
    // structural characters so the LIKE/Contains token matches the persisted bytes. Audit property
    // paths are almost always plain identifiers, but escaping keeps the predicate correct for the
    // rare quoted/escaped segment instead of silently missing it.
    private static string EscapeForJsonString(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
}
