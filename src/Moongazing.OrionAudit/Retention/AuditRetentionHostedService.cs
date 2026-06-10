using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Retention;

/// <summary>
/// Background sweep that hard-deletes audit rows that fall outside the configured
/// <see cref="RetentionPolicy"/>. Bounded by <see cref="RetentionSweepOptions.MaxRowsPerSweep"/>
/// per cycle so each transaction stays short; the next cycle picks up the rest.
/// </summary>
public sealed partial class AuditRetentionHostedService<TDbContext> : BackgroundService
    where TDbContext : DbContext
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "OrionAudit retention sweep failed; will retry on the next interval.")]
    private partial void LogSweepFailed(Exception ex);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly RetentionPolicy policy;
    private readonly RetentionSweepOptions options;
    private readonly TimeProvider clock;
    private readonly ILogger<AuditRetentionHostedService<TDbContext>> logger;
    private readonly IAuditArchiver archiver;

    /// <summary>
    /// v0.7.7 source-compatible 5-arg ctor. Defaults the archiver to
    /// <see cref="DeleteAuditArchiver"/> so existing call sites compiled against v0.7.7
    /// keep their straight-delete behaviour. ABI break note: the v0.7.7 5-arg ctor is
    /// retained as an explicit overload that chains to the 6-arg one with archiver =
    /// null, so compiled v0.7.7 callers continue to resolve their original signature
    /// without MissingMethodException.
    /// </summary>
    public AuditRetentionHostedService(
        IServiceScopeFactory scopeFactory,
        RetentionPolicy policy,
        RetentionSweepOptions options,
        TimeProvider clock,
        ILogger<AuditRetentionHostedService<TDbContext>> logger)
        : this(scopeFactory, policy, options, clock, logger, archiver: null)
    {
    }

    /// <summary>
    /// v0.7.8 ctor with optional <see cref="IAuditArchiver"/>. Consumers register a
    /// custom archiver (e.g. <see cref="CopyToTableAuditArchiver{TArchiveRow}"/>) to ship
    /// expiring rows to a cold store before deletion. Null defaults to
    /// <see cref="DeleteAuditArchiver"/>.
    /// </summary>
    public AuditRetentionHostedService(
        IServiceScopeFactory scopeFactory,
        RetentionPolicy policy,
        RetentionSweepOptions options,
        TimeProvider clock,
        ILogger<AuditRetentionHostedService<TDbContext>> logger,
        IAuditArchiver? archiver)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.archiver = archiver ?? new DeleteAuditArchiver();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (policy is RetentionPolicy.NonePolicy)
        {
            return;
        }

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
#pragma warning disable CA1031 // background loop should swallow unexpected failures, keep ticking
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSweepFailed(ex);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Runs a single sweep cycle. Exposed for tests and operator-triggered runs.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        using var activity = OrionAuditTelemetry.ActivitySource.StartActivity(
            "OrionAudit.Retention.Sweep", ActivityKind.Internal);

        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var deleted = policy switch
        {
            RetentionPolicy.RetainForPolicy r => await SweepAgeAsync(ctx, r.Age, cancellationToken).ConfigureAwait(false),
            RetentionPolicy.RetainCountPolicy r => await SweepCountAsync(ctx, r.Rows, cancellationToken).ConfigureAwait(false),
            _ => 0,
        };

        activity?.SetTag("orionaudit.retention.rows_deleted", deleted);
        OrionAuditTelemetry.RetentionRowsDeleted.Add(deleted);
        OrionAuditTelemetry.RetentionSweepDuration.Record(sw.Elapsed.TotalMilliseconds);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return deleted;
    }

    private async Task<int> SweepAgeAsync(TDbContext ctx, TimeSpan age, CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - age;
        // When the default DeleteAuditArchiver is in effect, run the v0.7.7 fast path
        // (single ExecuteDelete) without materialising the rows. A custom archiver,
        // however, NEEDS the row data so it can copy it to the archive table - in that
        // case we read the eligible rows into memory first and hand them off.
        if (archiver is DeleteAuditArchiver)
        {
            return await ctx.Set<AuditLog>()
                .Where(a => a.OccurredOnUtc < cutoff)
                .OrderBy(a => a.OccurredOnUtc)
                .Take(options.MaxRowsPerSweep)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        var rows = await ctx.Set<AuditLog>()
            .AsNoTracking()
            .Where(a => a.OccurredOnUtc < cutoff)
            .OrderBy(a => a.OccurredOnUtc)
            .Take(options.MaxRowsPerSweep)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await archiver.ArchiveAsync(ctx, rows, policy, ct).ConfigureAwait(false);
    }

    private async Task<int> SweepCountAsync(TDbContext ctx, int keep, CancellationToken ct)
    {
        // Collect (entityType, entityId, tenantId) groups with > keep rows, then for each group
        // delete everything beyond the latest `keep` rows. Done client-side per group to stay
        // provider-portable; bounded total deletes by MaxRowsPerSweep.
        var groups = await ctx.Set<AuditLog>()
            .GroupBy(a => new { a.EntityType, a.EntityId, a.TenantId })
            .Where(g => g.Count() > keep)
            .Select(g => new { g.Key.EntityType, g.Key.EntityId, g.Key.TenantId, Total = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var totalDeleted = 0;
        // v0.7.8: when a custom archiver is in effect we MUST funnel the count-policy
        // path through it too, otherwise CopyToTable consumers silently lose rows that
        // the count policy retires. The default DeleteAuditArchiver keeps the v0.7.7
        // fast path (id-list ExecuteDelete) so single-tenant consumers are unaffected.
        var useArchiver = archiver is not DeleteAuditArchiver;
        foreach (var group in groups)
        {
            if (totalDeleted >= options.MaxRowsPerSweep)
            {
                break;
            }
            if (useArchiver)
            {
                var archivableRows = await ctx.Set<AuditLog>()
                    .AsNoTracking()
                    .Where(a => a.EntityType == group.EntityType
                                && a.EntityId == group.EntityId
                                && a.TenantId == group.TenantId)
                    .OrderByDescending(a => a.OccurredOnUtc)
                    .Skip(keep)
                    .Take(options.MaxRowsPerSweep - totalDeleted)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                if (archivableRows.Count == 0)
                {
                    continue;
                }
                var archived = await archiver.ArchiveAsync(ctx, archivableRows, policy, ct).ConfigureAwait(false);
                totalDeleted += archived;
                continue;
            }
            var idsToDelete = await ctx.Set<AuditLog>()
                .Where(a => a.EntityType == group.EntityType
                            && a.EntityId == group.EntityId
                            && a.TenantId == group.TenantId)
                .OrderByDescending(a => a.OccurredOnUtc)
                .Skip(keep)
                .Take(options.MaxRowsPerSweep - totalDeleted)
                .Select(a => a.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (idsToDelete.Count == 0)
            {
                continue;
            }
            var deleted = await ctx.Set<AuditLog>()
                .Where(a => idsToDelete.Contains(a.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            totalDeleted += deleted;
        }
        return totalDeleted;
    }
}
