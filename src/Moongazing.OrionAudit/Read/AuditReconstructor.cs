using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Read;

/// <summary>Default <see cref="IAuditReconstructor"/> backed by the consumer's <see cref="DbContext"/>.</summary>
public sealed class AuditReconstructor : IAuditReconstructor
{
    private readonly DbContext context;
    private readonly JsonSerializerContext? jsonContext;

    /// <summary>Initializes a new instance reading from the supplied <see cref="DbContext"/>.</summary>
    public AuditReconstructor(DbContext context) : this(context, jsonContext: null) { }

    /// <summary>
    /// Initializes a new instance with an optional source-generated <see cref="JsonSerializerContext"/>
    /// that the reconstructor will use when deserialising replayed state — trim-safe and
    /// Native-AOT clean. When null, falls back to reflective <c>JsonSerializer.Deserialize&lt;T&gt;</c>.
    /// </summary>
    public AuditReconstructor(DbContext context, JsonSerializerContext? jsonContext)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.jsonContext = jsonContext;
    }

    /// <inheritdoc />
    public async Task<T?> ReconstructAsync<T>(string entityId, DateTime asOf, CancellationToken cancellationToken = default)
        where T : class, new()
    {
        ArgumentException.ThrowIfNullOrEmpty(entityId);
        using var activity = OrionAuditTelemetry.ActivitySource.StartActivity("OrionAudit.Reconstruct", ActivityKind.Internal);
        activity?.SetTag("orionaudit.entity_type", typeof(T).Name);
        activity?.SetTag("orionaudit.as_of", asOf.ToString("O"));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var entityTypeName = typeof(T).AssemblyQualifiedName!;
            var rows = await context.Set<AuditLog>()
                .Where(a => a.EntityType == entityTypeName && a.EntityId == entityId && a.OccurredOnUtc <= asOf)
                .OrderBy(a => a.OccurredOnUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            activity?.SetTag("orionaudit.audit_row_count", rows.Count);
            var result = Replay<T>(rows, entityId);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        finally
        {
            OrionAuditTelemetry.ReconstructDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, T?>> ReconstructManyAsync<T>(
        IEnumerable<string> entityIds,
        DateTime asOf,
        CancellationToken cancellationToken = default)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        using var activity = OrionAuditTelemetry.ActivitySource.StartActivity("OrionAudit.ReconstructMany", ActivityKind.Internal);
        activity?.SetTag("orionaudit.entity_type", typeof(T).Name);
        activity?.SetTag("orionaudit.as_of", asOf.ToString("O"));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var idList = entityIds.ToList();
            activity?.SetTag("orionaudit.entity_id_count", idList.Count);
            var entityTypeName = typeof(T).AssemblyQualifiedName!;

            var rows = await context.Set<AuditLog>()
                .Where(a => a.EntityType == entityTypeName && idList.Contains(a.EntityId) && a.OccurredOnUtc <= asOf)
                .OrderBy(a => a.OccurredOnUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            activity?.SetTag("orionaudit.audit_row_count", rows.Count);
            var grouped = rows.GroupBy(a => a.EntityId).ToDictionary(g => g.Key, g => g.ToList());
            var result = new Dictionary<string, T?>(idList.Count, StringComparer.Ordinal);
            foreach (var id in idList)
            {
                result[id] = grouped.TryGetValue(id, out var group) ? Replay<T>(group, id) : null;
            }
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        finally
        {
            OrionAuditTelemetry.ReconstructDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private T? Replay<T>(List<AuditLog> rows, string entityId) where T : class, new()
    {
        if (rows.Count == 0)
        {
            return null;
        }
        if (rows[^1].Action is AuditAction.Deleted or AuditAction.SoftDeleted)
        {
            return null;
        }

        if (rows[0].Action != AuditAction.Inserted)
        {
            throw new OrionAuditException(
                $"Audit history for entity id '{entityId}' starts with a non-Insert action — corrupted history.");
        }

        // Find the latest snapshot row at or before asOf (rows are already ordered by OccurredOnUtc).
        // Snapshots can come from the periodic SnapshotPolicy on Update rows, or implicitly from
        // Insert (initial state). Walk backwards to find the freshest one.
        var snapshotIndex = -1;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Snapshot is not null && rows[i].Action is AuditAction.Updated or AuditAction.Inserted)
            {
                snapshotIndex = i;
                break;
            }
        }

        JsonObject state;
        int startIndex;
        if (snapshotIndex >= 0 && rows[snapshotIndex].Snapshot is { } snapshotJson)
        {
            state = JsonNode.Parse(snapshotJson)?.AsObject()
                ?? throw new OrionAuditException(
                    $"Snapshot on row {rows[snapshotIndex].Id} for entity '{entityId}' did not deserialize as a JSON object.");
            // Replay only the diffs that came AFTER the snapshot.
            startIndex = snapshotIndex + 1;
        }
        else
        {
            state = new JsonObject();
            startIndex = 0;
        }

        for (var i = startIndex; i < rows.Count; i++)
        {
            var row = rows[i];
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

        // v0.7.29: record how many rows were replayed past the snapshot (or all rows when none
        // applied) - the snapshot-effectiveness signal. Recorded only when an entity was actually
        // reconstructed (the early null returns above for absent/deleted entities do not emit).
        OrionAuditTelemetry.RecordReconstructEventsReplayed(rows.Count - startIndex);

        return jsonContext is not null
            ? (T?)JsonSerializer.Deserialize(state, typeof(T), jsonContext)
            : JsonSerializer.Deserialize<T>(state);
    }
}
