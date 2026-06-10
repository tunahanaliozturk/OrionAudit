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

    /// <summary>
    /// Per-tenant retention policies. The sweep evaluates each tenant's policy
    /// independently. Rows whose <see cref="AuditLog.TenantId"/> is not present in the
    /// dictionary fall back to <paramref name="fallback"/>.
    /// </summary>
    /// <param name="byTenantId">Map from tenant id to retention policy.</param>
    /// <param name="fallback">Policy used for tenants not present in <paramref name="byTenantId"/>. Must NOT be another <c>PerTenant</c> policy.</param>
    public static RetentionPolicy PerTenant(
        IReadOnlyDictionary<string, RetentionPolicy> byTenantId,
        RetentionPolicy fallback)
    {
        ArgumentNullException.ThrowIfNull(byTenantId);
        ArgumentNullException.ThrowIfNull(fallback);
        if (byTenantId.Count == 0)
        {
            throw new ArgumentException(
                "RetentionPolicy.PerTenant requires at least one tenant mapping.",
                nameof(byTenantId));
        }
        if (byTenantId.Values.Any(p => p is null))
        {
            // Null policy values would silently skip the sweep for that tenant - reject
            // at construction so the misconfiguration surfaces at startup. Consumers
            // wanting to opt-out a tenant pass `RetentionPolicy.None` explicitly.
            throw new ArgumentException(
                "RetentionPolicy.PerTenant: tenant policy values must not be null. " +
                "Use RetentionPolicy.None for tenants that should not be swept.",
                nameof(byTenantId));
        }
        if (byTenantId.Values.Any(p => p is PerTenantPolicy)
            || fallback is PerTenantPolicy)
        {
            throw new ArgumentException(
                "RetentionPolicy.PerTenant cannot nest another PerTenant policy.",
                nameof(byTenantId));
        }
        // Snapshot to a copy under Ordinal so subsequent mutations on the caller's dict
        // cannot invalidate the registry. Keeps lookup behaviour deterministic.
        var snapshot = byTenantId.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return new PerTenantPolicy(snapshot, fallback);
    }

    internal sealed record NonePolicy : RetentionPolicy;
    internal sealed record RetainForPolicy(TimeSpan Age) : RetentionPolicy;
    internal sealed record RetainCountPolicy(int Rows) : RetentionPolicy;
    internal sealed record PerTenantPolicy(
        IReadOnlyDictionary<string, RetentionPolicy> ByTenantId,
        RetentionPolicy Fallback) : RetentionPolicy;
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
