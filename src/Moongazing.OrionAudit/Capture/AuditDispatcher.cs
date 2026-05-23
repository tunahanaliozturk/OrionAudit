using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moongazing.OrionAudit.Configuration;

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
    private readonly TimeProvider clock;
    private readonly ILogger<AuditDispatcher<TDbContext>> logger;

    /// <summary>Initializes a new dispatcher.</summary>
    public AuditDispatcher(
        IServiceScopeFactory scopeFactory,
        AsyncCaptureOptions options,
        SnapshotPolicy snapshotPolicy,
        TimeProvider clock,
        ILogger<AuditDispatcher<TDbContext>> logger)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.snapshotPolicy = snapshotPolicy ?? throw new ArgumentNullException(nameof(snapshotPolicy));
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
        foreach (var row in claimed)
        {
            try
            {
                var auditLog = BuildAuditLog(ctx, row);
                ctx.Add(auditLog);
                ctx.Set<AuditCaptureQueueEntry>().Remove(row);
                processed++;
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

        // Inserts (AuditLog) + deletes (queue rows) + failure updates commit together.
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        OrionAuditTelemetry.DispatchRowsProcessed.Add(processed);
        OrionAuditTelemetry.DispatchRowsDeadLettered.Add(deadLettered);
        OrionAuditTelemetry.DispatchBatchDuration.Record(sw.Elapsed.TotalMilliseconds);
        activity?.SetTag("orionaudit.dispatch.rows_processed", processed);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return processed;
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
