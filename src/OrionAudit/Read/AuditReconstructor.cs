using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OrionAudit.Capture;

namespace OrionAudit.Read;

/// <summary>Default <see cref="IAuditReconstructor"/> backed by the consumer's <see cref="DbContext"/>.</summary>
public sealed class AuditReconstructor : IAuditReconstructor
{
    private readonly DbContext context;

    /// <summary>Initializes a new instance reading from the supplied <see cref="DbContext"/>.</summary>
    public AuditReconstructor(DbContext context)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<T?> ReconstructAsync<T>(string entityId, DateTime asOf, CancellationToken cancellationToken = default)
        where T : class, new()
    {
        ArgumentException.ThrowIfNullOrEmpty(entityId);
        var entityTypeName = typeof(T).AssemblyQualifiedName!;
        var rows = await context.Set<AuditLog>()
            .Where(a => a.EntityType == entityTypeName && a.EntityId == entityId && a.OccurredOnUtc <= asOf)
            .OrderBy(a => a.OccurredOnUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Replay<T>(rows, entityId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, T?>> ReconstructManyAsync<T>(
        IEnumerable<string> entityIds,
        DateTime asOf,
        CancellationToken cancellationToken = default)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        var idList = entityIds.ToList();
        var entityTypeName = typeof(T).AssemblyQualifiedName!;

        var rows = await context.Set<AuditLog>()
            .Where(a => a.EntityType == entityTypeName && idList.Contains(a.EntityId) && a.OccurredOnUtc <= asOf)
            .OrderBy(a => a.OccurredOnUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var grouped = rows.GroupBy(a => a.EntityId).ToDictionary(g => g.Key, g => g.ToList());
        var result = new Dictionary<string, T?>(idList.Count, StringComparer.Ordinal);
        foreach (var id in idList)
        {
            result[id] = grouped.TryGetValue(id, out var group) ? Replay<T>(group, id) : null;
        }
        return result;
    }

    private static T? Replay<T>(List<AuditLog> rows, string entityId) where T : class, new()
    {
        if (rows.Count == 0)
        {
            return null;
        }
        if (rows[^1].Action == AuditAction.Deleted)
        {
            return null;
        }

        if (rows[0].Action != AuditAction.Inserted)
        {
            throw new OrionAuditException(
                $"Audit history for entity id '{entityId}' starts with a non-Insert action — corrupted history.");
        }

        var state = new JsonObject();
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Diff) || row.Diff == "[]")
            {
                continue;
            }
            try
            {
                state = DiffEngine.Apply(state, row.Diff);
            }
            catch (Exception ex)
            {
                throw new OrionAuditException(
                    $"Failed to replay audit row {row.Id} for entity '{entityId}': {ex.Message}", ex);
            }
        }

        return JsonSerializer.Deserialize<T>(state);
    }
}
