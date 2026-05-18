using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrionAudit.Configuration;

namespace OrionAudit.Capture;

/// <summary>
/// Builds JSON snapshots of audited entity state, applying field-level rules
/// (<see cref="AuditFieldRule.Exclude"/>, <see cref="AuditFieldRule.Hash"/>,
/// <see cref="AuditFieldRule.Redact"/>) from the supplied configuration.
/// </summary>
public static class SnapshotBuilder
{
    /// <summary>Marker value substituted for properties marked with <see cref="RedactedAuditAttribute"/>.</summary>
    public const string RedactedMarker = "<redacted>";

    /// <summary>
    /// Produces a <see cref="JsonObject"/> snapshot of the supplied property values, applying any
    /// configured field rules for <paramref name="entityType"/>.
    /// </summary>
    public static JsonObject Build(
        Type entityType,
        IReadOnlyDictionary<string, object?> propertyValues,
        IAuditConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(propertyValues);
        ArgumentNullException.ThrowIfNull(configuration);

        var typeConfig = configuration.GetConfig(entityType);
        var snapshot = new JsonObject();

        foreach (var (propName, rawValue) in propertyValues)
        {
            var rule = typeConfig?.FieldRule(propName) ?? AuditFieldRule.Capture;
            switch (rule)
            {
                case AuditFieldRule.Exclude:
                    continue;
                case AuditFieldRule.Redact:
                    snapshot[propName] = RedactedMarker;
                    break;
                case AuditFieldRule.Hash:
                    snapshot[propName] = HashValue(rawValue);
                    break;
                case AuditFieldRule.Capture:
                default:
                    snapshot[propName] = ConvertToNode(rawValue);
                    break;
            }
        }

        return snapshot;
    }

    private static string? HashValue(object? value)
    {
        if (value is null)
        {
            return null;
        }
        var text = value as string ?? JsonSerializer.Serialize(value);
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(hash);
#else
        return Convert.ToHexString(hash).ToLowerInvariant();
#endif
    }

    private static JsonNode? ConvertToNode(object? value)
    {
        if (value is null)
        {
            return null;
        }
        return JsonSerializer.SerializeToNode(value, value.GetType());
    }
}
