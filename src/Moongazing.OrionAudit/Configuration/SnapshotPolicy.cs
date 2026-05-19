namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// When to persist a full <see cref="AuditLog.Snapshot"/> on Update rows. Snapshots let the
/// reconstructor short-circuit diff replay: it picks the latest snapshot &lt;= <c>asOf</c> and
/// applies only the diffs after it, turning reconstruction into O(K) where K is the number of
/// updates since the snapshot.
/// </summary>
public abstract record SnapshotPolicy
{
    /// <summary>No periodic snapshots. Reconstruction replays from the Insert (O(N) total diffs).</summary>
    public static SnapshotPolicy Never { get; } = new NeverPolicy();

    /// <summary>Write a snapshot every <paramref name="updates"/>-th Update row for the entity.</summary>
    public static SnapshotPolicy Every(int updates)
    {
        if (updates < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(updates), updates, "Must be >= 1.");
        }
        return new EveryNthPolicy(updates);
    }

    /// <summary>Write a snapshot when <paramref name="elapsed"/> has passed since the last one.</summary>
    public static SnapshotPolicy EveryDuration(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Must be positive.");
        }
        return new EveryDurationPolicy(elapsed);
    }

    internal sealed record NeverPolicy : SnapshotPolicy;
    internal sealed record EveryNthPolicy(int Updates) : SnapshotPolicy;
    internal sealed record EveryDurationPolicy(TimeSpan Elapsed) : SnapshotPolicy;
}
