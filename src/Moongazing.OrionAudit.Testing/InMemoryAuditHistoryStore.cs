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

        IEnumerable<AuditLog> ordered = query.Order == AuditHistoryOrder.OldestFirst
            ? matched.OrderBy(r => r.OccurredOnUtc).ThenBy(r => r.Id)
            : matched.OrderByDescending(r => r.OccurredOnUtc).ThenByDescending(r => r.Id);

        var items = ordered.Skip(query.Skip).Take(query.EffectiveTake).ToList();
        return Task.FromResult(new AuditHistoryPage(items, total, query.Skip, query.EffectiveTake));
    }

    /// <inheritdoc />
    public override Task<AuditCompactionResult> CompactAsync(AuditCompactionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            var history = rows
                .Where(r => string.Equals(r.EntityType, request.EntityType, StringComparison.Ordinal)
                    && string.Equals(r.EntityId, request.EntityId, StringComparison.Ordinal)
                    && (request.TenantId is null || string.Equals(r.TenantId, request.TenantId, StringComparison.Ordinal)))
                .ToList();

            var plan = AuditHistoryCompactor.Plan(history, request.RetainTail);
            if (!plan.IsEffective)
            {
                return Task.FromResult(plan.ToResult());
            }

            var removeIds = plan.RowsToRemove.Select(r => r.Id).ToHashSet();
            rows.RemoveAll(r => removeIds.Contains(r.Id));
            rows.Add(plan.SnapshotRow!);
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
        return true;
    }
}
