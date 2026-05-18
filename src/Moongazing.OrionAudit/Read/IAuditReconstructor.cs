namespace Moongazing.OrionAudit;

/// <summary>
/// Reconstructs entity state at a historical point in time by replaying audit-log diffs. Single
/// and batch overloads are provided. See documentation for performance characteristics.
/// </summary>
public interface IAuditReconstructor
{
    /// <summary>
    /// Returns the state of entity <typeparamref name="T"/> with the given primary key at
    /// <paramref name="asOf"/>, or null if the entity did not exist or was deleted at that time.
    /// Reconstruction is <em>O(N)</em> in the number of audit rows up to <paramref name="asOf"/>.
    /// For entities with thousands of historical changes, expect latency in the seconds.
    /// </summary>
    Task<T?> ReconstructAsync<T>(string entityId, DateTime asOf, CancellationToken cancellationToken = default)
        where T : class, new();

    /// <summary>
    /// Returns the state of each requested entity at <paramref name="asOf"/>. Uses a single audit
    /// query grouped by entity id; replays in bounded parallel. Missing or deleted entities map to
    /// null. Result key order matches input order.
    /// </summary>
    Task<IReadOnlyDictionary<string, T?>> ReconstructManyAsync<T>(
        IEnumerable<string> entityIds,
        DateTime asOf,
        CancellationToken cancellationToken = default)
        where T : class, new();
}
