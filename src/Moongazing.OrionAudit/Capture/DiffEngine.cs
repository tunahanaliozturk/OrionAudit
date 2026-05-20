using System.Text.Json;
using System.Text.Json.Nodes;

namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// Computes and applies RFC 6902 JSON Patches between entity snapshots using OrionAudit's
/// in-house, reflection-free <see cref="Json6902"/> engine. Patches are serialized as JSON
/// strings for persistence in the <see cref="AuditLog.Diff"/> column.
/// </summary>
public static class DiffEngine
{
    /// <summary>
    /// Computes a JSON Patch (RFC 6902) that transforms <paramref name="before"/> into
    /// <paramref name="after"/>. Returns <c>"[]"</c> when the snapshots are equal.
    /// </summary>
    public static string Compute(JsonObject before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        return Json6902.Compute(before, after).ToJsonString();
    }

    /// <summary>
    /// Applies a JSON Patch produced by <see cref="Compute"/> onto a copy of
    /// <paramref name="target"/>, returning the result as a new <see cref="JsonObject"/>.
    /// Throws if the patch is malformed or inapplicable.
    /// </summary>
    public static JsonObject Apply(JsonObject target, string patchJson)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(patchJson);

        JsonNode? patchNode;
        try
        {
            patchNode = JsonNode.Parse(patchJson);
        }
        catch (JsonException ex)
        {
            throw new OrionAuditException($"JSON Patch is not valid JSON: {ex.Message}", ex);
        }

        if (patchNode is not JsonArray patch)
        {
            throw new OrionAuditException("JSON Patch must be a JSON array.");
        }

        var result = Json6902.Apply(target, patch);
        return result as JsonObject
            ?? throw new OrionAuditException("JSON Patch apply did not yield a JSON object.");
    }
}
