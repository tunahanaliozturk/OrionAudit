using Moongazing.OrionAudit.Store;

namespace Moongazing.OrionAudit.Testing;

/// <summary>
/// In-memory <see cref="IAuditHistoryStore"/> over a mutable list of <see cref="AuditLog"/> rows.
/// Implements both querying and snapshot compaction with no persistence dependency, so tests (and
/// consumers prototyping against the store abstraction) can exercise the full read/compaction
/// surface without a database. String filters compare with <see cref="StringComparison.Ordinal"/>,
/// matching the exact-match column predicates the EF Core store emits.
/// </summary>
public sealed class InMemoryAuditHistoryStore : AuditHistoryStoreBase
{
    private readonly List<AuditLog> rows;

    // Plain object lock rather than System.Threading.Lock so the type stays multi-target safe:
    // Lock is net9+ only and this package builds for net8 as well.
    private readonly object gate = new();

    /// <summary>Initializes an empty store.</summary>
    public InMemoryAuditHistoryStore() => rows = new List<AuditLog>();

    /// <summary>Initializes a store seeded with a copy of <paramref name="seed"/>.</summary>
    public InMemoryAuditHistoryStore(IEnumerable<AuditLog> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        rows = seed.ToList();
    }

    /// <summary>Current row count. Primarily for test assertions.</summary>
    public int Count
    {
        get { lock (gate) { return rows.Count; } }
    }

    /// <summary>Adds a row to the store. Returns this instance for chaining.</summary>
    public InMemoryAuditHistoryStore Add(AuditLog row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (gate) { rows.Add(row); }
        return this;
    }

    /// <summary>Adds many rows to the store. Returns this instance for chaining.</summary>
    public InMemoryAuditHistoryStore AddRange(IEnumerable<AuditLog> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (gate) { rows.AddRange(batch); }
        return this;
    }

    /// <summary>A defensive snapshot of all rows currently held. Primarily for test assertions.</summary>
    public IReadOnlyList<AuditLog> Snapshot()
    {
        lock (gate) { return rows.ToList(); }
    }

    /// <inheritdoc />
    public override Task<AuditHistoryPage> QueryAsync(AuditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        List<AuditLog> matched;
        lock (gate)
        {
            matched = rows.Where(r => Matches(r, query)).ToList();
        }

        var total = matched.Count;
        if (total == 0)
        {
            return Task.FromResult(AuditHistoryPage.Empty(query.Skip, query.EffectiveTake));
        }

        var items = ApplyOrder(matched, query).Skip(query.Skip).Take(query.EffectiveTake).ToList();
        return Task.FromResult(new AuditHistoryPage(items, total, query.Skip, query.EffectiveTake));
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<AuditAggregateBucket>> AggregateAsync(
        AuditAggregationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        List<AuditLog> matched;
        lock (gate)
        {
            matched = rows.Where(r => Matches(r, query.Filter)).ToList();
        }

        IReadOnlyList<AuditAggregateBucket> buckets = query.GroupBy switch
        {
            AuditAggregateBy.Action => matched
                .GroupBy(r => r.Action)
                .Select(g => new AuditAggregateBucket(g.Key.ToString(), g.LongCount()))
                .ToList(),
            AuditAggregateBy.EntityType => matched
                .GroupBy(r => r.EntityType, StringComparer.Ordinal)
                .Select(g => new AuditAggregateBucket(g.Key, g.LongCount()))
                .ToList(),
            AuditAggregateBy.UserId => matched
                .GroupBy(r => r.UserId, StringComparer.Ordinal)
                .Select(g => new AuditAggregateBucket(g.Key, g.LongCount()))
                .ToList(),
            AuditAggregateBy.TenantId => matched
                .GroupBy(r => r.TenantId, StringComparer.Ordinal)
                .Select(g => new AuditAggregateBucket(g.Key, g.LongCount()))
                .ToList(),
            AuditAggregateBy.TimeBucket => matched
                .GroupBy(r => TruncateToBucket(r.OccurredOnUtc, query.TimeBucket))
                .Select(g => new AuditAggregateBucket(
                    g.Key.ToString("O", System.Globalization.CultureInfo.InvariantCulture), g.LongCount())
                {
                    BucketStartUtc = g.Key,
                })
                .ToList(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(query), query.GroupBy, "Unknown audit aggregation group."),
        };

        return Task.FromResult(buckets);
    }

    private static DateTime TruncateToBucket(DateTime occurredOnUtc, AuditTimeBucket bucket)
    {
        var u = occurredOnUtc.Kind == DateTimeKind.Utc
            ? occurredOnUtc
            : DateTime.SpecifyKind(occurredOnUtc, DateTimeKind.Utc);
        return bucket switch
        {
            AuditTimeBucket.Hour => new DateTime(u.Year, u.Month, u.Day, u.Hour, 0, 0, DateTimeKind.Utc),
            AuditTimeBucket.Month => new DateTime(u.Year, u.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => new DateTime(u.Year, u.Month, u.Day, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    private static IEnumerable<AuditLog> ApplyOrder(IEnumerable<AuditLog> source, AuditHistoryQuery query)
    {
        var descending = query.Order == AuditHistoryOrder.NewestFirst;

        // Mirror EfCoreAuditHistoryStore.ApplyOrder: the chosen field is primary, then OccurredOnUtc
        // and Id are appended as stable tie-breaks. For the default OccurredOn field this collapses to
        // the historical "(OccurredOnUtc, Id)" ordering.
        IOrderedEnumerable<AuditLog> ordered = query.SortBy switch
        {
            AuditHistorySortField.EntityType => descending
                ? source.OrderByDescending(r => r.EntityType, StringComparer.Ordinal)
                : source.OrderBy(r => r.EntityType, StringComparer.Ordinal),
            AuditHistorySortField.Action => descending
                ? source.OrderByDescending(r => r.Action)
                : source.OrderBy(r => r.Action),
            // Mirror EfCoreAuditHistoryStore: a nullable sort key (UserId) is ordered NULLS LAST in
            // both directions via a leading "key is null" sort (false before true), so paging is
            // stable and matches the EF Core store row-for-row.
            AuditHistorySortField.UserId => descending
                ? source.OrderBy(r => r.UserId is null).ThenByDescending(r => r.UserId, StringComparer.Ordinal)
                : source.OrderBy(r => r.UserId is null).ThenBy(r => r.UserId, StringComparer.Ordinal),
            _ => descending
                ? source.OrderByDescending(r => r.OccurredOnUtc)
                : source.OrderBy(r => r.OccurredOnUtc),
        };

        if (query.SortBy != AuditHistorySortField.OccurredOn)
        {
            ordered = descending
                ? ordered.ThenByDescending(r => r.OccurredOnUtc)
                : ordered.ThenBy(r => r.OccurredOnUtc);
        }

        return descending
            ? ordered.ThenByDescending(r => r.Id)
            : ordered.ThenBy(r => r.Id);
    }

    /// <inheritdoc />
    public override Task<AuditCompactionResult> CompactAsync(AuditCompactionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            // Tenant scoping mirrors EfCoreAuditHistoryStore.CompactAsync: null = all tenants,
            // "" = the no-tenant stream only (column null OR ""), a concrete value = exact match. An
            // empty-string tenant must never widen to all tenants.
            var requestedTenant = request.TenantId;
            var noTenantStream = requestedTenant is { Length: 0 };
            var history = rows
                .Where(r => string.Equals(r.EntityType, request.EntityType, StringComparison.Ordinal)
                    && string.Equals(r.EntityId, request.EntityId, StringComparison.Ordinal)
                    && (requestedTenant is null
                        || (noTenantStream && string.IsNullOrEmpty(r.TenantId))
                        || (!noTenantStream && string.Equals(r.TenantId, requestedTenant, StringComparison.Ordinal))))
                .ToList();

            var plan = AuditHistoryCompactor.Plan(history, request.RetainTail);
            if (!plan.IsEffective)
            {
                return Task.FromResult(plan.ToResult());
            }

            // The plan's SnapshotRow is the boundary row mutated in place; it already lives in the
            // list (same reference held in `history`), so we only remove the strictly-older folded
            // rows and leave the mutated boundary where it sits. Reusing the boundary's Id and
            // OccurredOnUtc keeps the snapshot ahead of the retained tail at an equal timestamp.
            var removeIds = plan.RowsToRemove.Select(r => r.Id).ToHashSet();
            rows.RemoveAll(r => removeIds.Contains(r.Id));
            return Task.FromResult(plan.ToResult());
        }
    }

    private static bool Matches(AuditLog row, AuditHistoryQuery query)
    {
        if (query.EntityType is { } et && !string.Equals(row.EntityType, et, StringComparison.Ordinal))
        {
            return false;
        }
        if (query.EntityBaseType is { } bt && !string.Equals(row.EntityBaseType, bt, StringComparison.Ordinal))
        {
            return false;
        }
        if (query.EntityId is { } eid && !string.Equals(row.EntityId, eid, StringComparison.Ordinal))
        {
            return false;
        }
        if (query.Action is { } action && row.Action != action)
        {
            return false;
        }
        if (query.UserId is { } uid && !string.Equals(row.UserId, uid, StringComparison.Ordinal))
        {
            return false;
        }
        if (query.TenantId is { } tid && !string.Equals(row.TenantId, tid, StringComparison.Ordinal))
        {
            return false;
        }
        if (query.FromUtc is { } from && row.OccurredOnUtc < from)
        {
            return false;
        }
        if (query.ToUtc is { } to && row.OccurredOnUtc > to)
        {
            return false;
        }
        // v0.11.0 richer filters, matching EfCoreAuditHistoryStore.ApplyFilters semantics.
        if (query.HasEntityTypesFilter && !query.EntityTypes!.Contains(row.EntityType, StringComparer.Ordinal))
        {
            return false;
        }
        if (query.HasActionsFilter && !query.Actions!.Contains(row.Action))
        {
            return false;
        }
        if (query.CorrelationId is { } cid && !string.Equals(row.CorrelationId, cid, StringComparison.Ordinal))
        {
            return false;
        }
        if (query.UserType is { } ut && !string.Equals(row.UserType, ut, StringComparison.Ordinal))
        {
            return false;
        }
        // Mirror EfCoreAuditHistoryStore: a row matches the ChangedPath filter when its Diff contains
        // any op-anchored exact or prefix token. The tokens already carry the "op":"<verb>", anchor, so
        // a path inside an operation's value cannot satisfy the match.
        if (query.ChangedPath is not null)
        {
            var matchesPath = false;
            foreach (var token in query.ChangedPathExactTokens)
            {
                if (row.Diff.Contains(token, StringComparison.Ordinal))
                {
                    matchesPath = true;
                    break;
                }
            }
            if (!matchesPath)
            {
                foreach (var token in query.ChangedPathPrefixTokens)
                {
                    if (row.Diff.Contains(token, StringComparison.Ordinal))
                    {
                        matchesPath = true;
                        break;
                    }
                }
            }
            if (!matchesPath)
            {
                return false;
            }
        }
        return true;
    }
}
