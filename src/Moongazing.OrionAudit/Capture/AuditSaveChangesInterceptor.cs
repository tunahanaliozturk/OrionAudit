using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.Orion.Abstractions.Observers;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Publishing;

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

    // v0.7.26: resolve and invoke the optional capture observer. Resolved per-call from
    // the scoped provider (same pattern as IAuditEventPublisher). null and the Null
    // implementation are both treated as 'no observer'; observer faults are swallowed so
    // an observability outage cannot abort the consumer's transaction.
    //
    // v0.11.1 convergence: the fault-safe invocation is now SafeObserverInvoker.Resolve from
    // Orion.Abstractions - the shared primitive the v0.7.26 fix (resolve INSIDE the swallow
    // guard, so a registered observer whose constructor / DI dependency throws cannot abort
    // SaveChangesAsync) was generalised into. The NullAuditCaptureObserver short-circuit is
    // mapped into the resolve factory (return null for 'no observer'), so the behavior is
    // identical: null observer and NullAuditCaptureObserver are both no-ops, and every other
    // fault is swallowed.
    private void NotifyCaptureObserver(int auditedEntityCount, bool isAsyncCapture)
    {
        SafeObserverInvoker.Resolve(
            resolve: () =>
            {
                var observer = serviceProvider.GetService<IAuditCaptureObserver>();
                return observer is NullAuditCaptureObserver ? null : observer;
            },
            action: observer => observer.OnCaptured(auditedEntityCount, isAsyncCapture));
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
        // Presence of AuditHashChainOptions is the opt-in switch for tamper-evidence (same gate
        // pattern as AsyncCaptureOptions). null ⇒ chaining off ⇒ hash columns stay null.
        var hashChain = serviceProvider.GetService<Integrity.AuditHashChainOptions>();

        // Async-capture mode: write a lightweight queue row per audited entity instead of an
        // AuditLog row. The diff and final AuditLog row are produced later by the dispatcher.
        // The publisher is intentionally NOT called here in async mode — the dispatcher fires
        // it from its own transaction once the AuditLog row exists (see AuditDispatcher).
        if (asyncCapture is not null)
        {
            foreach (var entry in auditedEntries)
            {
                ctx.Add(BuildQueueEntry(entry, configuration, user, tenantId, correlationId, occurredOn, jsonContext));
            }
            OrionAuditTelemetry.EntriesWritten.Add(auditedEntries.Count);
            // v0.7.14: distribution of audited rows per save; complements the steady-state
            // EntriesWritten counter by exposing the tail (bulk imports, batch-mode saves).
            OrionAuditTelemetry.CaptureEntriesPerSave.Record(auditedEntries.Count);
            OrionAuditTelemetry.CaptureDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
            // v0.7.26: notify the optional capture observer (async-capture path).
            NotifyCaptureObserver(auditedEntries.Count, isAsyncCapture: true);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
        }

        var snapshotsTaken = 0;

        var writtenCount = 0;
        var failedCount = 0;
        // Built per-row so we can hand the events to IAuditEventPublisher after the loop. Only
        // sized when a publisher is actually wired (NullAuditEventPublisher skips the allocation).
        var publisher = serviceProvider.GetService<IAuditEventPublisher>();
        var publishEvents = publisher is null or NullAuditEventPublisher
            ? null
            : new List<AuditLogEvent>(auditedEntries.Count);
        // Only materialised when hash-chaining is on; holds the rows to stamp after the loop.
        var hashableRows = hashChain is null ? null : new List<AuditLog>(auditedEntries.Count);

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

            hashableRows?.Add(auditLog);
            publishEvents?.Add(ToEvent(auditLog));
        }

        // Tamper-evidence: stamp the hash chain across the rows just added, chaining each onto its
        // entity stream's persisted anchor (which is locked + advanced in this same transaction).
        // Done after the build loop (so every row's content is final, including any SnapshotPolicy
        // snapshot stamped above and the custom-column shadow values applied by ApplyCustomColumns)
        // and before publish/SaveChanges so the hashes commit atomically with the rows. Every captured
        // row is chained, including ones that recorded a diff Error, so an error row cannot silently
        // create a gap.
        if (hashChain is not null && hashableRows is { Count: > 0 })
        {
            var keyProvider = serviceProvider.GetRequiredService<Integrity.IAuditChainKeyProvider>();
            await Integrity.EfCoreAuditHashChainWriter
                .StampAsync(ctx, hashableRows, hashChain.Scope, keyProvider,
                    configuration.CustomColumns, cancellationToken)
                .ConfigureAwait(false);
        }

        OrionAuditTelemetry.EntriesWritten.Add(writtenCount);
        OrionAuditTelemetry.EntriesFailed.Add(failedCount);
        OrionAuditTelemetry.SnapshotsWritten.Add(snapshotsTaken);
        // v0.7.14: record the TOTAL audited entry count (written + failed), not just
        // writtenCount. A save where every audit row failed (e.g. custom column provider
        // threw, diff serialization choked) still produced audit entries; recording 0
        // would pollute the histogram p50 with a meaningless "this save audited nothing"
        // sample. The capture-shape distribution is the goal, not the success ratio.
        OrionAuditTelemetry.CaptureEntriesPerSave.Record(writtenCount + failedCount);
        OrionAuditTelemetry.CaptureDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        // v0.7.26: notify the optional capture observer (inline path). Total audited
        // count (written + failed) so observers see the full capture surface.
        NotifyCaptureObserver(writtenCount + failedCount, isAsyncCapture: false);

        // Publish BEFORE SaveChanges so a publisher exception aborts the consumer transaction.
        // A NullAuditEventPublisher has nothing to publish and was filtered out above.
        // Strict outbox-style semantics (publish-after-durable-commit with retry) are tracked
        // in the v0.7.x roadmap; until then consumers MUST treat AuditLogEvent as an
        // at-least-once notification and dedupe on AuditLogId.
        if (publisher is not null && publishEvents is { Count: > 0 })
        {
            await publisher.PublishAsync(publishEvents, cancellationToken).ConfigureAwait(false);
        }

        // Status is set only once everything (capture + publish) has succeeded so a publisher
        // exception is correctly reflected as a failure span.
        activity?.SetStatus(ActivityStatusCode.Ok);

        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    // Mirrors AuditLog to AuditLogEvent. Centralised so the dispatcher's call-site projects the
    // same shape. DateTimeOffset is constructed from the UTC DateTime + TimeSpan.Zero so the
    // wire shape is timezone-explicit even though AuditLog stores UTC DateTime.
    internal static AuditLogEvent ToEvent(AuditLog log) => new(
        AuditLogId: log.Id,
        EntityType: log.EntityType,
        EntityKey: log.EntityId,
        Action: log.Action.ToString(),
        At: new DateTimeOffset(log.OccurredOnUtc, TimeSpan.Zero),
        TenantId: log.TenantId,
        UserId: log.UserId,
        CorrelationId: log.CorrelationId,
        Diff: string.Equals(log.Diff, "[]", StringComparison.Ordinal) ? null : log.Diff);

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

        // TPH / polymorphic capture: when the type config declares a base type, stamp its
        // FullName on EntityBaseType so a future inheritance-aware AuditFor<TBase>() (v0.7.2)
        // can return the full hierarchy. Stays null when no base type is configured, which
        // preserves the v0.7.0 schema shape for existing consumers.
        var entityBaseType = configuration.GetConfig(entityType)?.BaseType?.FullName;

        var auditLog = new AuditLog
        {
            EntityType = entityType.AssemblyQualifiedName!,
            EntityBaseType = entityBaseType,
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
            EntityBaseType = typeConfig?.BaseType?.FullName,
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
