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
                if (ShouldSnapshot(ctx, snapshotPolicy, auditLog, occurredOn))
                {
                    auditLog.Snapshot = afterNode.ToJsonString();
                    snapshotsTaken++;
                }
            }

            ctx.Add(auditLog);
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

    private static bool ShouldSnapshot(DbContext ctx, SnapshotPolicy policy, AuditLog row, DateTime occurredOn)
    {
        var cursor = ctx.Set<SnapshotCursor>().Find(row.EntityType, row.EntityId, row.TenantId ?? string.Empty);
        if (cursor is null)
        {
            cursor = new SnapshotCursor
            {
                EntityType = row.EntityType,
                EntityId = row.EntityId,
                TenantId = row.TenantId ?? string.Empty,
                UpdatesSinceLast = 0,
                LastSnapshotUtc = null,
            };
            ctx.Add(cursor);
        }

        cursor.UpdatesSinceLast++;
        var shouldSnapshot = policy switch
        {
            SnapshotPolicy.EveryNthPolicy n => cursor.UpdatesSinceLast >= n.Updates,
            SnapshotPolicy.EveryDurationPolicy d =>
                cursor.LastSnapshotUtc is null
                || (occurredOn - cursor.LastSnapshotUtc.Value) >= d.Elapsed,
            _ => false,
        };

        if (shouldSnapshot)
        {
            cursor.UpdatesSinceLast = 0;
            cursor.LastSnapshotUtc = occurredOn;
        }
        return shouldSnapshot;
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
