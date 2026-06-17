using System.Text.Json.Nodes;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Demo;

/// <summary>
/// Records an in-memory audit trail for a single entity by snapshotting it after
/// each mutation and emitting <see cref="AuditLog"/> rows - the same row shape the
/// EF Core interceptor writes, minus the database. The first change is captured as
/// <see cref="AuditAction.Inserted"/> with a full snapshot (the replay anchor);
/// subsequent changes carry only the RFC 6902 diff against the previous state.
/// </summary>
internal sealed class AuditTrail
{
    private readonly string entityType = typeof(Order).AssemblyQualifiedName!;
    private readonly string entityId;
    private readonly List<AuditLog> rows = new();
    private JsonObject previous = new();
    private DateTime clock;
    private bool inserted;

    public AuditTrail(Guid id, DateTime startUtc)
    {
        entityId = id.ToString();
        clock = startUtc;
    }

    public IReadOnlyList<AuditLog> Rows => rows;

    /// <summary>Captures the current entity state as the next row in the trail.</summary>
    public AuditTrail Capture(Order order, AuditAction action, string user)
    {
        var snapshot = DemoSupport.Snapshot(order);
        clock = clock.AddMinutes(30);

        if (!inserted)
        {
            // First row anchors the trail: store the full snapshot so reconstruction
            // can start from a concrete state rather than an empty object.
            rows.Add(new AuditLog
            {
                EntityType = entityType,
                EntityId = entityId,
                Action = AuditAction.Inserted,
                OccurredOnUtc = clock,
                UserDisplay = user,
                CorrelationId = $"corr-{rows.Count + 1:000}",
                Diff = DiffEngine.Compute(new JsonObject(), snapshot),
                Snapshot = snapshot.ToJsonString(),
            });
            inserted = true;
        }
        else
        {
            var diff = DiffEngine.Compute(previous, snapshot);
            rows.Add(new AuditLog
            {
                EntityType = entityType,
                EntityId = entityId,
                Action = action,
                OccurredOnUtc = clock,
                UserDisplay = user,
                CorrelationId = $"corr-{rows.Count + 1:000}",
                Diff = diff,
                Snapshot = action is AuditAction.Deleted or AuditAction.SoftDeleted
                    ? snapshot.ToJsonString()
                    : null,
            });
        }

        previous = snapshot;
        return this;
    }

    /// <summary>
    /// Pure in-memory time-travel: replays the trail up to <paramref name="asOf"/> by
    /// starting from the latest snapshot anchor at or before that instant and applying
    /// each later diff in order. Mirrors AuditReconstructor.Replay but takes no DbContext.
    /// Returns null when the entity did not exist (or was deleted) at that instant.
    /// </summary>
    public JsonObject? ReconstructAsOf(DateTime asOf)
    {
        var ordered = rows.Where(r => r.OccurredOnUtc <= asOf)
                          .OrderBy(r => r.OccurredOnUtc)
                          .ToList();
        if (ordered.Count == 0)
        {
            return null;
        }
        if (ordered[^1].Action is AuditAction.Deleted or AuditAction.SoftDeleted)
        {
            return null;
        }

        // Walk back to the freshest snapshot anchor.
        var anchor = -1;
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            if (ordered[i].Snapshot is not null &&
                ordered[i].Action is AuditAction.Inserted or AuditAction.Updated)
            {
                anchor = i;
                break;
            }
        }

        JsonObject state;
        int start;
        if (anchor >= 0)
        {
            state = JsonNode.Parse(ordered[anchor].Snapshot!)!.AsObject();
            start = anchor + 1;
        }
        else
        {
            state = new JsonObject();
            start = 0;
        }

        for (var i = start; i < ordered.Count; i++)
        {
            var diff = ordered[i].Diff;
            if (!string.IsNullOrEmpty(diff) && diff != "[]")
            {
                state = DiffEngine.Apply(state, diff);
            }
        }
        return state;
    }
}
