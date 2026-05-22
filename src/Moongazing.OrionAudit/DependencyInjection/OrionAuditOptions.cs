using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Serialization;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit;

/// <summary>
/// Fluent options surface exposed to the <c>AddOrionAudit&lt;TContext&gt;</c> configure callback.
/// Wraps an <see cref="AuditConfigurationBuilder"/> and tracks resolver registrations.
/// </summary>
public sealed class OrionAuditOptions
{
    /// <summary>
    /// The underlying fluent configuration builder. Exposed so the source-generated
    /// <c>RegisterAuditedTypes(AuditConfigurationBuilder)</c> method on an
    /// <c>[OrionAuditModule]</c> class can register audited types directly.
    /// </summary>
    public AuditConfigurationBuilder ConfigurationBuilder { get; } = new();
    internal Type? UserResolverType { get; private set; }
    internal Type? TenantResolverType { get; private set; }
    internal string TableNameValue { get; private set; } = AuditLogEntityTypeConfiguration.DefaultTableName;
    internal HashSet<Assembly> ScanAssemblies { get; } = new();
    internal SnapshotPolicy SnapshotPolicy { get; private set; } = SnapshotPolicy.Never;
    internal RetentionPolicy RetentionPolicy { get; private set; } = RetentionPolicy.None;
    internal RetentionSweepOptions SweepOptions { get; } = new();
    internal JsonSerializerContext? JsonContext { get; private set; }

    /// <summary>True when <see cref="UseAsyncCapture"/> has been called.</summary>
    public bool AsyncCaptureEnabled { get; private set; }

    /// <summary>The async-capture tunables. Meaningful only when <see cref="AsyncCaptureEnabled"/> is true.</summary>
    public AsyncCaptureOptions AsyncCaptureOptions { get; private set; } = new();

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

    /// <summary>
    /// Adds an assembly to be scanned for <see cref="AuditableAttribute"/>-marked types via
    /// reflection. Reflective path; for trim/AOT consumers, prefer the source-generated
    /// <c>RegisterAuditedTypes</c> method on an <c>[OrionAuditModule]</c> partial class.
    /// </summary>
    [RequiresUnreferencedCode("Uses AuditableTypeDiscovery.Discover which scans the supplied assembly via reflection. Use the [OrionAuditModule] source generator instead.")]
    [RequiresDynamicCode("Uses AuditableTypeDiscovery.Discover which scans the supplied assembly via reflection.")]
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

    /// <summary>
    /// Deletes audit rows older than <paramref name="age"/>. The hosted sweep runs every hour
    /// by default; override via <see cref="RetentionSweepInterval"/>.
    /// </summary>
    public OrionAuditOptions RetainFor(TimeSpan age)
    {
        RetentionPolicy = RetentionPolicy.RetainFor(age);
        return this;
    }

    /// <summary>Keep the latest <paramref name="rows"/> audit rows per audited entity instance.</summary>
    public OrionAuditOptions RetainCount(int rows)
    {
        RetentionPolicy = RetentionPolicy.RetainCount(rows);
        return this;
    }

    /// <summary>Overrides the background sweep interval (default: 1 hour).</summary>
    public OrionAuditOptions RetentionSweepInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Must be positive.");
        }
        SweepOptions.SweepInterval = interval;
        return this;
    }

    /// <summary>Caps how many rows the background sweep may delete per cycle (default: 10 000).</summary>
    public OrionAuditOptions MaxRowsPerSweep(int max)
    {
        if (max < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "Must be >= 1.");
        }
        SweepOptions.MaxRowsPerSweep = max;
        return this;
    }

    /// <summary>
    /// Supplies a System.Text.Json source-generated <see cref="JsonSerializerContext"/> for
    /// snapshot serialisation and time-travel deserialisation. When set, the snapshot builder
    /// and reconstructor route audited types through the context instead of through the
    /// reflective <c>JsonSerializer.SerializeToNode</c> / <c>Deserialize&lt;T&gt;</c> paths —
    /// trim-safe and Native-AOT-clean. When null (default) the library falls back to
    /// reflection (which emits trim warnings under <c>PublishAot</c>).
    /// </summary>
    public OrionAuditOptions UseJsonContext(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        JsonContext = context;
        return this;
    }

    /// <summary>
    /// Opts into async staging-capture. The interceptor writes a lightweight
    /// <c>OrionAudit_Capture_Queue</c> row in the consumer's transaction; a background
    /// dispatcher computes the diff and writes the final <see cref="AuditLog"/> row shortly
    /// after. Capture stays atomic and lossless; audit becomes eventually consistent. When
    /// this is not called the synchronous v0.4.0 capture path is used unchanged.
    /// </summary>
    public OrionAuditOptions UseAsyncCapture(Action<AsyncCaptureBuilder>? configure = null)
    {
        var builder = new AsyncCaptureBuilder();
        configure?.Invoke(builder);
        AsyncCaptureOptions = builder.Options;
        AsyncCaptureEnabled = true;
        return this;
    }
}
