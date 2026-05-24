using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// Fluent bulk-import builder. Construct via <c>DbContext.CreateAuditImport(...)</c>;
/// call <see cref="Add{T}"/> per record then <see cref="SaveAsync"/>. <see cref="SaveAsync"/>
/// can be called multiple times to resume after a partial failure — idempotency stamps
/// matched-already rows as <c>Skipped</c>.
/// </summary>
public sealed class AuditImportBuilder
{
    private readonly DbContext context;
    private readonly AuditImportOptions options;
    private readonly IAuditConfiguration configuration;
    private readonly JsonSerializerContext? jsonContext;
    private readonly List<PendingRecord> pending = new();

    internal AuditImportBuilder(
        DbContext context,
        AuditImportOptions options,
        IAuditConfiguration configuration,
        JsonSerializerContext? jsonContext)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.jsonContext = jsonContext;
    }

    /// <summary>Adds one record to the buffer. Throws if mandatory fields are missing.</summary>
    public AuditImportBuilder Add<T>(Action<AuditImportRecord<T>> configure) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var record = new AuditImportRecord<T>(this.configuration);
        configure(record);
        record.Validate();
        pending.Add(record.ToPending());
        return this;
    }

    /// <summary>
    /// Drains the buffer to <see cref="AuditLog"/>. Always writes directly (bypasses the
    /// async-capture queue). Each call processes the records added since the last call.
    /// </summary>
    public async Task<ImportResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ImportBatch))
        {
            throw new InvalidOperationException(
                "AuditImportOptions.ImportBatch is required before SaveAsync.");
        }
        if (pending.Count == 0)
        {
            return new ImportResult(0, 0, 0);
        }

        using var activity = OrionAuditTelemetry.ActivitySource.StartActivity(
            "OrionAudit.Import", ActivityKind.Internal);
        var sw = Stopwatch.StartNew();

        var tag = options.ImportBatch!;
        var prefix = $"import:{tag}";

        var existingCorrelations = await context.Set<AuditLog>()
            .Where(a => a.CorrelationId != null && a.CorrelationId.StartsWith(prefix))
            .Select(a => a.CorrelationId!)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existing = new HashSet<string>(existingCorrelations, StringComparer.Ordinal);

        var written = 0;
        var skipped = 0;
        var deadLettered = 0;
        var batch = new List<AuditLog>(Math.Min(pending.Count, options.BatchSize));
        var batchRecords = new List<PendingRecord>(batch.Capacity);

        foreach (var record in pending)
        {
            var correlation = record.SourceId is null
                ? prefix
                : $"{prefix}#{record.SourceId}";

            if (existing.Contains(correlation))
            {
                skipped++;
                continue;
            }
            // Don't add to `existing` here — that set represents rows already in the DB before
            // this SaveAsync. Multiple in-batch records sharing a correlation (the no-SourceId
            // case) are intentionally all written; their idempotency is batch-level, not
            // per-record.

            AuditLog row;
            try
            {
                row = BuildAuditLog(record, correlation);
                written++;
            }
#pragma warning disable CA1031 // a malformed record must not abort the batch
            catch (Exception ex)
#pragma warning restore CA1031
            {
                row = new AuditLog
                {
                    EntityType = record.EntityType.AssemblyQualifiedName!,
                    EntityId = record.KeyString,
                    Action = record.Action,
                    OccurredOnUtc = record.OccurredOnUtc,
                    UserId = record.UserId,
                    UserDisplay = record.UserDisplay,
                    UserType = record.UserType,
                    TenantId = record.TenantId,
                    CorrelationId = correlation,
                    Diff = "[]",
                    Error = ex.ToString(),
                };
                deadLettered++;
            }
            batch.Add(row);
            batchRecords.Add(record);

            if (batch.Count >= options.BatchSize)
            {
                await FlushAsync(batch, batchRecords, cancellationToken).ConfigureAwait(false);
            }
        }

        if (batch.Count > 0)
        {
            await FlushAsync(batch, batchRecords, cancellationToken).ConfigureAwait(false);
        }

        pending.Clear();

        OrionAuditTelemetry.ImportRowsWritten.Add(written);
        OrionAuditTelemetry.ImportRowsSkipped.Add(skipped);
        OrionAuditTelemetry.ImportRowsDeadLettered.Add(deadLettered);
        OrionAuditTelemetry.ImportBatchDuration.Record(sw.Elapsed.TotalMilliseconds);
        activity?.SetTag("orionaudit.import.rows_written", written);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return new ImportResult(written, skipped, deadLettered);
    }

    private async Task FlushAsync(
        List<AuditLog> batch,
        List<PendingRecord> batchRecords,
        CancellationToken cancellationToken)
    {
        await context.Set<AuditLog>().AddRangeAsync(batch, cancellationToken).ConfigureAwait(false);
        // ApplyRecordCustomColumns must run after Add so EF tracks each AuditLog before we
        // touch its shadow properties.
        for (var i = 0; i < batch.Count; i++)
        {
            ApplyRecordCustomColumns(batch[i], batchRecords[i]);
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        batch.Clear();
        batchRecords.Clear();
    }

    private AuditLog BuildAuditLog(PendingRecord record, string correlation)
    {
        var beforeValues = record.Before is null
            ? new Dictionary<string, object?>()
            : ToValueDictionary(record.EntityType, record.Before);
        var afterValues = record.After is null
            ? new Dictionary<string, object?>()
            : ToValueDictionary(record.EntityType, record.After);

        var beforeNode = jsonContext is not null
            ? SnapshotBuilder.Build(record.EntityType, beforeValues, configuration, jsonContext)
            : SnapshotBuilder.Build(record.EntityType, beforeValues, configuration);
        var afterNode = jsonContext is not null
            ? SnapshotBuilder.Build(record.EntityType, afterValues, configuration, jsonContext)
            : SnapshotBuilder.Build(record.EntityType, afterValues, configuration);

        var diff = DiffEngine.Compute(beforeNode, afterNode);

        var log = new AuditLog
        {
            EntityType = record.EntityType.AssemblyQualifiedName!,
            EntityId = record.KeyString,
            Action = record.Action,
            OccurredOnUtc = record.OccurredOnUtc,
            UserId = record.UserId,
            UserDisplay = record.UserDisplay,
            UserType = record.UserType,
            TenantId = record.TenantId,
            CorrelationId = correlation,
            Diff = diff,
        };

        if (record.Action == AuditAction.Deleted)
        {
            log.Snapshot = beforeNode.ToJsonString();
        }
        else if (record.Action == AuditAction.SoftDeleted)
        {
            log.Snapshot = afterNode.ToJsonString();
        }
        return log;
    }

    private void ApplyRecordCustomColumns(AuditLog row, PendingRecord record)
    {
        if (record.CustomColumns is null || record.CustomColumns.Count == 0)
        {
            return;
        }
        foreach (var (name, value) in record.CustomColumns)
        {
            context.Entry(row).Property(name).CurrentValue = value;
        }
    }

    private Dictionary<string, object?> ToValueDictionary(Type entityType, object entity)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        var pkNames = GetPrimaryKeyNames(entityType);
        foreach (var p in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || pkNames.Contains(p.Name))
            {
                continue;
            }
            dict[p.Name] = p.GetValue(entity);
        }
        return dict;
    }

    private HashSet<string> GetPrimaryKeyNames(Type entityType)
    {
        var et = context.Model.FindEntityType(entityType);
        var pk = et?.FindPrimaryKey();
        if (pk is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        return pk.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    internal sealed class PendingRecord
    {
        public Type EntityType { get; init; } = default!;
        public string KeyString { get; init; } = default!;
        public AuditAction Action { get; init; }
        public object? Before { get; init; }
        public object? After { get; init; }
        public string? UserId { get; init; }
        public string? UserDisplay { get; init; }
        public string? UserType { get; init; }
        public string? TenantId { get; init; }
        public string? SourceId { get; init; }
        public DateTime OccurredOnUtc { get; init; }
        public Dictionary<string, object?>? CustomColumns { get; init; }
    }
}

/// <summary>Builder for a single import record passed to <c>Add&lt;T&gt;</c>.</summary>
public sealed class AuditImportRecord<T> where T : class
{
    private readonly IAuditConfiguration configuration;
    private string? keyString;
    private AuditAction? action;
    private object? before;
    private object? after;
    private string? userId;
    private string? userDisplay;
    private string? userType;
    private string? tenantId;
    private string? sourceId;
    private DateTime occurredOnUtc = DateTime.UtcNow;
    private Dictionary<string, object?>? customColumns;

    internal AuditImportRecord(IAuditConfiguration configuration)
        => this.configuration = configuration;

    /// <summary>Primary key — converted to string via <c>ToString()</c>.</summary>
    public AuditImportRecord<T> Key(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        keyString = key.ToString() ?? throw new ArgumentException("Key.ToString() returned null.", nameof(key));
        return this;
    }

    /// <summary>Composite key — passes through <see cref="AuditKey.From"/>.</summary>
    public AuditImportRecord<T> Key(params object?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        keyString = AuditKey.From(parts);
        return this;
    }

    /// <summary>Sets the action this record records.</summary>
    public AuditImportRecord<T> Action(AuditAction value) { action = value; return this; }

    /// <summary>Sets the before-state entity (null → Insert).</summary>
    public AuditImportRecord<T> Before(T? state) { before = state; return this; }

    /// <summary>Sets the after-state entity (null → Delete).</summary>
    public AuditImportRecord<T> After(T? state) { after = state; return this; }

    /// <summary>Sets user attribution.</summary>
    public AuditImportRecord<T> By(string? id, string? display = null, string? type = null)
        { userId = id; userDisplay = display; userType = type; return this; }

    /// <summary>Sets the tenant id.</summary>
    public AuditImportRecord<T> Tenant(string? value) { tenantId = value; return this; }

    /// <summary>Stable per-record id used for idempotency (combined with ImportBatch).</summary>
    public AuditImportRecord<T> SourceId(object? value) { sourceId = value?.ToString(); return this; }

    /// <summary>Sets the UTC timestamp of the originating change.</summary>
    public AuditImportRecord<T> At(DateTime utc) { occurredOnUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc); return this; }

    /// <summary>Sets a previously-registered custom column's value for this record.</summary>
    public AuditImportRecord<T> WithColumn(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!configuration.CustomColumns.Any(c => string.Equals(c.Name, name, StringComparison.Ordinal)))
        {
            throw new OrionAuditConfigurationException(
                $"AuditImport.WithColumn '{name}': column is not registered via AddColumn.");
        }
        customColumns ??= new Dictionary<string, object?>(StringComparer.Ordinal);
        customColumns[name] = value;
        return this;
    }

    internal void Validate()
    {
        if (keyString is null)
        {
            throw new InvalidOperationException(
                $"AuditImport record for '{typeof(T).Name}': Key(...) is required.");
        }
        if (action is null)
        {
            throw new InvalidOperationException(
                $"AuditImport record for '{typeof(T).Name}': Action(...) is required.");
        }
    }

    internal AuditImportBuilder.PendingRecord ToPending() => new()
    {
        EntityType = typeof(T),
        KeyString = keyString!,
        Action = action!.Value,
        Before = before,
        After = after,
        UserId = userId,
        UserDisplay = userDisplay,
        UserType = userType,
        TenantId = tenantId,
        SourceId = sourceId,
        OccurredOnUtc = occurredOnUtc,
        CustomColumns = customColumns,
    };
}
