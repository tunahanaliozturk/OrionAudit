using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Publishing;

namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// The async-capture worker. Claims a batch of <see cref="AuditCaptureQueueEntry"/> rows,
/// computes each row's diff, writes the resulting <see cref="AuditLog"/> rows, and deletes the
/// claimed queue rows — the inserts and deletes commit in one transaction so dispatch is
/// exactly-once. Used by the dispatcher hosted service and exposed as
/// <see cref="IAuditDispatcher"/>.
/// </summary>
public sealed partial class AuditDispatcher<TDbContext> : IAuditDispatcher
    where TDbContext : DbContext
{
    [LoggerMessage(EventId = 10, Level = LogLevel.Error,
        Message = "OrionAudit dispatch failed for queue row {QueueRowId} (attempt {Attempt}).")]
    private partial void LogRowFailed(long queueRowId, int attempt, Exception ex);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error,
        Message = "OrionAudit queue row {QueueRowId} dead-lettered after {Attempts} attempts.")]
    private partial void LogRowDeadLettered(long queueRowId, int attempts);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly AsyncCaptureOptions options;
    private readonly SnapshotPolicy snapshotPolicy;
    private readonly IAuditConfiguration configuration;
    private readonly TimeProvider clock;
    private readonly ILogger<AuditDispatcher<TDbContext>> logger;

    /// <summary>Initializes a new dispatcher.</summary>
    public AuditDispatcher(
        IServiceScopeFactory scopeFactory,
        AsyncCaptureOptions options,
        SnapshotPolicy snapshotPolicy,
        IAuditConfiguration configuration,
        TimeProvider clock,
        ILogger<AuditDispatcher<TDbContext>> logger)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.snapshotPolicy = snapshotPolicy ?? throw new ArgumentNullException(nameof(snapshotPolicy));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<int> FlushPendingAsync(CancellationToken cancellationToken = default)
    {
        var total = 0;
        int processed;
        do
        {
            processed = await DispatchOnceAsync(cancellationToken).ConfigureAwait(false);
            total += processed;
        }
        while (processed > 0);
        return total;
    }

    /// <inheritdoc />
    public async Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return await ctx.Set<AuditCaptureQueueEntry>()
            .CountAsync(q => q.Error == null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Claims and processes a single batch. Returns the number of queue rows successfully
    /// turned into <see cref="AuditLog"/> rows in this cycle (0 when the queue is empty).
    /// </summary>
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        using var activity = OrionAuditTelemetry.ActivitySource.StartActivity(
            "OrionAudit.Dispatch", ActivityKind.Internal);
        var sw = Stopwatch.StartNew();

        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TDbContext>();

        var claimToken = Guid.NewGuid().ToString("N");
        var staleBefore = clock.GetUtcNow().UtcDateTime - options.ClaimLease;

        // Atomic claim: a single UPDATE over the next BatchSize dispatchable rows.
        await ctx.Set<AuditCaptureQueueEntry>()
            .Where(q => q.Error == null && (q.ClaimToken == null || q.ClaimedUtc < staleBefore))
            .OrderBy(q => q.Id)
            .Take(options.BatchSize)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.ClaimToken, claimToken)
                .SetProperty(q => q.ClaimedUtc, clock.GetUtcNow().UtcDateTime), cancellationToken)
            .ConfigureAwait(false);

        var claimed = await ctx.Set<AuditCaptureQueueEntry>()
            .Where(q => q.ClaimToken == claimToken)
            .OrderBy(q => q.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (claimed.Count == 0)
        {
            return 0;
        }

        var processed = 0;
        var deadLettered = 0;
        var publisher = scope.ServiceProvider.GetService<IAuditEventPublisher>();
        var publishEvents = publisher is null or NullAuditEventPublisher
            ? null
            : new List<AuditLogEvent>(claimed.Count);

        foreach (var row in claimed)
        {
            try
            {
                var auditLog = BuildAuditLog(ctx, row);
                ctx.Add(auditLog);
                ApplyCustomColumnsFromQueue(ctx, auditLog, row);
                ctx.Set<AuditCaptureQueueEntry>().Remove(row);
                processed++;
                publishEvents?.Add(AuditSaveChangesInterceptor.ToEvent(auditLog));
            }
#pragma warning disable CA1031 // a single bad row must not abort the batch
            catch (Exception ex)
#pragma warning restore CA1031
            {
                row.Attempts++;
                row.ClaimToken = null;
                row.ClaimedUtc = null;
                LogRowFailed(row.Id, row.Attempts, ex);
                if (row.Attempts >= options.MaxAttempts)
                {
                    row.Error = ex.ToString();
                    deadLettered++;
                    LogRowDeadLettered(row.Id, row.Attempts);
                }
            }
        }

        // Publish BEFORE the dispatcher's SaveChanges so a publisher exception aborts the same
        // transaction that holds the AuditLog insert + queue-row delete. Keeps the v0.5 dispatch
        // contract: either the AuditLog row exists AND the publisher was called, or neither.
        //
        // Edge case: if PublishAsync succeeds and SaveChanges later fails (rare commit failures
        // such as network partition mid-commit), downstream may observe an event whose AuditLog
        // row was never persisted. The queue row remains and the next dispatch cycle generates a
        // new AuditLog Guid and re-publishes. Consumers MUST treat AuditLogEvent as an
        // at-least-once notification and reconcile against the AuditLog table when authoritative
        // state matters. Strict transactional outbox semantics are tracked in the v0.7.x roadmap.
        if (publisher is not null && publishEvents is { Count: > 0 })
        {
            await publisher.PublishAsync(publishEvents, cancellationToken).ConfigureAwait(false);
        }

        // Inserts (AuditLog) + deletes (queue rows) + failure updates commit together.
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        OrionAuditTelemetry.DispatchRowsProcessed.Add(processed);
        OrionAuditTelemetry.DispatchRowsDeadLettered.Add(deadLettered);
        OrionAuditTelemetry.DispatchBatchDuration.Record(sw.Elapsed.TotalMilliseconds);
        OrionAuditTelemetry.SetQueueDepth(await ctx.Set<AuditCaptureQueueEntry>()
            .CountAsync(q => q.Error == null, cancellationToken).ConfigureAwait(false));
        activity?.SetTag("orionaudit.dispatch.rows_processed", processed);
        // Status set last so an exception above (publish or commit) leaves the span as failed.
        activity?.SetStatus(ActivityStatusCode.Ok);
        return processed;
    }

    // Deserialises the queue row's CustomColumnsJson and writes each registered column's value
    // to the AuditLog's shadow property. Names registered after the queue row was written are
    // simply absent from the JSON and stay NULL; names present in the JSON but no longer
    // registered are ignored (forward-compatible drift).
    private void ApplyCustomColumnsFromQueue(TDbContext ctx, AuditLog auditLog, AuditCaptureQueueEntry row)
    {
        if (configuration.CustomColumns.Count == 0 || string.IsNullOrEmpty(row.CustomColumnsJson))
        {
            return;
        }
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(row.CustomColumnsJson);
        }
        catch (JsonException)
        {
            return;
        }
        if (node is not JsonObject customs)
        {
            return;
        }
        foreach (var column in configuration.CustomColumns)
        {
            if (customs[column.Name] is not JsonValue v)
            {
                continue;
            }
            try
            {
                var clr = v.Deserialize(column.ClrType, JsonSerializerOptions.Default);
                ctx.Entry(auditLog).Property(column.Name).CurrentValue = clr;
            }
#pragma warning disable CA1031 // a malformed value must not abort the batch
            catch
#pragma warning restore CA1031
            {
                auditLog.Error = string.IsNullOrEmpty(auditLog.Error)
                    ? $"AddColumn '{column.Name}': dispatch deserialize failed"
                    : auditLog.Error + $"; AddColumn '{column.Name}': dispatch deserialize failed";
            }
        }
    }

    private AuditLog BuildAuditLog(TDbContext ctx, AuditCaptureQueueEntry row)
    {
        var before = JsonNode.Parse(row.BeforeJson)!.AsObject();
        var after = JsonNode.Parse(row.AfterJson)!.AsObject();

        var auditLog = new AuditLog
        {
            EntityType = row.EntityType,
            EntityId = row.EntityId,
            Action = row.Action,
            OccurredOnUtc = row.OccurredOnUtc,
            UserId = row.UserId,
            UserDisplay = row.UserDisplay,
            UserType = row.UserType,
            TenantId = row.TenantId,
            CorrelationId = row.CorrelationId,
            Diff = DiffEngine.Compute(before, after),
        };

        if (row.Action is AuditAction.Deleted)
        {
            auditLog.Snapshot = row.BeforeJson;
        }
        else if (row.Action is AuditAction.SoftDeleted)
        {
            auditLog.Snapshot = row.AfterJson;
        }
        else if (row.Action == AuditAction.Updated
                 && snapshotPolicy is not SnapshotPolicy.NeverPolicy
                 && SnapshotPolicyEvaluator.ShouldSnapshot(ctx, snapshotPolicy, auditLog, row.OccurredOnUtc))
        {
            auditLog.Snapshot = row.AfterJson;
        }

        return auditLog;
    }
}
