namespace Moongazing.OrionAudit;

using System.Linq.Expressions;

/// <summary>
/// Composable filter / projection extensions on <see cref="IQueryable{T}"/> of
/// <see cref="AuditLog"/>. The existing <see cref="AuditQueryExtensions"/> methods extend
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/> so they auto-resolve the audit
/// table and the tenant resolver; this set extends the materialised query so consumers can
/// keep stacking filters AFTER calling <c>AuditFor&lt;T&gt;()</c> / <c>AuditLog()</c>, OR
/// compose against an audit query handed in from a different <see cref="Microsoft.EntityFrameworkCore.DbContext"/>
/// (the "cross-context" scenario where audit storage lives on a dedicated DB but operator
/// projections want to combine it with primary-DB data).
/// </summary>
public static class AuditLogQueryExtensions
{
    /// <summary>Restrict to rows whose <see cref="AuditLog.OccurredOnUtc"/> falls inside <paramref name="fromUtc"/> .. <paramref name="toUtc"/> (inclusive endpoints).</summary>
    /// <param name="query">The audit query.</param>
    /// <param name="fromUtc">Lower bound (inclusive).</param>
    /// <param name="toUtc">Upper bound (inclusive).</param>
    public static IQueryable<AuditLog> BetweenDates(this IQueryable<AuditLog> query, DateTime fromUtc, DateTime toUtc)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (toUtc < fromUtc)
        {
            throw new ArgumentException(
                $"AuditLogQueryExtensions.BetweenDates: toUtc ({toUtc:O}) is earlier than fromUtc ({fromUtc:O}).");
        }
        return query.Where(a => a.OccurredOnUtc >= fromUtc && a.OccurredOnUtc <= toUtc);
    }

    /// <summary>Restrict to rows whose <see cref="AuditLog.OccurredOnUtc"/> falls within the last <paramref name="window"/> from <see cref="DateTime.UtcNow"/>.</summary>
    public static IQueryable<AuditLog> WithinLast(this IQueryable<AuditLog> query, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"AuditLogQueryExtensions.WithinLast: window must be positive (got {window}).");
        }
        var cutoff = DateTime.UtcNow - window;
        return query.Where(a => a.OccurredOnUtc >= cutoff);
    }

    /// <summary>Restrict to rows attributed to <paramref name="userId"/>.</summary>
    public static IQueryable<AuditLog> ByUser(this IQueryable<AuditLog> query, string userId)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        return query.Where(a => a.UserId == userId);
    }

    /// <summary>Restrict to rows attributed to any of the supplied <paramref name="userIds"/>.</summary>
    public static IQueryable<AuditLog> ByUsers(this IQueryable<AuditLog> query, IEnumerable<string> userIds)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(userIds);
        // Materialise to List<T> rather than T[] so the relational LINQ translator picks the
        // List.Contains overload that maps cleanly to SQL IN. Array.Contains uses the
        // ReadOnlySpan-based extension on .NET 9+ which the EF Core expression interpreter
        // cannot evaluate for parameter binding.
        var ids = userIds is List<string> alreadyList ? alreadyList : userIds.ToList();
        return query.Where(a => a.UserId != null && ids.Contains(a.UserId));
    }

    /// <summary>Restrict to rows whose <see cref="AuditLog.UserType"/> matches <paramref name="userType"/> (e.g. <c>"user"</c>, <c>"system"</c>, <c>"job"</c>).</summary>
    public static IQueryable<AuditLog> ByUserType(this IQueryable<AuditLog> query, string userType)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrEmpty(userType);
        return query.Where(a => a.UserType == userType);
    }

    /// <summary>Restrict to rows tagged with <paramref name="tenantId"/>. Use this when bypassing the auto-resolved tenant filter via <c>crossTenant: true</c>.</summary>
    public static IQueryable<AuditLog> ByTenant(this IQueryable<AuditLog> query, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        return query.Where(a => a.TenantId == tenantId);
    }

    /// <summary>Restrict to rows with the given <paramref name="action"/> (<c>Create</c>, <c>Update</c>, <c>Delete</c>).</summary>
    public static IQueryable<AuditLog> ByAction(this IQueryable<AuditLog> query, AuditAction action)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Where(a => a.Action == action);
    }

    /// <summary>Restrict to rows whose <see cref="AuditLog.CorrelationId"/> matches.</summary>
    public static IQueryable<AuditLog> ByCorrelation(this IQueryable<AuditLog> query, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrEmpty(correlationId);
        return query.Where(a => a.CorrelationId == correlationId);
    }

    /// <summary>Order rows newest-first by <see cref="AuditLog.OccurredOnUtc"/>.</summary>
    public static IOrderedQueryable<AuditLog> Newest(this IQueryable<AuditLog> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.OrderByDescending(a => a.OccurredOnUtc);
    }

    /// <summary>Order rows oldest-first by <see cref="AuditLog.OccurredOnUtc"/>.</summary>
    public static IOrderedQueryable<AuditLog> Oldest(this IQueryable<AuditLog> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.OrderBy(a => a.OccurredOnUtc);
    }

    /// <summary>
    /// Project distinct user ids that appear in the query. Useful for fan-out joins against
    /// a User table that lives in a different <see cref="Microsoft.EntityFrameworkCore.DbContext"/>
    /// (the cross-context scenario): take the result set in-process, then issue a single
    /// <c>WHERE Id IN (...)</c> against the user-store context to materialise display names.
    /// </summary>
    public static IQueryable<string> DistinctUserIds(this IQueryable<AuditLog> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Where(a => a.UserId != null).Select(a => a.UserId!).Distinct();
    }

    /// <summary>
    /// Project (UserId, ActivityCount) pairs ordered by descending activity, capped at
    /// <paramref name="top"/>. Useful for "who did what" dashboards built across audit DB
    /// + identity DB without paying for a SQL-side JOIN.
    /// </summary>
    public static IQueryable<UserActivitySummary> TopActorsByCount(
        this IQueryable<AuditLog> query, int top)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (top < 1)
        {
            throw new ArgumentException(
                $"AuditLogQueryExtensions.TopActorsByCount: top must be at least 1 (got {top}).");
        }
        // Two-stage projection: the inner Select projects the GROUP BY result into an
        // anonymous shape that every relational provider translates cleanly to SQL; the
        // outer Select materialises the strongly-typed record. EF Core's translator does
        // NOT recognise positional-record construction inside the GROUP BY Select on
        // SQLite / SQL Server, so the anonymous indirection is the path with the widest
        // provider support.
        return query
            .Where(a => a.UserId != null)
            .GroupBy(a => a.UserId!)
            .Select(g => new { UserId = g.Key, ActivityCount = g.Count() })
            .OrderByDescending(s => s.ActivityCount)
            .Take(top)
            .Select(s => new UserActivitySummary(s.UserId, s.ActivityCount));
    }

    /// <summary>
    /// Compose a free-form predicate. Equivalent to <see cref="Queryable.Where{TSource}(IQueryable{TSource}, Expression{Func{TSource, bool}})"/>
    /// but reads as a continuation of the audit DSL.
    /// </summary>
    public static IQueryable<AuditLog> Matching(
        this IQueryable<AuditLog> query, Expression<Func<AuditLog, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(predicate);
        return query.Where(predicate);
    }
}

/// <summary>
/// Aggregate row returned by <see cref="AuditLogQueryExtensions.TopActorsByCount"/>. The
/// shape exists as a top-level type (not anonymous) so it composes with EF Core's
/// projection translator and so callers can pass the results through method signatures.
/// </summary>
/// <param name="UserId">Stable user id.</param>
/// <param name="ActivityCount">Number of <see cref="AuditLog"/> rows attributed to the user within the filtered query.</param>
public sealed record UserActivitySummary(string UserId, int ActivityCount);
