using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Moongazing.OrionAudit.Read;

/// <summary>How a single field changed within an audited entry.</summary>
public enum ChangeKind
{
    /// <summary>A property gained a value (RFC 6902 <c>add</c>).</summary>
    Added,

    /// <summary>A property lost a value (RFC 6902 <c>remove</c>).</summary>
    Removed,

    /// <summary>A property's value changed (RFC 6902 <c>replace</c>).</summary>
    Modified,
}

/// <summary>One field-level change extracted from an <see cref="AuditLog"/> row's RFC 6902 diff.</summary>
public sealed class FieldChange
{
    /// <summary>JSON Pointer path of the changed property (e.g. <c>/Body</c>).</summary>
    public string PropertyPath { get; init; } = default!;

    /// <summary>The pre-change value as a string, or null for an <see cref="ChangeKind.Added"/> change.</summary>
    public string? OldValue { get; init; }

    /// <summary>The post-change value as a string, or null for a <see cref="ChangeKind.Removed"/> change.</summary>
    public string? NewValue { get; init; }

    /// <summary>Whether this field was added, removed, or modified. Serialized as a string for UI consumers.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ChangeKind>))]
    public ChangeKind ChangeKind { get; init; }
}

/// <summary>Human-readable view of a single <see cref="AuditLog"/> row.</summary>
public sealed class AuditEntryView
{
    /// <summary>The audit row's id.</summary>
    public Guid Id { get; init; }

    /// <summary>What kind of change the row records. Serialized as a string ("Inserted", "Updated", ...) for UI consumers.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AuditAction>))]
    public AuditAction Action { get; init; }

    /// <summary>UTC timestamp of the change.</summary>
    public DateTime OccurredOnUtc { get; init; }

    /// <summary>Human-readable user display name, when attributed.</summary>
    public string? UserDisplay { get; init; }

    /// <summary>Correlation id captured with the change, when present.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Field-level changes extracted from the row's diff.</summary>
    public IReadOnlyList<FieldChange> Changes { get; init; } = Array.Empty<FieldChange>();
}

/// <summary>
/// Turns <see cref="AuditLog"/> rows into <see cref="AuditEntryView"/>s. Pure — depends only on
/// <c>System.Text.Json</c>; works type-agnostically against the RFC 6902 diff (JSON-path based).
/// </summary>
public static class AuditViewRenderer
{
    /// <summary>Renders one audit row into a readable view.</summary>
    public static AuditEntryView Render(AuditLog row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new AuditEntryView
        {
            Id = row.Id,
            Action = row.Action,
            OccurredOnUtc = row.OccurredOnUtc,
            UserDisplay = row.UserDisplay,
            CorrelationId = row.CorrelationId,
            Changes = ParseChanges(row.Diff),
        };
    }

    /// <summary>Renders many audit rows, ordered chronologically by <see cref="AuditLog.OccurredOnUtc"/>.</summary>
    public static IReadOnlyList<AuditEntryView> RenderMany(IEnumerable<AuditLog> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.OrderBy(r => r.OccurredOnUtc).Select(Render).ToList();
    }

    private static IReadOnlyList<FieldChange> ParseChanges(string diff)
    {
        if (string.IsNullOrEmpty(diff) || diff == "[]")
        {
            return Array.Empty<FieldChange>();
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(diff);
        }
        catch (JsonException)
        {
            return Array.Empty<FieldChange>();
        }

        if (node is not JsonArray ops)
        {
            return Array.Empty<FieldChange>();
        }

        var changes = new List<FieldChange>(ops.Count);
        foreach (var op in ops)
        {
            if (op is not JsonObject obj)
            {
                continue;
            }
            var opName = obj["op"]?.GetValue<string>();
            var path = obj["path"]?.GetValue<string>();
            if (path is null || opName is null)
            {
                continue;
            }
            // Unwrap scalar string values so the consumer sees "v2" rather than "\"v2\"".
            var valueNode = obj["value"];
            var value = valueNode is JsonValue jv && jv.TryGetValue<string>(out var s)
                ? s
                : valueNode?.ToJsonString();
            changes.Add(opName switch
            {
                "add" => new FieldChange { PropertyPath = path, NewValue = value, ChangeKind = ChangeKind.Added },
                "remove" => new FieldChange { PropertyPath = path, OldValue = null, ChangeKind = ChangeKind.Removed },
                "replace" => new FieldChange { PropertyPath = path, NewValue = value, ChangeKind = ChangeKind.Modified },
                _ => new FieldChange { PropertyPath = path, NewValue = value, ChangeKind = ChangeKind.Modified },
            });
        }
        return changes;
    }
}
