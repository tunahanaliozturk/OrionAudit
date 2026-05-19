using System.Reflection;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit;

/// <summary>
/// Fluent options surface exposed to the <c>AddOrionAudit&lt;TContext&gt;</c> configure callback.
/// Wraps an <see cref="AuditConfigurationBuilder"/> and tracks resolver registrations.
/// </summary>
public sealed class OrionAuditOptions
{
    internal AuditConfigurationBuilder ConfigurationBuilder { get; } = new();
    internal Type? UserResolverType { get; private set; }
    internal Type? TenantResolverType { get; private set; }
    internal string TableNameValue { get; private set; } = AuditLogEntityTypeConfiguration.DefaultTableName;
    internal HashSet<Assembly> ScanAssemblies { get; } = new();
    internal SnapshotPolicy SnapshotPolicy { get; private set; } = SnapshotPolicy.Never;

    /// <summary>Registers a type for audit with optional field-level overrides.</summary>
    public OrionAuditOptions Audit<T>(Action<AuditTypeBuilder<T>>? configure = null) where T : class
    {
        ConfigurationBuilder.Audit(configure);
        return this;
    }

    /// <summary>Registers the implementation type to use as <see cref="IAuditUserResolver"/>.</summary>
    public OrionAuditOptions UserResolver<TResolver>() where TResolver : class, IAuditUserResolver
    {
        UserResolverType = typeof(TResolver);
        return this;
    }

    /// <summary>Registers the implementation type to use as <see cref="IAuditTenantResolver"/>.</summary>
    public OrionAuditOptions TenantResolver<TResolver>() where TResolver : class, IAuditTenantResolver
    {
        TenantResolverType = typeof(TResolver);
        return this;
    }

    /// <summary>Overrides the audit-log table name (default <c>OrionAudit_Log</c>).</summary>
    public OrionAuditOptions TableName(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        TableNameValue = tableName;
        return this;
    }

    /// <summary>Adds an assembly to be scanned for <see cref="AuditableAttribute"/>-marked types.</summary>
    public OrionAuditOptions ScanAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ScanAssemblies.Add(assembly);
        return this;
    }

    /// <summary>
    /// Writes a full <see cref="AuditLog.Snapshot"/> every <paramref name="updates"/>-th Update for
    /// each audited entity. Reconstruction starts from the latest snapshot and replays only the
    /// diffs after it (turns O(N) into O(K) where K = updates since the last snapshot).
    /// </summary>
    public OrionAuditOptions SnapshotEvery(int updates)
    {
        SnapshotPolicy = SnapshotPolicy.Every(updates);
        return this;
    }

    /// <summary>Time-based variant of <see cref="SnapshotEvery(int)"/>.</summary>
    public OrionAuditOptions SnapshotEvery(TimeSpan elapsed)
    {
        SnapshotPolicy = SnapshotPolicy.EveryDuration(elapsed);
        return this;
    }
}
