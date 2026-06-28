using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionAudit.Store;

/// <summary>
/// <see cref="IAuditHistoryStore"/> backed by the consumer's <see cref="DbContext"/>. Translates
/// an <see cref="AuditHistoryQuery"/> into a server-side query over the <see cref="AuditLog"/> set
/// and applies compaction inside a transaction (insert the compacted snapshot, delete the folded
/// rows). This is the default store registered by the DI wiring.
/// </summary>
public sealed class EfCoreAuditHistoryStore : AuditHistoryStoreBase
{
    private readonly DbContext context;

    /// <summary>Initializes a new instance reading from and writing to the supplied <see cref="DbContext"/>.</summary>
    public EfCoreAuditHistoryStore(DbContext context)
        => this.context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public override async Task<AuditHistoryPage> QueryAsync(AuditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();

        var filtered = ApplyFilters(context.Set<AuditLog>().AsNoTracking(), query);

        var total = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);
        if (total == 0)
        {
            return AuditHistoryPage.Empty(query.Skip, query.EffectiveTake);
        }

        var items = await ApplyOrder(filtered, query)
            .Skip(query.Skip)
            .Take(query.EffectiveTake)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new AuditHistoryPage(items, total, query.Skip, query.EffectiveTake);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<AuditAggregateBucket>> AggregateAsync(
        AuditAggregationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();

        var filtered = ApplyFilters(context.Set<AuditLog>().AsNoTracking(), query.Filter);

        // Every branch projects to (Key, Count) and runs the count as a server-side GROUP BY, so the
        // database returns one row per distinct group, never the underlying audit rows. The result set
        // is bounded by the cardinality of the grouped column (number of actions / entity types /
        // users / tenants / time buckets), not by the table size.
        switch (query.GroupBy)
        {
            case AuditAggregateBy.Action:
                {
                    var groups = await filtered
                        .GroupBy(a => a.Action)
                        .Select(g => new { g.Key, Count = (long)g.Count() })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return groups
                        .Select(g => new AuditAggregateBucket(g.Key.ToString(), g.Count))
                        .ToList();
                }
            case AuditAggregateBy.EntityType:
                {
                    var groups = await filtered
                        .GroupBy(a => a.EntityType)
                        .Select(g => new { g.Key, Count = (long)g.Count() })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return groups.Select(g => new AuditAggregateBucket(g.Key, g.Count)).ToList();
                }
            case AuditAggregateBy.UserId:
                {
                    var groups = await filtered
                        .GroupBy(a => a.UserId)
                        .Select(g => new { g.Key, Count = (long)g.Count() })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return groups.Select(g => new AuditAggregateBucket(g.Key, g.Count)).ToList();
                }
            case AuditAggregateBy.TenantId:
                {
                    var groups = await filtered
                        .GroupBy(a => a.TenantId)
                        .Select(g => new { g.Key, Count = (long)g.Count() })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return groups.Select(g => new AuditAggregateBucket(g.Key, g.Count)).ToList();
                }
            case AuditAggregateBy.TimeBucket:
                return await AggregateByTimeBucketAsync(filtered, query.TimeBucket, cancellationToken)
                    .ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(query), query.GroupBy, "Unknown audit aggregation group.");
        }
    }

    // Time bucketing groups by the year/month (and, below month, day/hour) components of
    // OccurredOnUtc. Pulling the components into the GROUP BY (rather than a provider-specific date
    // function such as date_trunc) keeps the query translatable across SQLite / SQL Server /
    // PostgreSQL / MySQL. The grouped result is bounded by the number of distinct buckets, so this
    // never materialises the row set; the bucket-start instant is reassembled from the components.
    private static async Task<IReadOnlyList<AuditAggregateBucket>> AggregateByTimeBucketAsync(
        IQueryable<AuditLog> filtered, AuditTimeBucket bucket, CancellationToken cancellationToken)
    {
        switch (bucket)
        {
            case AuditTimeBucket.Month:
                {
                    var groups = await filtered
                        .GroupBy(a => new { a.OccurredOnUtc.Year, a.OccurredOnUtc.Month })
                        .Select(g => new { g.Key.Year, g.Key.Month, Count = (long)g.Count() })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return groups
                        .Select(g => MakeTimeBucket(new DateTime(g.Year, g.Month, 1, 0, 0, 0, DateTimeKind.Utc), g.Count))
                        .ToList();
                }
            case AuditTimeBucket.Hour:
                {
                    var groups = await filtered
                        .GroupBy(a => new { a.OccurredOnUtc.Year, a.OccurredOnUtc.Month, a.OccurredOnUtc.Day, a.OccurredOnUtc.Hour })
                        .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, Count = (long)g.Count() })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return groups
                        .Select(g => MakeTimeBucket(new DateTime(g.Year, g.Month, g.Day, g.Hour, 0, 0, DateTimeKind.Utc), g.Count))
                        .ToList();
                }
            case AuditTimeBucket.Day:
            default:
                {
                    var groups = await filtered
                        .GroupBy(a => new { a.OccurredOnUtc.Year, a.OccurredOnUtc.Month, a.OccurredOnUtc.Day })
                        .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, Count = (long)g.Count() })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return groups
                        .Select(g => MakeTimeBucket(new DateTime(g.Year, g.Month, g.Day, 0, 0, 0, DateTimeKind.Utc), g.Count))
                        .ToList();
                }
        }
    }

    private static AuditAggregateBucket MakeTimeBucket(DateTime startUtc, long count)
        => new(startUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture), count)
        {
            BucketStartUtc = startUtc,
        };

    private static IOrderedQueryable<AuditLog> ApplyOrder(IQueryable<AuditLog> source, AuditHistoryQuery query)
    {
        var descending = query.Order == AuditHistoryOrder.NewestFirst;

        // The chosen field is the primary sort; OccurredOnUtc then Id are always appended as stable
        // tie-breaks so paging is deterministic. For the default OccurredOn field this collapses to the
        // pre-v0.11 "(OccurredOnUtc, Id)" ordering exactly.
        //
        // Null ordering is fixed at NULLS LAST regardless of direction (the documented contract on
        // AuditHistorySortField.UserId): a nullable sort key (UserId) would otherwise sort nulls at
        // opposite ends under ascending vs descending, AND providers disagree on the default placement
        // of NULLs (PostgreSQL puts them last on ASC, SQL Server first), so paging would be neither
        // stable nor portable. Emitting an explicit "key IS NULL" leading sort (false=0 before true=1)
        // forces non-null rows first, then nulls, in BOTH directions, identically across providers.
        IOrderedQueryable<AuditLog> ordered = query.SortBy switch
        {
            AuditHistorySortField.EntityType => descending
                ? source.OrderByDescending(a => a.EntityType)
                : source.OrderBy(a => a.EntityType),
            AuditHistorySortField.Action => descending
                ? source.OrderByDescending(a => a.Action)
                : source.OrderBy(a => a.Action),
            AuditHistorySortField.UserId => descending
                ? source.OrderBy(a => a.UserId == null).ThenByDescending(a => a.UserId)
                : source.OrderBy(a => a.UserId == null).ThenBy(a => a.UserId),
            _ => descending
                ? source.OrderByDescending(a => a.OccurredOnUtc)
                : source.OrderBy(a => a.OccurredOnUtc),
        };

        if (query.SortBy != AuditHistorySortField.OccurredOn)
        {
            ordered = descending
                ? ordered.ThenByDescending(a => a.OccurredOnUtc)
                : ordered.ThenBy(a => a.OccurredOnUtc);
        }

        return descending
            ? ordered.ThenByDescending(a => a.Id)
            : ordered.ThenBy(a => a.Id);
    }

    /// <inheritdoc />
    public override async Task<AuditCompactionResult> CompactAsync(AuditCompactionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        // Tenant scoping has three modes, and conflating them is what lets a no-tenant compaction
        // bleed across tenants:
        //   null  -> compact across ALL tenants for this entity id (operator-driven cross-tenant fold);
        //   ""    -> the no-tenant stream ONLY. A row has "no tenant" whether its column is the canonical
        //            "" (AuditTenant.Canonical, written since v0.9.0) or a pre-normalization null, so it
        //            matches (TenantId == null || TenantId == ""). Mirrors the chain verifier's no-tenant
        //            matching so a stream is scoped identically wherever it is keyed;
        //   value -> exactly that tenant's rows.
        // Critically, "" must NOT fall through to the null/all-tenants branch; that is the cross-tenant
        // widening bug.
        var requestedTenant = request.TenantId;
        var noTenantStream = requestedTenant is { Length: 0 };

        var rows = await context.Set<AuditLog>()
            .Where(a => a.EntityType == request.EntityType
                && a.EntityId == request.EntityId
                && (requestedTenant == null
                    || (noTenantStream && (a.TenantId == null || a.TenantId == ""))
                    || (!noTenantStream && a.TenantId == requestedTenant)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var plan = AuditHistoryCompactor.Plan(rows, request.RetainTail);
        if (!plan.IsEffective)
        {
            return plan.ToResult();
        }

        // The plan's SnapshotRow is the boundary row mutated in place. It was loaded (tracked) by
        // the query above, so its mutated state is already pending as an UPDATE; we only need to
        // remove the strictly-older folded rows. Keeping the boundary's Id and OccurredOnUtc means
        // the snapshot keeps its chronological position ahead of the retained tail. SaveChanges runs
        // in a single transaction by default, so a failure leaves the history untouched (the folded
        // rows are not deleted unless the snapshot update also lands).
        context.Set<AuditLog>().RemoveRange(plan.RowsToRemove);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return plan.ToResult();
    }

    private static IQueryable<AuditLog> ApplyFilters(IQueryable<AuditLog> source, AuditHistoryQuery query)
    {
        if (query.EntityType is { } entityType)
        {
            source = source.Where(a => a.EntityType == entityType);
        }
        if (query.EntityBaseType is { } baseType)
        {
            source = source.Where(a => a.EntityBaseType == baseType);
        }
        if (query.EntityId is { } entityId)
        {
            source = source.Where(a => a.EntityId == entityId);
        }
        if (query.Action is { } action)
        {
            source = source.Where(a => a.Action == action);
        }
        if (query.UserId is { } userId)
        {
            source = source.Where(a => a.UserId == userId);
        }
        if (query.TenantId is { } tenantId)
        {
            source = source.Where(a => a.TenantId == tenantId);
        }
        if (query.FromUtc is { } fromUtc)
        {
            source = source.Where(a => a.OccurredOnUtc >= fromUtc);
        }
        if (query.ToUtc is { } toUtc)
        {
            source = source.Where(a => a.OccurredOnUtc <= toUtc);
        }
        // v0.11.0 richer filters. Set membership translates to a server-side IN (...); the lists are
        // materialised first so EF emits a parameterised IN rather than re-evaluating the property.
        if (query.HasEntityTypesFilter)
        {
            var entityTypes = query.EntityTypes!.ToList();
            source = source.Where(a => entityTypes.Contains(a.EntityType));
        }
        if (query.HasActionsFilter)
        {
            var actions = query.Actions!.ToList();
            source = source.Where(a => actions.Contains(a.Action));
        }
        if (query.CorrelationId is { } correlationId)
        {
            source = source.Where(a => a.CorrelationId == correlationId);
        }
        if (query.UserType is { } userType)
        {
            source = source.Where(a => a.UserType == userType);
        }
        // Value-change predicate: keep rows whose Diff JSON references the requested path either
        // exactly ("op":"<verb>","path":"/p") or as the parent of a nested change
        // ("op":"<verb>","path":"/p/..."). The tokens are anchored to a patch operation's "path" member
        // (each is preceded by the "op":"<verb>", that always introduces the path in the serialized
        // operation), so a "path" string that appears inside an operation's "value" never falsely
        // matches; each token is also closed (by a quote or a slash) so a same-prefix sibling does not
        // match. We emit a relational LIKE per token via EF.Functions.Like rather than string.Contains
        // because on PostgreSQL the Diff column is jsonb: the Npgsql LIKE translation casts the jsonb
        // value to text (diff::text LIKE ...), which the bare Contains/LIKE over a jsonb column would
        // not, and on the text-typed providers (SQLite / SQL Server / MySQL) it is the same LIKE.
        var changedPathPredicate = BuildChangedPathPredicate(query);
        if (changedPathPredicate is not null)
        {
            source = source.Where(changedPathPredicate);
        }
        return source;
    }

    // The character that escapes LIKE metacharacters in the patterns below. A JSON Pointer segment can
    // legally contain '%', '_', or '\\', which are LIKE wildcards / the escape char; left unescaped they
    // would widen the match. We escape them in the token and pass the same char to EF.Functions.Like so
    // the pattern matches the literal token.
    private const char LikeEscape = '\\';

    // Builds "Diff LIKE %token% OR ..." over the op-anchored exact and prefix tokens, or null when no
    // ChangedPath filter is set. The OR is assembled as an expression tree so it translates as one
    // server-side predicate; each branch is an EF.Functions.Like with an explicit ESCAPE char.
    private static System.Linq.Expressions.Expression<Func<AuditLog, bool>>? BuildChangedPathPredicate(
        AuditHistoryQuery query)
    {
        var tokens = new List<string>(query.ChangedPathExactTokens.Count + query.ChangedPathPrefixTokens.Count);
        tokens.AddRange(query.ChangedPathExactTokens);
        tokens.AddRange(query.ChangedPathPrefixTokens);
        if (tokens.Count == 0)
        {
            return null;
        }

        var parameter = System.Linq.Expressions.Expression.Parameter(typeof(AuditLog), "a");
        var diff = System.Linq.Expressions.Expression.Property(parameter, nameof(AuditLog.Diff));
        var likeMethod = typeof(Microsoft.EntityFrameworkCore.DbFunctionsExtensions).GetMethod(
            nameof(Microsoft.EntityFrameworkCore.DbFunctionsExtensions.Like),
            new[] { typeof(Microsoft.EntityFrameworkCore.DbFunctions), typeof(string), typeof(string), typeof(string) })!;
        var efFunctions = System.Linq.Expressions.Expression.Constant(Microsoft.EntityFrameworkCore.EF.Functions);
        var escapeArg = System.Linq.Expressions.Expression.Constant(LikeEscape.ToString());

        System.Linq.Expressions.Expression? body = null;
        foreach (var token in tokens)
        {
            var pattern = System.Linq.Expressions.Expression.Constant("%" + EscapeForLike(token) + "%");
            var call = System.Linq.Expressions.Expression.Call(likeMethod, efFunctions, diff, pattern, escapeArg);
            body = body is null
                ? call
                : System.Linq.Expressions.Expression.OrElse(body, call);
        }

        return System.Linq.Expressions.Expression.Lambda<Func<AuditLog, bool>>(body!, parameter);
    }

    // Escapes LIKE wildcards so a token matches literally: the escape char first (so it isn't
    // double-escaped), then '%' and '_'. Pairs with the ESCAPE '\\' passed to EF.Functions.Like.
    private static string EscapeForLike(string token)
        => token
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
