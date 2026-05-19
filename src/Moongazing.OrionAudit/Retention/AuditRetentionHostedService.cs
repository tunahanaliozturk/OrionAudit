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

    /// <summary>Initializes a new retention sweep service.</summary>
    public AuditRetentionHostedService(
        IServiceScopeFactory scopeFactory,
        RetentionPolicy policy,
        RetentionSweepOptions options,
        TimeProvider clock,
        ILogger<AuditRetentionHostedService<TDbContext>> logger)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        // Bounded delete: take the oldest N rows past the cutoff. ExecuteDeleteAsync on a
        // .Take(N) query is the portable EF Core 9 idiom — translates to a TOP/LIMIT delete on
        // every supported provider.
        return await ctx.Set<AuditLog>()
            .Where(a => a.OccurredOnUtc < cutoff)
            .OrderBy(a => a.OccurredOnUtc)
            .Take(options.MaxRowsPerSweep)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
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
        foreach (var group in groups)
        {
            if (totalDeleted >= options.MaxRowsPerSweep)
            {
                break;
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
