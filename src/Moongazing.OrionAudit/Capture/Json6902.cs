using System.Globalization;
using System.Text.Json.Nodes;

namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// In-house RFC 6902 (JSON Patch) compute/apply engine built only on
/// <see cref="System.Text.Json.Nodes"/>. Reflection-free and Native-AOT clean.
/// <see cref="Compute"/> emits only <c>add</c>/<c>remove</c>/<c>replace</c>;
/// <see cref="Apply"/> supports all six RFC 6902 operations so historical patches
/// (which may carry <c>move</c>/<c>copy</c>/<c>test</c>) still replay.
/// </summary>
internal static class Json6902
{
    // ---- Compute ----------------------------------------------------------

    /// <summary>
    /// Computes an RFC 6902 patch transforming <paramref name="before"/> into
    /// <paramref name="after"/>. Returns an empty array when the nodes are deep-equal.
    /// </summary>
    public static JsonArray Compute(JsonNode? before, JsonNode? after)
    {
        var ops = new JsonArray();
        Diff(string.Empty, before, after, ops);
        return ops;
    }

    private static void Diff(string path, JsonNode? before, JsonNode? after, JsonArray ops)
    {
        // Containers are diffed structurally rather than short-circuited on a full deep-equal:
        // DiffObject/DiffArray emit no ops when the children are unchanged, so an upfront
        // NodesEqual would only re-walk the entire subtree that the structural pass walks
        // anyway. The deep-equal guard is kept solely on the leaf path below, where it actually
        // suppresses a spurious replace for two equal scalars. This removes one full recursive
        // deep-equal pass per container node on every audited change.
        if (before is JsonObject beforeObj && after is JsonObject afterObj)
        {
            DiffObject(path, beforeObj, afterObj, ops);
            return;
        }

        if (before is JsonArray beforeArr && after is JsonArray afterArr)
        {
            DiffArray(path, beforeArr, afterArr, ops);
            return;
        }

        // Leaf (or kind-mismatch) comparison: only here is a deep-equal meaningful.
        if (NodesEqual(before, after))
        {
            return;
        }

        ops.Add(Op("replace", path, after, withValue: true));
    }

    private static void DiffObject(string path, JsonObject before, JsonObject after, JsonArray ops)
    {
        foreach (var pair in before)
        {
            if (!after.ContainsKey(pair.Key))
            {
                ops.Add(Op("remove", path + "/" + EscapeToken(pair.Key), value: null, withValue: false));
            }
        }

        foreach (var (key, afterValue) in after)
        {
            var childPath = path + "/" + EscapeToken(key);
            if (before.TryGetPropertyValue(key, out var beforeValue))
            {
                Diff(childPath, beforeValue, afterValue, ops);
            }
            else
            {
                ops.Add(Op("add", childPath, afterValue, withValue: true));
            }
        }
    }

    private static void DiffArray(string path, JsonArray before, JsonArray after, JsonArray ops)
    {
        var shared = Math.Min(before.Count, after.Count);

        for (var i = 0; i < shared; i++)
        {
            Diff(path + "/" + i.ToString(CultureInfo.InvariantCulture), before[i], after[i], ops);
        }

        // after is longer -> append the tail in ascending order.
        for (var i = shared; i < after.Count; i++)
        {
            ops.Add(Op("add", path + "/" + i.ToString(CultureInfo.InvariantCulture), after[i], withValue: true));
        }

        // before is longer -> remove the tail, highest index first so lower indices stay valid.
        for (var i = before.Count - 1; i >= shared; i--)
        {
            ops.Add(Op("remove", path + "/" + i.ToString(CultureInfo.InvariantCulture), value: null, withValue: false));
        }
    }

#pragma warning disable CA1859 // Return type is intentionally JsonNode (not JsonObject) so JsonArray.Add uses the non-generic overload — required for Native-AOT trim safety.
    private static JsonNode Op(string op, string path, JsonNode? value, bool withValue)
    {
        var node = new JsonObject
        {
            ["op"] = op,
            ["path"] = path,
        };
        if (withValue)
        {
            node["value"] = value?.DeepClone();
        }
        return node;
    }
#pragma warning restore CA1859

    // ---- Apply ------------------------------------------------------------

    /// <summary>
    /// Applies <paramref name="patch"/> onto a deep copy of <paramref name="target"/> and
    /// returns the result. Throws <see cref="OrionAuditException"/> on a malformed or
    /// inapplicable patch.
    /// </summary>
    public static JsonNode Apply(JsonNode target, JsonArray patch)
    {
        var current = target.DeepClone();
        foreach (var entry in patch)
        {
            if (entry is not JsonObject op)
            {
                throw new OrionAuditException("JSON Patch operation must be a JSON object.");
            }
            current = ApplyOne(current, op);
        }
        return current;
    }

    private static JsonNode ApplyOne(JsonNode root, JsonObject op)
    {
        var name = op["op"]?.GetValue<string>()
            ?? throw new OrionAuditException("JSON Patch operation is missing 'op'.");
        var path = op["path"]?.GetValue<string>()
            ?? throw new OrionAuditException("JSON Patch operation is missing 'path'.");

        switch (name)
        {
            case "add":
                return ApplyAdd(root, path, RequireValue(op));
            case "remove":
                ApplyRemove(root, path);
                return root;
            case "replace":
                return ApplyReplace(root, path, RequireValue(op));
            case "move":
            {
                var detached = Detach(root, RequireFrom(op));
                return ApplyAdd(root, path, detached);
            }
            case "copy":
            {
                var source = Resolve(root, RequireFrom(op));
                return ApplyAdd(root, path, source);
            }
            case "test":
            {
                var actual = path.Length == 0 ? root : Resolve(root, path);
                if (!NodesEqual(actual, op["value"]))
                {
                    throw new OrionAuditException($"JSON Patch 'test' failed at '{path}'.");
                }
                return root;
            }
            default:
                throw new OrionAuditException($"Unsupported JSON Patch op: '{name}'.");
        }
    }

    private static JsonNode ApplyAdd(JsonNode root, string path, JsonNode? value)
    {
        if (path.Length == 0)
        {
            return value?.DeepClone()
                ?? throw new OrionAuditException("JSON Patch 'add' to the document root requires a value.");
        }

        var (parent, token) = ResolveParent(root, path);
        switch (parent)
        {
            case JsonObject obj:
                obj[token] = value?.DeepClone();
                break;
            case JsonArray arr:
                var index = token == "-" ? arr.Count : ParseIndex(token, arr.Count, allowEnd: true);
                arr.Insert(index, value?.DeepClone());
                break;
            default:
                throw new OrionAuditException($"JSON Patch 'add' parent is not a container: '{path}'.");
        }
        return root;
    }

    private static void ApplyRemove(JsonNode root, string path)
    {
        var (parent, token) = ResolveParent(root, path);
        switch (parent)
        {
            case JsonObject obj:
                if (!obj.Remove(token))
                {
                    throw new OrionAuditException($"JSON Patch 'remove' target not found: '{path}'.");
                }
                break;
            case JsonArray arr:
                arr.RemoveAt(ParseIndex(token, arr.Count, allowEnd: false));
                break;
            default:
                throw new OrionAuditException($"JSON Patch 'remove' parent is not a container: '{path}'.");
        }
    }

    private static JsonNode ApplyReplace(JsonNode root, string path, JsonNode? value)
    {
        if (path.Length == 0)
        {
            return value?.DeepClone()
                ?? throw new OrionAuditException("JSON Patch 'replace' of the document root requires a value.");
        }

        var (parent, token) = ResolveParent(root, path);
        switch (parent)
        {
            case JsonObject obj:
                if (!obj.ContainsKey(token))
                {
                    throw new OrionAuditException($"JSON Patch 'replace' target not found: '{path}'.");
                }
                obj[token] = value?.DeepClone();
                break;
            case JsonArray arr:
                arr[ParseIndex(token, arr.Count, allowEnd: false)] = value?.DeepClone();
                break;
            default:
                throw new OrionAuditException($"JSON Patch 'replace' parent is not a container: '{path}'.");
        }
        return root;
    }

    private static JsonNode? Detach(JsonNode root, string pointer)
    {
        var (parent, token) = ResolveParent(root, pointer);
        switch (parent)
        {
            case JsonObject obj:
                if (!obj.TryGetPropertyValue(token, out var objValue))
                {
                    throw new OrionAuditException($"JSON Patch 'from' not found: '{pointer}'.");
                }
                obj.Remove(token);
                return objValue;
            case JsonArray arr:
                var index = ParseIndex(token, arr.Count, allowEnd: false);
                var arrValue = arr[index];
                arr.RemoveAt(index);
                return arrValue;
            default:
                throw new OrionAuditException($"JSON Patch 'from' parent is not a container: '{pointer}'.");
        }
    }

    private static JsonNode? RequireValue(JsonObject op)
    {
        if (!op.ContainsKey("value"))
        {
            throw new OrionAuditException("JSON Patch operation is missing 'value'.");
        }
        return op["value"];
    }

    private static string RequireFrom(JsonObject op) =>
        op["from"]?.GetValue<string>()
        ?? throw new OrionAuditException("JSON Patch operation is missing 'from'.");

    // ---- JSON Pointer (RFC 6901) -----------------------------------------

    private static string[] ParsePointer(string pointer)
    {
        if (pointer.Length == 0)
        {
            return Array.Empty<string>();
        }
        if (pointer[0] != '/')
        {
            throw new OrionAuditException($"Invalid JSON Pointer: '{pointer}'.");
        }

        var tokens = pointer.Substring(1).Split('/');
        for (var i = 0; i < tokens.Length; i++)
        {
            tokens[i] = UnescapeToken(tokens[i]);
        }
        return tokens;
    }

    private static JsonNode? Resolve(JsonNode root, string pointer)
    {
        JsonNode? node = root;
        foreach (var token in ParsePointer(pointer))
        {
            node = node switch
            {
                JsonObject obj when obj.TryGetPropertyValue(token, out var child) => child,
                JsonArray arr => arr[ParseIndex(token, arr.Count, allowEnd: false)],
                _ => throw new OrionAuditException($"JSON Pointer path not found: '{pointer}'."),
            };
        }
        return node;
    }

    private static (JsonNode Parent, string Token) ResolveParent(JsonNode root, string pointer)
    {
        var tokens = ParsePointer(pointer);
        if (tokens.Length == 0)
        {
            throw new OrionAuditException("JSON Patch operation cannot target the document root here.");
        }

        var node = root;
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            node = node switch
            {
                JsonObject obj when obj.TryGetPropertyValue(tokens[i], out var child) && child is not null => child,
                JsonArray arr => arr[ParseIndex(tokens[i], arr.Count, allowEnd: false)]
                    ?? throw new OrionAuditException($"JSON Pointer path not found: '{pointer}'."),
                _ => throw new OrionAuditException($"JSON Pointer path not found: '{pointer}'."),
            };
        }
        return (node, tokens[^1]);
    }

    private static int ParseIndex(string token, int count, bool allowEnd)
    {
        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            throw new OrionAuditException($"Invalid JSON Pointer array index: '{token}'.");
        }

        var upperBound = allowEnd ? count : count - 1;
        if (index < 0 || index > upperBound)
        {
            throw new OrionAuditException($"JSON Pointer array index out of range: '{token}'.");
        }
        return index;
    }

    private static string EscapeToken(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string UnescapeToken(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    // ---- Deep equality ----------------------------------------------------

    private static bool NodesEqual(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);
}
