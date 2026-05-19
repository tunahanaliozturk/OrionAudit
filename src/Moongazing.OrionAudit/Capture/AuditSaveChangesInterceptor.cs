using System.Diagnostics;
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

        var writtenCount = 0;
        var failedCount = 0;
        foreach (var entry in auditedEntries)
        {
            var auditLog = BuildAuditLog(entry, configuration, user, tenantId, correlationId, occurredOn);
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
        OrionAuditTelemetry.CaptureDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    private static AuditLog BuildAuditLog(
        EntityEntry entry,
        IAuditConfiguration configuration,
        AuditUser? user,
        string? tenantId,
        string? correlationId,
        DateTime occurredOn)
    {
        var entityType = entry.Entity.GetType();
        var primaryKey = ExtractPrimaryKey(entry);

        var action = entry.State switch
        {
            EntityState.Added => AuditAction.Inserted,
            EntityState.Modified => AuditAction.Updated,
            EntityState.Deleted => AuditAction.Deleted,
            _ => throw new InvalidOperationException($"Unsupported entry state {entry.State}.")
        };

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

        try
        {
            var beforeNode = SnapshotBuilder.Build(entityType, beforeValues, configuration);
            var afterNode = SnapshotBuilder.Build(entityType, afterValues, configuration);
            auditLog.Diff = DiffEngine.Compute(beforeNode, afterNode);

            if (action == AuditAction.Deleted)
            {
                auditLog.Snapshot = beforeNode.ToJsonString();
            }
        }
        catch (Exception ex)
        {
            auditLog.Diff = "[]";
            auditLog.Error = ex.ToString();
        }

        return auditLog;
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
