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

    /// <summary>
    /// Optional consumer-friendly display label for the property (v0.7.3). Populated by the
    /// <see cref="AuditViewRenderer"/> overloads that accept an <see cref="Configuration.IAuditConfiguration"/>;
    /// the default <c>Render</c> overload leaves it null. Falls back to <see cref="PropertyPath"/>
    /// when no label is configured, so the viewer can always show something readable.
    /// </summary>
    public string? DisplayLabel { get; init; }
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

    /// <summary>
    /// Consumer-registered custom columns (from <c>OrionAuditOptions.AddColumn</c>), projected
    /// from the underlying <see cref="AuditLog"/> row's shadow properties. Empty when no
    /// columns are configured.
    /// </summary>
    public IReadOnlyDictionary<string, object?> CustomColumns { get; init; }
        = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Optional consumer-friendly display label for the entity type (v0.7.3). Populated when
    /// the renderer receives an <see cref="Configuration.IAuditConfiguration"/> with a
    /// declared label; null otherwise so the viewer falls back to the CLR type name.
    /// </summary>
    public string? EntityDisplayLabel { get; init; }
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

    /// <summary>Renders one audit row, attaching the supplied custom-column values.</summary>
    public static AuditEntryView Render(AuditLog row, IReadOnlyDictionary<string, object?> customColumns)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(customColumns);
        return new AuditEntryView
        {
            Id = row.Id,
            Action = row.Action,
            OccurredOnUtc = row.OccurredOnUtc,
            UserDisplay = row.UserDisplay,
            CorrelationId = row.CorrelationId,
            Changes = ParseChanges(row.Diff),
            CustomColumns = customColumns,
        };
    }

    /// <summary>Renders many audit rows, ordered chronologically by <see cref="AuditLog.OccurredOnUtc"/>.</summary>
    public static IReadOnlyList<AuditEntryView> RenderMany(IEnumerable<AuditLog> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.OrderBy(r => r.OccurredOnUtc).Select(Render).ToList();
    }

    /// <summary>
    /// Renders one audit row and decorates the view with consumer-friendly labels read from
    /// the supplied <paramref name="configuration"/> (v0.7.3). Per-property labels surface as
    /// <see cref="FieldChange.DisplayLabel"/>; the entity-level label surfaces as
    /// <see cref="AuditEntryView.EntityDisplayLabel"/>. Resolution: the renderer looks up
    /// the configured <see cref="System.Type"/> by <see cref="AuditLog.EntityType"/>'s assembly-qualified
    /// name, then asks the <see cref="Configuration.AuditableTypeConfig"/> for labels. When the
    /// type cannot be resolved (legacy row from another assembly, AQN missing) labels fall
    /// back to null and the viewer surfaces the raw property path / type name.
    /// </summary>
    public static AuditEntryView Render(AuditLog row, Configuration.IAuditConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(configuration);

        var typeConfig = TryGetTypeConfig(row, configuration);
        return new AuditEntryView
        {
            Id = row.Id,
            Action = row.Action,
            OccurredOnUtc = row.OccurredOnUtc,
            UserDisplay = row.UserDisplay,
            CorrelationId = row.CorrelationId,
            EntityDisplayLabel = typeConfig?.EntityLabel,
            Changes = ParseChanges(row.Diff, typeConfig),
        };
    }

    /// <summary>
    /// Renders one audit row with consumer-friendly labels (see the
    /// <see cref="Render(AuditLog, Configuration.IAuditConfiguration)"/> overload) AND attached
    /// custom-column values.
    /// </summary>
    public static AuditEntryView Render(
        AuditLog row,
        Configuration.IAuditConfiguration configuration,
        IReadOnlyDictionary<string, object?> customColumns)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(customColumns);

        var typeConfig = TryGetTypeConfig(row, configuration);
        return new AuditEntryView
        {
            Id = row.Id,
            Action = row.Action,
            OccurredOnUtc = row.OccurredOnUtc,
            UserDisplay = row.UserDisplay,
            CorrelationId = row.CorrelationId,
            EntityDisplayLabel = typeConfig?.EntityLabel,
            Changes = ParseChanges(row.Diff, typeConfig),
            CustomColumns = customColumns,
        };
    }

    private static Configuration.AuditableTypeConfig? TryGetTypeConfig(
        AuditLog row, Configuration.IAuditConfiguration configuration)
    {
        if (string.IsNullOrEmpty(row.EntityType))
        {
            return null;
        }
        var type = Type.GetType(row.EntityType, throwOnError: false);
        return type is null ? null : configuration.GetConfig(type);
    }

    private static IReadOnlyList<FieldChange> ParseChanges(string diff)
        => ParseChanges(diff, typeConfig: null);

    private static IReadOnlyList<FieldChange> ParseChanges(string diff, Configuration.AuditableTypeConfig? typeConfig)
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
            // v0.7.3 label lookup: derive the top-level property name from the JSON Pointer
            // (skip the leading '/', take everything up to the next '/'). Nested properties
            // inherit their root's label so a change to /Address/Street labels as 'Address'.
            string? displayLabel = null;
            if (typeConfig is not null && path.Length > 1 && path[0] == '/')
            {
                var slash = path.IndexOf('/', 1);
                var propName = slash < 0 ? path[1..] : path[1..slash];
                displayLabel = typeConfig.FieldLabel(propName);
            }
            changes.Add(opName switch
            {
                "add" => new FieldChange { PropertyPath = path, NewValue = value, ChangeKind = ChangeKind.Added, DisplayLabel = displayLabel },
                "remove" => new FieldChange { PropertyPath = path, OldValue = null, ChangeKind = ChangeKind.Removed, DisplayLabel = displayLabel },
                "replace" => new FieldChange { PropertyPath = path, NewValue = value, ChangeKind = ChangeKind.Modified, DisplayLabel = displayLabel },
                _ => new FieldChange { PropertyPath = path, NewValue = value, ChangeKind = ChangeKind.Modified, DisplayLabel = displayLabel },
            });
        }
        return changes;
    }
}
