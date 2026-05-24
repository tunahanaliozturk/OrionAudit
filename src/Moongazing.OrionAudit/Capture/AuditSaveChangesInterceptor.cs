using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// EF Core <see cref="SaveChangesInterceptor"/> that captures Insert / Update / Delete operations
/// against audited entities, computes JSON Patch diffs, and writes <see cref="AuditLog"/> rows in
/// the same transaction.
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IServiceProvider serviceProvider;

    /// <param name="serviceProvider">
    /// The scoped service provider captured at DbContext construction by the
    /// <c>(sp, o) =&gt; o.AddInterceptors(new AuditSaveChangesInterceptor(sp))</c> wiring.
    /// </param>
    public AuditSaveChangesInterceptor(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        var ctx = eventData.Context!;
        var configuration = serviceProvider.GetRequiredService<IAuditConfiguration>();
        var clock = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        var asyncCapture = serviceProvider.GetService<AsyncCaptureOptions>();

        // State check is a struct compare; IsAudited is a FrozenDictionary lookup. Both are cheap,
        // but state-first lets us skip the dictionary lookup for entities that aren't being saved.
        var auditedEntries = ctx.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                        && configuration.IsAudited(e.Entity.GetType()))
            .ToList();

        if (auditedEntries.Count == 0)
        {
            return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
        }

        using var activity = OrionAuditTelemetry.ActivitySource.StartActivity("OrionAudit.Capture", ActivityKind.Internal);
        activity?.SetTag("orionaudit.entry_count", auditedEntries.Count);

        var stopwatch = Stopwatch.StartNew();
        var user = serviceProvider.GetService<IAuditUserResolver>()?.Resolve(serviceProvider);
        var tenantId = serviceProvider.GetService<IAuditTenantResolver>()?.Resolve(serviceProvider);
        var correlationId = AuditScope.Current ?? Activity.Current?.Id;
        var occurredOn = clock.GetUtcNow().UtcDateTime;

        if (tenantId is not null)
        {
            activity?.SetTag("orionaudit.tenant_id", tenantId);
        }
        if (user?.Type is not null)
        {
            activity?.SetTag("orionaudit.user_type", user.Type);
        }

        var snapshotPolicy = serviceProvider.GetService<SnapshotPolicy>() ?? SnapshotPolicy.Never;
        var jsonContext = serviceProvider.GetService<JsonSerializerContext>();

        // Async-capture mode: write a lightweight queue row per audited entity instead of an
        // AuditLog row. The diff and final AuditLog row are produced later by the dispatcher.
        if (asyncCapture is not null)
        {
            foreach (var entry in auditedEntries)
            {
                ctx.Add(BuildQueueEntry(entry, configuration, user, tenantId, correlationId, occurredOn, jsonContext));
            }
            OrionAuditTelemetry.EntriesWritten.Add(auditedEntries.Count);
            OrionAuditTelemetry.CaptureDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
        }

        var snapshotsTaken = 0;

        var writtenCount = 0;
        var failedCount = 0;
        foreach (var entry in auditedEntries)
        {
            var (auditLog, afterNode) = BuildAuditLog(entry, configuration, user, tenantId, correlationId, occurredOn, jsonContext);

            // Apply periodic snapshot policy on Updated rows only — Deleted / SoftDeleted already
            // populated Snapshot inside BuildAuditLog.
            if (auditLog.Error is null
                && auditLog.Action == AuditAction.Updated
                && snapshotPolicy is not SnapshotPolicy.NeverPolicy
                && afterNode is not null)
            {
                if (SnapshotPolicyEvaluator.ShouldSnapshot(ctx, snapshotPolicy, auditLog, occurredOn))
                {
                    auditLog.Snapshot = afterNode.ToJsonString();
                    snapshotsTaken++;
                }
            }

            ctx.Add(auditLog);
            ApplyCustomColumns(ctx, auditLog, entry, configuration,
                auditLog.Action, user, tenantId);
            if (auditLog.Error is null)
            {
                writtenCount++;
            }
            else
            {
                failedCount++;
            }
        }

        OrionAuditTelemetry.EntriesWritten.Add(writtenCount);
        OrionAuditTelemetry.EntriesFailed.Add(failedCount);
        OrionAuditTelemetry.SnapshotsWritten.Add(snapshotsTaken);
        OrionAuditTelemetry.CaptureDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    // Runs after ctx.Add(auditLog) in the sync path. For each registered custom column the
    // provider is invoked; the result is written to the AuditLog's shadow property. Provider
    // failures degrade to NULL on that column plus an Error annotation — never abort the save.
    private static void ApplyCustomColumns(
        DbContext ctx,
        AuditLog auditLog,
        EntityEntry entry,
        IAuditConfiguration configuration,
        AuditAction action,
        AuditUser? user,
        string? tenantId)
    {
        if (configuration.CustomColumns.Count == 0)
        {
            return;
        }
        var auditCtx = new AuditColumnContext(entry.Entity, entry, action, user, tenantId);
        foreach (var column in configuration.CustomColumns)
        {
            try
            {
                var value = column.Provider(auditCtx);
                ctx.Entry(auditLog).Property(column.Name).CurrentValue = value;
            }
#pragma warning disable CA1031 // a single bad provider must not abort the save
            catch (Exception ex)
#pragma warning restore CA1031
            {
                auditLog.Error = string.IsNullOrEmpty(auditLog.Error)
                    ? $"AddColumn '{column.Name}': {ex.Message}"
                    : auditLog.Error + $"; AddColumn '{column.Name}': {ex.Message}";
            }
        }
    }

    private static (AuditLog Log, JsonObject? AfterNode) BuildAuditLog(
        EntityEntry entry,
        IAuditConfiguration configuration,
        AuditUser? user,
        string? tenantId,
        string? correlationId,
        DateTime occurredOn,
        JsonSerializerContext? jsonContext)
    {
        var entityType = entry.Entity.GetType();
        var primaryKey = ExtractPrimaryKey(entry);
        var typeConfig = configuration.GetConfig(entityType);

        var action = entry.State switch
        {
            EntityState.Added => AuditAction.Inserted,
            EntityState.Modified => AuditAction.Updated,
            EntityState.Deleted => AuditAction.Deleted,
            _ => throw new InvalidOperationException($"Unsupported entry state {entry.State}.")
        };

        // Promote Updated → SoftDeleted when the configured boolean property flips false → true.
        if (action == AuditAction.Updated && typeConfig?.SoftDeleteProperty is { } softDeleteProp)
        {
            var property = entry.Properties.FirstOrDefault(p => p.Metadata.Name == softDeleteProp);
            if (property is not null
                && property.OriginalValue is false
                && property.CurrentValue is true)
            {
                action = AuditAction.SoftDeleted;
            }
        }

        var beforeValues = entry.State == EntityState.Added
            ? new Dictionary<string, object?>()
            : SnapshotValues(entry, useOriginal: true);
        var afterValues = entry.State == EntityState.Deleted
            ? new Dictionary<string, object?>()
            : SnapshotValues(entry, useOriginal: false);

        var auditLog = new AuditLog
        {
            EntityType = entityType.AssemblyQualifiedName!,
            EntityId = primaryKey,
            Action = action,
            OccurredOnUtc = occurredOn,
            UserId = user?.Id,
            UserDisplay = user?.DisplayName,
            UserType = user?.Type,
            TenantId = tenantId,
            CorrelationId = correlationId,
        };

        JsonObject? afterNodeForCaller = null;
        try
        {
            JsonObject beforeNode;
            JsonObject afterNode;
            if (jsonContext is not null)
            {
                beforeNode = SnapshotBuilder.Build(entityType, beforeValues, configuration, jsonContext);
                afterNode = SnapshotBuilder.Build(entityType, afterValues, configuration, jsonContext);
            }
            else
            {
                beforeNode = SnapshotBuilder.Build(entityType, beforeValues, configuration);
                afterNode = SnapshotBuilder.Build(entityType, afterValues, configuration);
            }
            auditLog.Diff = DiffEngine.Compute(beforeNode, afterNode);

            if (action is AuditAction.Deleted)
            {
                auditLog.Snapshot = beforeNode.ToJsonString();
            }
            else if (action is AuditAction.SoftDeleted)
            {
                // For soft-deletes the row still exists, so capture the post-flip state.
                auditLog.Snapshot = afterNode.ToJsonString();
            }

            // Hand the after-state node to the outer loop so it can decide whether to also stamp
            // a snapshot under the SnapshotPolicy (Updated rows only).
            afterNodeForCaller = afterNode;
        }
        catch (Exception ex)
        {
            auditLog.Diff = "[]";
            auditLog.Error = ex.ToString();
        }

        return (auditLog, afterNodeForCaller);
    }

    // Async-capture counterpart of BuildAuditLog: builds the same rule-applied before/after
    // snapshot nodes (so [Hashed]/[Redacted]/[NotAuditable] are honoured before anything is
    // persisted) but defers diff computation to the dispatcher. A SnapshotBuilder failure for
    // an unregistered type propagates and rolls back the consumer's SaveChanges — the same
    // contract the synchronous path has.
    private static AuditCaptureQueueEntry BuildQueueEntry(
        EntityEntry entry,
        IAuditConfiguration configuration,
        AuditUser? user,
        string? tenantId,
        string? correlationId,
        DateTime occurredOn,
        JsonSerializerContext? jsonContext)
    {
        var entityType = entry.Entity.GetType();
        var typeConfig = configuration.GetConfig(entityType);

        var action = entry.State switch
        {
            EntityState.Added => AuditAction.Inserted,
            EntityState.Modified => AuditAction.Updated,
            EntityState.Deleted => AuditAction.Deleted,
            _ => throw new InvalidOperationException($"Unsupported entry state {entry.State}.")
        };
        if (action == AuditAction.Updated && typeConfig?.SoftDeleteProperty is { } softDeleteProp)
        {
            var property = entry.Properties.FirstOrDefault(p => p.Metadata.Name == softDeleteProp);
            if (property is not null && property.OriginalValue is false && property.CurrentValue is true)
            {
                action = AuditAction.SoftDeleted;
            }
        }

        var beforeValues = entry.State == EntityState.Added
            ? new Dictionary<string, object?>()
            : SnapshotValues(entry, useOriginal: true);
        var afterValues = entry.State == EntityState.Deleted
            ? new Dictionary<string, object?>()
            : SnapshotValues(entry, useOriginal: false);

        JsonObject beforeNode = jsonContext is not null
            ? SnapshotBuilder.Build(entityType, beforeValues, configuration, jsonContext)
            : SnapshotBuilder.Build(entityType, beforeValues, configuration);
        JsonObject afterNode = jsonContext is not null
            ? SnapshotBuilder.Build(entityType, afterValues, configuration, jsonContext)
            : SnapshotBuilder.Build(entityType, afterValues, configuration);

        var customsJson = SerializeCustomColumns(entry, configuration, user, tenantId, action);

        return new AuditCaptureQueueEntry
        {
            EntityType = entityType.AssemblyQualifiedName!,
            EntityId = ExtractPrimaryKey(entry),
            Action = action,
            BeforeJson = beforeNode.ToJsonString(),
            AfterJson = afterNode.ToJsonString(),
            UserId = user?.Id,
            UserDisplay = user?.DisplayName,
            UserType = user?.Type,
            TenantId = tenantId,
            CorrelationId = correlationId,
            OccurredOnUtc = occurredOn,
            Attempts = 0,
            CustomColumnsJson = customsJson,
        };
    }

    // Async-mode counterpart of ApplyCustomColumns. Invokes each registered provider with the
    // audited entity in scope and serialises (name → value) into a JSON object. The dispatcher
    // deserialises and applies the values to the final AuditLog row. Provider failures are
    // swallowed — the column lands NULL on the AuditLog. (The sync path annotates Error on
    // failure; the async path can't reach the not-yet-written AuditLog from here, so this
    // path is the documented trade-off for async users.)
    private static string? SerializeCustomColumns(
        EntityEntry entry,
        IAuditConfiguration configuration,
        AuditUser? user,
        string? tenantId,
        AuditAction action)
    {
        if (configuration.CustomColumns.Count == 0)
        {
            return null;
        }
        var auditCtx = new AuditColumnContext(entry.Entity, entry, action, user, tenantId);
        var obj = new JsonObject();
        var hasAny = false;
        foreach (var column in configuration.CustomColumns)
        {
            try
            {
                var value = column.Provider(auditCtx);
                obj[column.Name] = value is null ? null : JsonValue.Create(value);
                hasAny = true;
            }
#pragma warning disable CA1031 // single bad provider must not abort the save
            catch
#pragma warning restore CA1031
            {
                // Column ends up missing from the JSON → dispatcher leaves it NULL.
            }
        }
        return hasAny ? obj.ToJsonString() : null;
    }

    private static Dictionary<string, object?> SnapshotValues(EntityEntry entry, bool useOriginal)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in entry.Properties)
        {
            if (property.Metadata.IsPrimaryKey())
            {
                continue;
            }
            dict[property.Metadata.Name] = useOriginal ? property.OriginalValue : property.CurrentValue;
        }
        return dict;
    }

    private static string ExtractPrimaryKey(EntityEntry entry)
    {
        var pk = entry.Metadata.FindPrimaryKey()
            ?? throw new OrionAuditConfigurationException(
                $"Entity '{entry.Metadata.Name}' has no primary key configured.");

        if (pk.Properties.Count == 1)
        {
            var single = pk.Properties[0];
            return entry.Property(single.Name).CurrentValue?.ToString()
                ?? throw new InvalidOperationException(
                    $"Primary key value for entity '{entry.Metadata.Name}' is null.");
        }

        var parts = new object?[pk.Properties.Count];
        for (var i = 0; i < pk.Properties.Count; i++)
        {
            parts[i] = entry.Property(pk.Properties[i].Name).CurrentValue
                ?? throw new InvalidOperationException(
                    $"Composite primary key component '{pk.Properties[i].Name}' on '{entry.Metadata.Name}' is null.");
        }
        return AuditKey.From(parts);
    }
}
