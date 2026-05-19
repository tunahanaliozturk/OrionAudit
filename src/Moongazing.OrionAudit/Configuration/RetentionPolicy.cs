namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// Controls how long <see cref="AuditLog"/> rows are kept. The background sweep service
/// (registered automatically by <c>AddOrionAudit</c> when this policy is not
/// <see cref="None"/>) deletes rows past the retention window.
/// </summary>
public abstract record RetentionPolicy
{
    /// <summary>Audit rows are kept forever. No background sweep runs.</summary>
    public static RetentionPolicy None { get; } = new NonePolicy();

    /// <summary>Delete audit rows older than <paramref name="age"/> (by <c>OccurredOnUtc</c>).</summary>
    public static RetentionPolicy RetainFor(TimeSpan age)
    {
        if (age <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(age), age, "Must be positive.");
        }
        return new RetainForPolicy(age);
    }

    /// <summary>
    /// Keep the latest <paramref name="rows"/> rows per (EntityType, EntityId, TenantId).
    /// Older rows beyond the cap are deleted.
    /// </summary>
    public static RetentionPolicy RetainCount(int rows)
    {
        if (rows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "Must be >= 1.");
        }
        return new RetainCountPolicy(rows);
    }

    internal sealed record NonePolicy : RetentionPolicy;
    internal sealed record RetainForPolicy(TimeSpan Age) : RetentionPolicy;
    internal sealed record RetainCountPolicy(int Rows) : RetentionPolicy;
}

/// <summary>
/// Tunables for the background retention sweep. Defaults: 1-hour interval, 10 000 rows per
/// sweep so each batch transaction stays small.
/// </summary>
public sealed class RetentionSweepOptions
{
    /// <summary>How often the hosted service runs a sweep. Default: 1 hour.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Upper bound on rows deleted per sweep, to keep transactions short. Default: 10 000.</summary>
    public int MaxRowsPerSweep { get; set; } = 10_000;
}
