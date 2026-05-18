using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;

namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// Computes and applies RFC 6902 JSON Patches between entity snapshots using the
/// <c>JsonPatch.Net</c> library. Patches are serialized as JSON strings for persistence in the
/// <see cref="AuditLog.Diff"/> column.
/// </summary>
public static class DiffEngine
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Computes a JSON Patch (RFC 6902) that transforms <paramref name="before"/> into
    /// <paramref name="after"/>. Returns <c>"[]"</c> when the snapshots are equal.
    /// </summary>
    public static string Compute(JsonObject before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var patch = before.CreatePatch(after);
        return JsonSerializer.Serialize(patch, SerializerOptions);
    }

    /// <summary>
    /// Applies a JSON Patch produced by <see cref="Compute"/> onto a copy of <paramref name="target"/>,
    /// returning the result as a new <see cref="JsonObject"/>. Throws if the patch is malformed or
    /// inapplicable.
    /// </summary>
    public static JsonObject Apply(JsonObject target, string patchJson)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(patchJson);

        var patch = JsonSerializer.Deserialize<JsonPatch>(patchJson, SerializerOptions)
            ?? throw new OrionAuditException("JSON Patch deserialization returned null.");
        var clone = target.DeepClone()!.AsObject();
        var result = patch.Apply(clone);
        if (!result.IsSuccess)
        {
            throw new OrionAuditException($"JSON Patch apply failed: {result.Error}");
        }
        return result.Result!.AsObject();
    }
}
