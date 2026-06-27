using System.Text.Json;

namespace Moongazing.OrionAudit.Store;

/// <summary>
/// Serializes a single <see cref="AuditLog"/> row to its NDJSON line form: one compact JSON object,
/// no trailing newline (the caller frames the lines). Hand-rolled over <see cref="Utf8JsonWriter"/> so
/// it is reflection-free and Native-AOT clean, and so the row's <see cref="AuditLog.Diff"/> and
/// <see cref="AuditLog.Snapshot"/> columns - which are already JSON text - are embedded as <em>raw
/// JSON</em> rather than re-escaped into a string, keeping the exported line directly machine-readable
/// by a warehouse / SIEM.
/// </summary>
public static class AuditNdjsonWriter
{
    // Property names are fixed (stable wire contract for downstream consumers). Diff/Snapshot are
    // written raw when they hold valid JSON, and as a JSON string fallback if a row somehow carries
    // non-JSON text in those columns, so a single malformed historical row never aborts the export.
    /// <summary>
    /// Writes <paramref name="row"/> as one compact JSON object to <paramref name="writer"/>. Does not
    /// write a newline or flush; the streaming caller handles framing and flushing per page.
    /// </summary>
    public static void WriteRow(Utf8JsonWriter writer, AuditLog row)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(row);

        writer.WriteStartObject();
        writer.WriteString("id", row.Id);
        writer.WriteString("entityType", row.EntityType);
        WriteOptionalString(writer, "entityBaseType", row.EntityBaseType);
        writer.WriteString("entityId", row.EntityId);
        writer.WriteString("action", row.Action.ToString());
        // Round-trip ("O") UTC instant so downstream parsing is unambiguous and culture-independent.
        writer.WriteString("occurredOnUtc", row.OccurredOnUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        WriteOptionalString(writer, "userId", row.UserId);
        WriteOptionalString(writer, "userDisplay", row.UserDisplay);
        WriteOptionalString(writer, "userType", row.UserType);
        WriteOptionalString(writer, "tenantId", row.TenantId);
        WriteOptionalString(writer, "correlationId", row.CorrelationId);
        WriteRawJsonOrString(writer, "diff", row.Diff);
        WriteRawJsonOrString(writer, "snapshot", row.Snapshot);
        WriteOptionalString(writer, "error", row.Error);
        WriteOptionalString(writer, "entryHash", row.EntryHash);
        WriteOptionalString(writer, "previousHash", row.PreviousHash);
        if (row.HashKeyId is { } keyId)
        {
            writer.WriteNumber("hashKeyId", keyId);
        }
        else
        {
            writer.WriteNull("hashKeyId");
        }
        writer.WriteEndObject();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    // Embeds JSON-column text raw (so the exported diff/snapshot is real JSON, not a quoted string). A
    // null column becomes JSON null. If the column holds text that does not parse as JSON (a corrupt or
    // legacy row), fall back to writing it as a JSON string so the export stays valid NDJSON rather than
    // emitting a syntactically broken line.
    private static void WriteRawJsonOrString(Utf8JsonWriter writer, string name, string? value)
    {
        writer.WritePropertyName(name);
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        if (IsValidJson(value))
        {
            writer.WriteRawValue(value, skipInputValidation: true);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    private static bool IsValidJson(string value)
    {
        try
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(value));
            return JsonDocument.TryParseValue(ref reader, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
