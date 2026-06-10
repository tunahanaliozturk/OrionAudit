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
        var deleted = await DispatchPolicyAsync(ctx, policy, cancellationToken).ConfigureAwait(false);

        activity?.SetTag("orionaudit.retention.rows_deleted", deleted);
        OrionAuditTelemetry.RetentionRowsDeleted.Add(deleted);
        OrionAuditTelemetry.RetentionSweepDuration.Record(sw.Elapsed.TotalMilliseconds);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return deleted;
    }

    private async Task<int> DispatchPolicyAsync(TDbContext ctx, RetentionPolicy effective, CancellationToken ct)
    {
        return effective switch
        {
            RetentionPolicy.RetainForPolicy r => await SweepAgeAsync(ctx, r.Age, ct).ConfigureAwait(false),
            RetentionPolicy.RetainCountPolicy r => await SweepCountAsync(ctx, r.Rows, ct).ConfigureAwait(false),
            RetentionPolicy.PerTenantPolicy pt => await SweepPerTenantAsync(ctx, pt, ct).ConfigureAwait(false),
            RetentionPolicy.PerEntityTypePolicy pe => await SweepPerEntityTypeAsync(ctx, tenantId: null, pe, options.MaxRowsPerSweep, ct).ConfigureAwait(false),
            _ => 0,
        };
    }

    private async Task<int> SweepPerEntityTypeAsync(
        TDbContext ctx,
        string? tenantId,
        RetentionPolicy.PerEntityTypePolicy pe,
        int remainingBudget,
        CancellationToken ct)
    {
        // Discover the entity types (optionally scoped to a tenant). Cap the cycle's
        // total deletions at remainingBudget across entity types so the per-cycle budget
        // applies to PerEntityType nested under PerTenant just as it does to top-level
        // PerEntityType.
        var entityTypes = await (tenantId is null
                ? ctx.Set<AuditLog>().AsNoTracking().Select(a => a.EntityType).Distinct()
                : ctx.Set<AuditLog>().AsNoTracking().Where(a => a.TenantId == tenantId).Select(a => a.EntityType).Distinct())
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var total = 0;
        foreach (var entityType in entityTypes)
        {
            ct.ThrowIfCancellationRequested();
            if (total >= remainingBudget)
            {
                break;
            }
            var policy = pe.ByEntityType.TryGetValue(entityType, out var p) ? p : pe.Fallback;
            switch (policy)
            {
                case RetentionPolicy.NonePolicy:
                    continue;
                case RetentionPolicy.RetainForPolicy r:
                    total += await SweepAgeScopedAsync(ctx, tenantId, entityType, r.Age, remainingBudget - total, ct).ConfigureAwait(false);
                    break;
                case RetentionPolicy.RetainCountPolicy r:
                    total += await SweepCountScopedAsync(ctx, tenantId, entityType, r.Rows, remainingBudget - total, ct).ConfigureAwait(false);
                    break;
            }
        }
        return total;
    }

    private async Task<int> SweepAgeScopedAsync(
        TDbContext ctx, string? tenantId, string entityType, TimeSpan age, int remainingBudget, CancellationToken ct)
    {
        if (remainingBudget < 1)
        {
            return 0;
        }
        var cutoff = clock.GetUtcNow().UtcDateTime - age;
        var batch = Math.Min(options.MaxRowsPerSweep, remainingBudget);
        IQueryable<AuditLog> query = ctx.Set<AuditLog>()
            .Where(a => a.EntityType == entityType && a.OccurredOnUtc < cutoff);
        if (tenantId is not null)
        {
            query = query.Where(a => a.TenantId == tenantId);
        }
        if (archiver is DeleteAuditArchiver)
        {
            return await query.OrderBy(a => a.OccurredOnUtc).Take(batch).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        var rows = await query.AsNoTracking().OrderBy(a => a.OccurredOnUtc).Take(batch).ToListAsync(ct).ConfigureAwait(false);
        return await archiver.ArchiveAsync(ctx, rows, policy, ct).ConfigureAwait(false);
    }

    private async Task<int> SweepCountScopedAsync(
        TDbContext ctx, string? tenantId, string entityType, int keep, int remainingBudget, CancellationToken ct)
    {
        if (remainingBudget < 1)
        {
            return 0;
        }
        // Top-level PerEntityType calls this with tenantId=null. The previous version
        // treated null as "unscoped" - grouping only by EntityId - which meant rows for
        // (Order, id=42) belonging to different tenants merged into one group and
        // tenant A's RetainCount could trim tenant B's rows. We group by
        // (TenantId, EntityId) so per-(tenant, entity) windows compose correctly even
        // when the top-level policy is PerEntityType.
        var groups = await ctx.Set<AuditLog>()
            .Where(a => a.EntityType == entityType && (tenantId == null || a.TenantId == tenantId))
            .GroupBy(a => new { a.TenantId, a.EntityId })
            .Where(g => g.Count() > keep)
            .Select(g => new { g.Key.TenantId, g.Key.EntityId, Total = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var totalDeleted = 0;
        var useArchiver = archiver is not DeleteAuditArchiver;
        foreach (var group in groups)
        {
            if (totalDeleted >= remainingBudget)
            {
                break;
            }
            var trim = ctx.Set<AuditLog>()
                .Where(a => a.EntityType == entityType
                            && a.TenantId == group.TenantId
                            && a.EntityId == group.EntityId);
            if (useArchiver)
            {
                // Route through the archiver: select the rows-to-archive (the OLDEST
                // ones past `keep`) and hand them off so a CopyToTable consumer can
                // ship them to the archive store before they leave the live table.
                var archivableRows = await trim
                    .AsNoTracking()
                    .OrderByDescending(a => a.OccurredOnUtc)
                    .Skip(keep)
                    .Take(remainingBudget - totalDeleted)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                if (archivableRows.Count == 0)
                {
                    continue;
                }
                totalDeleted += await archiver.ArchiveAsync(ctx, archivableRows, policy, ct).ConfigureAwait(false);
                continue;
            }
            var idsToDelete = await trim
                .OrderByDescending(a => a.OccurredOnUtc)
                .Skip(keep)
                .Take(remainingBudget - totalDeleted)
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

    private async Task<int> SweepPerTenantAsync(TDbContext ctx, RetentionPolicy.PerTenantPolicy pt, CancellationToken ct)
    {
        // Discover the tenants the sweep has rows for. For each tenant pick the matching
        // policy (or the fallback) and dispatch to a tenant-scoped sweep. Per-cycle
        // budget (MaxRowsPerSweep) is enforced ACROSS all tenants, not per tenant - the
        // overall cap on a single sweep cycle is the documented cross-table contract.
        var tenants = await ctx.Set<AuditLog>()
            .AsNoTracking()
            .Select(a => a.TenantId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var total = 0;
        var budget = options.MaxRowsPerSweep;
        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            if (total >= budget)
            {
                break;
            }
            var perTenantPolicy = tenantId is not null && pt.ByTenantId.TryGetValue(tenantId, out var p)
                ? p
                : pt.Fallback;
            var remaining = budget - total;
            switch (perTenantPolicy)
            {
                case RetentionPolicy.NonePolicy:
                    continue;
                case RetentionPolicy.RetainForPolicy r:
                    total += await SweepAgeForTenantAsync(ctx, tenantId, r.Age, remaining, ct).ConfigureAwait(false);
                    break;
                case RetentionPolicy.RetainCountPolicy r:
                    total += await SweepCountForTenantAsync(ctx, tenantId, r.Rows, remaining, ct).ConfigureAwait(false);
                    break;
                case RetentionPolicy.PerEntityTypePolicy pe:
                    // v0.7.10 nested PerEntityType under PerTenant: scoped to this
                    // tenant's rows so per-(tenant, entity-type) windows compose
                    // without cross-tenant leakage.
                    total += await SweepPerEntityTypeAsync(ctx, tenantId, pe, remaining, ct).ConfigureAwait(false);
                    break;
            }
        }
        return total;
    }

    private static async Task<int> SweepCountForTenantAsync(TDbContext ctx, string? tenantId, int keep, int remainingBudget, CancellationToken ct)
    {
        if (remainingBudget < 1)
        {
            return 0;
        }
        var groups = await ctx.Set<AuditLog>()
            .Where(a => a.TenantId == tenantId)
            .GroupBy(a => new { a.EntityType, a.EntityId })
            .Where(g => g.Count() > keep)
            .Select(g => new { g.Key.EntityType, g.Key.EntityId, Total = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var totalDeleted = 0;
        foreach (var group in groups)
        {
            if (totalDeleted >= remainingBudget)
            {
                break;
            }
            var idsToDelete = await ctx.Set<AuditLog>()
                .Where(a => a.TenantId == tenantId
                            && a.EntityType == group.EntityType
                            && a.EntityId == group.EntityId)
                .OrderByDescending(a => a.OccurredOnUtc)
                .Skip(keep)
                .Take(remainingBudget - totalDeleted)
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

    private async Task<int> SweepAgeForTenantAsync(TDbContext ctx, string? tenantId, TimeSpan age, int remainingBudget, CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - age;
        var batch = Math.Min(options.MaxRowsPerSweep, remainingBudget);
        if (batch < 1)
        {
            return 0;
        }
        if (archiver is DeleteAuditArchiver)
        {
            return await ctx.Set<AuditLog>()
                .Where(a => a.TenantId == tenantId && a.OccurredOnUtc < cutoff)
                .OrderBy(a => a.OccurredOnUtc)
                .Take(batch)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }
        var rows = await ctx.Set<AuditLog>()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.OccurredOnUtc < cutoff)
            .OrderBy(a => a.OccurredOnUtc)
            .Take(batch)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return await archiver.ArchiveAsync(ctx, rows, policy, ct).ConfigureAwait(false);
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
