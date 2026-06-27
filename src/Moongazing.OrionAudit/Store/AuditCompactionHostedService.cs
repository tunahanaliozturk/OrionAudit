using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.Store;

/// <summary>
/// Background sweep that folds long per-entity audit histories into snapshot rows on a schedule, so
/// operators get the storage and reconstruction-cost benefit of <see cref="IAuditHistoryStore.CompactAsync"/>
/// without invoking it by hand. Each cycle discovers the streams whose row count exceeds
/// <see cref="CompactionSweepOptions.MinRowsBeforeCompaction"/>, folds at most
/// <see cref="CompactionSweepOptions.MaxStreamsPerSweep"/> of them, and bounds each cycle's wall-clock
/// by <see cref="CompactionSweepOptions.MaxSweepDuration"/> when set. Cancellation-aware: an in-flight
/// cycle stops promptly on shutdown.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hash-chain safety.</b> Snapshot compaction rewrites the boundary row's content (its
/// <see cref="AuditLog.Diff"/> / <see cref="AuditLog.Snapshot"/> / <see cref="AuditLog.Action"/>) and
/// deletes the older folded rows. With tamper-evident hash-chaining enabled (v0.9.0
/// <c>o.UseHashChain()</c>), both of those are exactly what the chain is designed to detect: a
/// rewritten row fails its keyed MAC (<see cref="AuditChainBreakReason.ContentMismatch"/>) and removed
/// rows make the stream disagree with its persisted <see cref="AuditChainAnchor"/>
/// (<see cref="AuditChainBreakReason.Truncated"/>). Compaction and an unforgeable chain are therefore
/// mutually exclusive on the same stream. So when the chain is enabled this sweep <em>skips every
/// stream that carries any hashed row</em> and only compacts unchained streams (for example, a prefix
/// captured before the chain was switched on). This keeps the chain verifiable; operators who want
/// both bounded history and tamper-evidence should compact a stream before chaining it, not after.
/// </para>
/// <para>
/// <b>Concurrency with live writes.</b> Per-stream compaction runs through
/// <see cref="EfCoreAuditHistoryStore.CompactAsync"/>, which loads the stream, mutates the boundary
/// row, removes the folded rows, and commits in a single <c>SaveChanges</c> transaction. A concurrent
/// capture appends a NEW row (a later timestamp) that the in-flight fold never selected, so the append
/// and the fold do not contend for the same rows; the appended row simply joins the retained tail.
/// </para>
/// </remarks>
public sealed partial class AuditCompactionHostedService<TDbContext> : BackgroundService
    where TDbContext : DbContext
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "OrionAudit background compaction sweep failed; will retry on the next interval.")]
    private partial void LogSweepFailed(Exception ex);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly CompactionSweepOptions options;
    private readonly TimeProvider clock;
    private readonly ILogger<AuditCompactionHostedService<TDbContext>> logger;
    private readonly bool hashChainEnabled;

    /// <summary>
    /// Initializes the background compaction service. <paramref name="hashChainOptions"/> is optional:
    /// when non-null (the consumer called <c>o.UseHashChain()</c>) the sweep skips chained streams to
    /// keep the tamper-evident chain verifiable.
    /// </summary>
    public AuditCompactionHostedService(
        IServiceScopeFactory scopeFactory,
        CompactionSweepOptions options,
        TimeProvider clock,
        ILogger<AuditCompactionHostedService<TDbContext>> logger,
        AuditHashChainOptions? hashChainOptions = null)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        hashChainEnabled = hashChainOptions is not null;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.SweepInterval, clock);
        do
        {
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // background loop must swallow unexpected failures and keep ticking
            catch (Exception ex)
#pragma warning restore CA1031
            {
                OrionAuditTelemetry.RecordCompactionError(ex.GetType().Name);
                LogSweepFailed(ex);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Runs a single compaction sweep cycle and returns the total number of rows folded away across
    /// every stream it touched. Exposed for tests and operator-triggered runs.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        using var activity = OrionAuditTelemetry.ActivitySource.StartActivity(
            "OrionAudit.Compaction.Sweep", ActivityKind.Internal);

        var sw = Stopwatch.StartNew();
        OrionAuditTelemetry.CompactionCycles.Add(1);

        var deadlineUtc = options.MaxSweepDuration is { } budget
            ? clock.GetUtcNow().UtcDateTime + budget
            : (DateTime?)null;

        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IAuditHistoryStore>();

        var candidates = await FindCandidatesAsync(ctx, cancellationToken).ConfigureAwait(false);

        var rowsFolded = 0;
        var streamsCompacted = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadlineUtc is { } d && clock.GetUtcNow().UtcDateTime >= d)
            {
                break;
            }

            var result = await store.CompactAsync(
                new AuditCompactionRequest
                {
                    EntityType = candidate.EntityType,
                    EntityId = candidate.EntityId,
                    RetainTail = options.RetainTail,
                    // The candidate's tenant is the canonical "" for the no-tenant stream; pass null in
                    // that case so CompactAsync compacts the no-tenant rows (its filter treats null as
                    // "do not constrain tenant", and an empty-string tenant id never matches a row whose
                    // TenantId column is null). A concrete tenant is passed through unchanged.
                    TenantId = candidate.TenantId.Length == 0 ? null : candidate.TenantId,
                },
                cancellationToken).ConfigureAwait(false);

            if (result.SnapshotWritten)
            {
                streamsCompacted++;
                rowsFolded += result.RowsRemoved;
            }
        }

        OrionAuditTelemetry.CompactionStreamsCompacted.Add(streamsCompacted);
        OrionAuditTelemetry.CompactionRowsFolded.Add(rowsFolded);
        OrionAuditTelemetry.CompactionSweepDuration.Record(sw.Elapsed.TotalMilliseconds);
        activity?.SetTag("orionaudit.compaction.streams_compacted", streamsCompacted);
        activity?.SetTag("orionaudit.compaction.rows_folded", rowsFolded);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return rowsFolded;
    }

    // Discovers the streams worth folding this cycle: grouped by (EntityType, EntityId, TenantId),
    // having strictly more than MinRowsBeforeCompaction rows, ordered deterministically (most rows
    // first, then by key) so the cap picks the highest-value streams and the selection is stable
    // across runs, capped at MaxStreamsPerSweep. When the hash chain is enabled, a stream that carries
    // ANY hashed row is excluded so compaction never rewrites/removes a chained row (see the class
    // remarks): the group's row count is computed only over its unchained (EntryHash == null) rows, and
    // a group with any hashed row is dropped entirely.
    private async Task<List<StreamKey>> FindCandidatesAsync(TDbContext ctx, CancellationToken ct)
    {
        var min = options.MinRowsBeforeCompaction;
        IQueryable<AuditLog> source = ctx.Set<AuditLog>().AsNoTracking();

        if (hashChainEnabled)
        {
            // Group with a per-group flag for "has any hashed row". Drop those groups; only fold streams
            // that are entirely unchained. Computed server-side so a chained table is not materialized.
            var grouped = await source
                .GroupBy(a => new { a.EntityType, a.EntityId, a.TenantId })
                .Select(g => new
                {
                    g.Key.EntityType,
                    g.Key.EntityId,
                    g.Key.TenantId,
                    Total = g.Count(),
                    HasHashed = g.Any(a => a.EntryHash != null),
                })
                .Where(g => !g.HasHashed && g.Total > min)
                .OrderByDescending(g => g.Total)
                .ThenBy(g => g.EntityType)
                .ThenBy(g => g.EntityId)
                .Take(options.MaxStreamsPerSweep)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return grouped
                .Select(g => new StreamKey(g.EntityType, g.EntityId, g.TenantId ?? string.Empty))
                .ToList();
        }

        var groups = await source
            .GroupBy(a => new { a.EntityType, a.EntityId, a.TenantId })
            .Select(g => new { g.Key.EntityType, g.Key.EntityId, g.Key.TenantId, Total = g.Count() })
            .Where(g => g.Total > min)
            .OrderByDescending(g => g.Total)
            .ThenBy(g => g.EntityType)
            .ThenBy(g => g.EntityId)
            .Take(options.MaxStreamsPerSweep)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return groups
            .Select(g => new StreamKey(g.EntityType, g.EntityId, g.TenantId ?? string.Empty))
            .ToList();
    }

    private readonly record struct StreamKey(string EntityType, string EntityId, string TenantId);
}
