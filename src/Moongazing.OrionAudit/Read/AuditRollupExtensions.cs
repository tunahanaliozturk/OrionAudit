namespace Moongazing.OrionAudit;

using System.Linq;

/// <summary>
/// Time-series rollup helpers for <see cref="AuditLog"/>. Aggregate the audit table into
/// per-bucket counts (daily, monthly) so operator dashboards can render activity
/// histograms without dragging every row into memory. Pairs with the v0.7.6 composable
/// filters - chain the bucket helpers AFTER <c>AuditFor&lt;T&gt;()</c> / <c>AuditLog()</c>
/// + filters to scope the rollup.
/// </summary>
public static class AuditRollupExtensions
{
    /// <summary>
    /// Group rows by UTC calendar day and project <see cref="AuditDailyBucket"/> rows
    /// ordered by ascending date. The bucket date is the start of the day (00:00:00 UTC),
    /// kept as <see cref="DateOnly"/> so consumers do not need to normalise on the client
    /// side. Empty days are NOT materialised - if you need a dense series, fill gaps in
    /// memory after materialising the result.
    /// </summary>
    public static IQueryable<AuditDailyBucket> RollupByDay(this IQueryable<AuditLog> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query
            .GroupBy(a => new
            {
                a.OccurredOnUtc.Year,
                a.OccurredOnUtc.Month,
                a.OccurredOnUtc.Day,
            })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                g.Key.Day,
                Count = g.Count(),
            })
            .OrderBy(b => b.Year).ThenBy(b => b.Month).ThenBy(b => b.Day)
            .Select(b => new AuditDailyBucket(
                new DateOnly(b.Year, b.Month, b.Day),
                b.Count));
    }

    /// <summary>
    /// Group rows by UTC calendar month and project <see cref="AuditMonthlyBucket"/> rows
    /// ordered by ascending month. The bucket year/month carry the first day of the month
    /// (e.g. <c>2026-06</c>); the day component is omitted from the projection because the
    /// month boundary is the meaningful axis.
    /// </summary>
    public static IQueryable<AuditMonthlyBucket> RollupByMonth(this IQueryable<AuditLog> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query
            .GroupBy(a => new
            {
                a.OccurredOnUtc.Year,
                a.OccurredOnUtc.Month,
            })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                Count = g.Count(),
            })
            .OrderBy(b => b.Year).ThenBy(b => b.Month)
            .Select(b => new AuditMonthlyBucket(b.Year, b.Month, b.Count));
    }

    /// <summary>
    /// Group rows by UTC calendar day AND <see cref="AuditAction"/>. The result has at most
    /// one row per (day, action) pair; if a day saw only inserts, only one entry surfaces.
    /// Useful for stacked activity charts that distinguish create vs update vs delete vs
    /// soft-delete.
    /// </summary>
    public static IQueryable<AuditDailyActionBucket> RollupByDayAndAction(this IQueryable<AuditLog> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query
            .GroupBy(a => new
            {
                a.OccurredOnUtc.Year,
                a.OccurredOnUtc.Month,
                a.OccurredOnUtc.Day,
                a.Action,
            })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                g.Key.Day,
                g.Key.Action,
                Count = g.Count(),
            })
            .OrderBy(b => b.Year).ThenBy(b => b.Month).ThenBy(b => b.Day).ThenBy(b => b.Action)
            .Select(b => new AuditDailyActionBucket(
                new DateOnly(b.Year, b.Month, b.Day),
                b.Action,
                b.Count));
    }

    /// <summary>
    /// Group rows by UTC calendar day AND user id, capped at <paramref name="topUsersPerDay"/>
    /// rows per day. Returns the heaviest contributors of each day so a per-day leaderboard
    /// fits in a single round-trip. Days with fewer than <paramref name="topUsersPerDay"/>
    /// distinct users return all of them.
    /// </summary>
    /// <param name="query">The audit query.</param>
    /// <param name="topUsersPerDay">Maximum users surfaced per day (must be positive).</param>
    public static IEnumerable<AuditDailyUserBucket> RollupByDayAndUser(
        this IEnumerable<AuditLog> query, int topUsersPerDay)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (topUsersPerDay < 1)
        {
            throw new ArgumentException(
                $"AuditRollupExtensions.RollupByDayAndUser: topUsersPerDay must be at least 1 (got {topUsersPerDay}).",
                nameof(topUsersPerDay));
        }
        return query
            .Where(a => a.UserId != null)
            .GroupBy(a => new
            {
                Day = new DateOnly(a.OccurredOnUtc.Year, a.OccurredOnUtc.Month, a.OccurredOnUtc.Day),
                a.UserId,
            })
            .Select(g => new AuditDailyUserBucket(g.Key.Day, g.Key.UserId!, g.Count()))
            .GroupBy(b => b.Day)
            .SelectMany(g => g
                .OrderByDescending(b => b.ActivityCount)
                .ThenBy(b => b.UserId)
                .Take(topUsersPerDay))
            .OrderBy(b => b.Day).ThenByDescending(b => b.ActivityCount).ThenBy(b => b.UserId);
    }
}

/// <summary>
/// Daily activity bucket: count of <see cref="AuditLog"/> rows that landed on the given
/// <paramref name="Day"/> (UTC).
/// </summary>
/// <param name="Day">Calendar day of the bucket (UTC).</param>
/// <param name="Count">Number of audit rows on that day within the source query.</param>
public sealed record AuditDailyBucket(DateOnly Day, int Count);

/// <summary>
/// Monthly activity bucket: count of <see cref="AuditLog"/> rows that landed in the given
/// month (UTC). <paramref name="Year"/> is the four-digit calendar year and
/// <paramref name="Month"/> is 1..12.
/// </summary>
public sealed record AuditMonthlyBucket(int Year, int Month, int Count);

/// <summary>
/// Daily activity bucket keyed by (Day, Action). One row per distinct action that fired on
/// the day.
/// </summary>
public sealed record AuditDailyActionBucket(DateOnly Day, AuditAction Action, int Count);

/// <summary>
/// Per-day, per-user activity bucket returned by
/// <see cref="AuditRollupExtensions.RollupByDayAndUser"/>.
/// </summary>
public sealed record AuditDailyUserBucket(DateOnly Day, string UserId, int ActivityCount);
