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
                "RetentionPolicy.PerTenant cannot nest another PerTenant policy. " +
                "PerEntityType IS allowed inside PerTenant for per-(tenant, entity-type) windows.",
                nameof(byTenantId));
        }
        // Snapshot to a copy under Ordinal so subsequent mutations on the caller's dict
        // cannot invalidate the registry. Keeps lookup behaviour deterministic.
        var snapshot = byTenantId.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return new PerTenantPolicy(snapshot, fallback);
    }

    /// <summary>
    /// Per-(tenant, entity-type) retention policies. Each tenant can specify a distinct
    /// policy per <c>EntityType</c> name plus a tenant-level fallback for entity types
    /// not covered. Composes on top of <see cref="PerTenant"/> - a single tenant binds to
    /// a <see cref="PerEntityTypePolicy"/> internally that the sweep evaluates per entity
    /// group.
    /// </summary>
    /// <param name="byEntityType">Map from <c>EntityType</c> name to retention policy.</param>
    /// <param name="fallback">Policy used for entity types not present in <paramref name="byEntityType"/>. Must NOT be another <c>PerEntityType</c> or <c>PerTenant</c> policy.</param>
    public static RetentionPolicy PerEntityType(
        IReadOnlyDictionary<string, RetentionPolicy> byEntityType,
        RetentionPolicy fallback)
    {
        ArgumentNullException.ThrowIfNull(byEntityType);
        ArgumentNullException.ThrowIfNull(fallback);
        if (byEntityType.Count == 0)
        {
            throw new ArgumentException(
                "RetentionPolicy.PerEntityType requires at least one entity-type mapping.",
                nameof(byEntityType));
        }
        if (byEntityType.Values.Any(p => p is null))
        {
            throw new ArgumentException(
                "RetentionPolicy.PerEntityType: entity-type policy values must not be null. " +
                "Use RetentionPolicy.None for entity types that should not be swept.",
                nameof(byEntityType));
        }
        if (byEntityType.Values.Any(p => p is PerTenantPolicy or PerEntityTypePolicy)
            || fallback is PerTenantPolicy or PerEntityTypePolicy)
        {
            throw new ArgumentException(
                "RetentionPolicy.PerEntityType cannot nest another PerTenant or PerEntityType policy.",
                nameof(byEntityType));
        }
        var snapshot = byEntityType.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return new PerEntityTypePolicy(snapshot, fallback);
    }

    internal sealed record NonePolicy : RetentionPolicy;
    internal sealed record RetainForPolicy(TimeSpan Age) : RetentionPolicy;
    internal sealed record RetainCountPolicy(int Rows) : RetentionPolicy;
    internal sealed record PerTenantPolicy(
        IReadOnlyDictionary<string, RetentionPolicy> ByTenantId,
        RetentionPolicy Fallback) : RetentionPolicy;
    internal sealed record PerEntityTypePolicy(
        IReadOnlyDictionary<string, RetentionPolicy> ByEntityType,
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

    /// <summary>
    /// Optional wall-clock budget per sweep cycle. When set, the sweep stops dispatching
    /// to inner per-tenant / per-entity branches once <see cref="MaxSweepDuration"/>
    /// elapses since the cycle started, returning whatever total it has accumulated so
    /// far. Useful for operators who run retention on a maintenance window and need a
    /// guarantee that the sweep gives up control by a known deadline rather than
    /// chewing through MaxRowsPerSweep rows of a stuck backend.
    /// Default <see langword="null"/> = unlimited (preserves v0.7.11 behaviour).
    /// </summary>
    public TimeSpan? MaxSweepDuration { get; set; }

    /// <summary>
    /// When true, the sweep evaluates eligibility and reports the count of rows that
    /// WOULD have been removed, but does NOT actually delete (or archive) any row. The
    /// telemetry counter <c>orionaudit.retention.dry_run_rows</c> increments instead of
    /// <c>orionaudit.retention.rows_deleted</c>. Default false.
    /// </summary>
    /// <remarks>
    /// Operators use dry-run mode to validate a new <see cref="RetentionPolicy"/>
    /// (especially <see cref="RetentionPolicy.PerTenant"/> or
    /// <see cref="RetentionPolicy.PerEntityType"/>) on production data without touching
    /// any row. The hosted service emits its normal log line with the would-have-deleted
    /// count, so a follow-up production run with <see cref="DryRun"/> = false has a
    /// known expected magnitude.
    /// </remarks>
    public bool DryRun { get; set; }
}
