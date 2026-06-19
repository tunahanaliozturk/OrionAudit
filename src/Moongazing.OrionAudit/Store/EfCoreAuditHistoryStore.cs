using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionAudit.Store;

/// <summary>
/// <see cref="IAuditHistoryStore"/> backed by the consumer's <see cref="DbContext"/>. Translates
/// an <see cref="AuditHistoryQuery"/> into a server-side query over the <see cref="AuditLog"/> set
/// and applies compaction inside a transaction (insert the compacted snapshot, delete the folded
/// rows). This is the default store registered by the DI wiring.
/// </summary>
public sealed class EfCoreAuditHistoryStore : AuditHistoryStoreBase
{
    private readonly DbContext context;

    /// <summary>Initializes a new instance reading from and writing to the supplied <see cref="DbContext"/>.</summary>
    public EfCoreAuditHistoryStore(DbContext context)
        => this.context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public override async Task<AuditHistoryPage> QueryAsync(AuditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();

        var filtered = ApplyFilters(context.Set<AuditLog>().AsNoTracking(), query);

        var total = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);
        if (total == 0)
        {
            return AuditHistoryPage.Empty(query.Skip, query.EffectiveTake);
        }

        var ordered = query.Order == AuditHistoryOrder.OldestFirst
            ? filtered.OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id)
            : filtered.OrderByDescending(a => a.OccurredOnUtc).ThenByDescending(a => a.Id);

        var items = await ordered
            .Skip(query.Skip)
            .Take(query.EffectiveTake)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new AuditHistoryPage(items, total, query.Skip, query.EffectiveTake);
    }

    /// <inheritdoc />
    public override async Task<AuditCompactionResult> CompactAsync(AuditCompactionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var rows = await context.Set<AuditLog>()
            .Where(a => a.EntityType == request.EntityType
                && a.EntityId == request.EntityId
                && (request.TenantId == null || a.TenantId == request.TenantId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var plan = AuditHistoryCompactor.Plan(rows, request.RetainTail);
        if (!plan.IsEffective)
        {
            return plan.ToResult();
        }

        // Insert the compacted snapshot and remove the folded rows together. SaveChanges runs in a
        // single transaction by default, so a failure leaves the history untouched (the folded rows
        // are not deleted unless the snapshot row also lands).
        context.Set<AuditLog>().Add(plan.SnapshotRow!);
        context.Set<AuditLog>().RemoveRange(plan.RowsToRemove);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return plan.ToResult();
    }

    private static IQueryable<AuditLog> ApplyFilters(IQueryable<AuditLog> source, AuditHistoryQuery query)
    {
        if (query.EntityType is { } entityType)
        {
            source = source.Where(a => a.EntityType == entityType);
        }
        if (query.EntityBaseType is { } baseType)
        {
            source = source.Where(a => a.EntityBaseType == baseType);
        }
        if (query.EntityId is { } entityId)
        {
            source = source.Where(a => a.EntityId == entityId);
        }
        if (query.Action is { } action)
        {
            source = source.Where(a => a.Action == action);
        }
        if (query.UserId is { } userId)
        {
            source = source.Where(a => a.UserId == userId);
        }
        if (query.TenantId is { } tenantId)
        {
            source = source.Where(a => a.TenantId == tenantId);
        }
        if (query.FromUtc is { } fromUtc)
        {
            source = source.Where(a => a.OccurredOnUtc >= fromUtc);
        }
        if (query.ToUtc is { } toUtc)
        {
            source = source.Where(a => a.OccurredOnUtc <= toUtc);
        }
        return source;
    }
}
