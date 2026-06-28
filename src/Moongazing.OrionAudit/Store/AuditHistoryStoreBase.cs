namespace Moongazing.OrionAudit.Store;

/// <summary>
/// Base class for <see cref="IAuditHistoryStore"/> implementations that supplies a
/// capability-default of <see cref="NotSupportedException"/> for every operation. Derive from this
/// and override only the members the backend can honour, leaving the rest to throw a clear
/// "not supported by this store" error.
/// </summary>
/// <remarks>
/// This is the same capability-default shape the family uses elsewhere (a default implementation a
/// consumer can selectively replace). A backend that supports reads but not in-place compaction
/// overrides <see cref="QueryAsync"/> and inherits the throwing <see cref="CompactAsync"/>; a
/// backend that supports neither can be registered as-is to make the "audit storage is opaque
/// here" contract explicit at the call site rather than via a missing-method surprise.
/// </remarks>
public abstract class AuditHistoryStoreBase : IAuditHistoryStore
{
    /// <inheritdoc />
    /// <remarks>The default throws <see cref="NotSupportedException"/>; override to support querying.</remarks>
    public virtual Task<AuditHistoryPage> QueryAsync(AuditHistoryQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support querying audit history. Override " +
            $"{nameof(QueryAsync)} or use a store backed by a queryable persistence layer.");

    /// <inheritdoc />
    /// <remarks>The default throws <see cref="NotSupportedException"/>; override to support aggregation.</remarks>
    public virtual Task<IReadOnlyList<AuditAggregateBucket>> AggregateAsync(AuditAggregationQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support aggregating audit history. Override " +
            $"{nameof(AggregateAsync)} or use a store backed by a queryable persistence layer.");

    /// <inheritdoc />
    /// <remarks>The default throws <see cref="NotSupportedException"/>; override to support compaction.</remarks>
    public virtual Task<AuditCompactionResult> CompactAsync(AuditCompactionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support snapshot compaction. Override " +
            $"{nameof(CompactAsync)} or use a store whose backend allows in-place row removal.");
}
