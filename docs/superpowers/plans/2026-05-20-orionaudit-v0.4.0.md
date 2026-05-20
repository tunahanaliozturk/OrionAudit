# OrionAudit v0.4.0 — AOT-Clean Diff Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the `JsonPatch.Net` dependency with an in-house, reflection-free RFC 6902 diff engine, restore the Native AOT CI gate, and release OrionAudit v0.4.0.

**Architecture:** A new `internal static class Json6902` implements RFC 6902 compute/apply over `System.Text.Json.Nodes` (zero reflection). The public `DiffEngine` class keeps its exact signatures and becomes a thin facade over `Json6902`. The persisted patch format stays RFC 6902 JSON, so existing `AuditLog.Diff` rows replay unchanged. `Compute` emits only `add`/`remove`/`replace`; `Apply` supports all six operations so historical `JsonPatch.Net`-format diffs (which can contain `move`/`copy`/`test`) still replay.

**Tech Stack:** .NET (net8.0/net9.0/net10.0), `System.Text.Json.Nodes`, xUnit, GitHub Actions, Native AOT (ILC).

**Spec:** `docs/superpowers/specs/2026-05-20-orionaudit-v0.4.0-design.md`

---

## File Structure

| File | Responsibility | Change |
| ---- | -------------- | ------ |
| `src/Moongazing.OrionAudit/Capture/Json6902.cs` | RFC 6902 compute/apply engine, reflection-free | Create |
| `src/Moongazing.OrionAudit/Capture/DiffEngine.cs` | Public facade; serialises patches to/from JSON strings | Modify |
| `src/Moongazing.OrionAudit/Moongazing.OrionAudit.csproj` | Drop `JsonPatch.Net` package reference | Modify |
| `tests/Moongazing.OrionAudit.Tests/DiffEngineTests.cs` | Behaviour + backward-compat tests | Modify |
| `aot/Moongazing.OrionAudit.AotProbe/Program.cs` | Native AOT probe — exercises reflection-free surface | Create (restore) |
| `aot/Moongazing.OrionAudit.AotProbe/Moongazing.OrionAudit.AotProbe.csproj` | AOT probe project | Create (restore) |
| `OrionAudit.sln` | Register the AOT probe project | Modify |
| `.github/workflows/ci-cd.yml` | Restore `aot-publish-check` job | Modify |
| `src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs` | Telemetry version → `0.4.0` | Modify |
| `Directory.Build.props` | `<Version>` → `0.4.0` | Modify |
| `CHANGELOG.md` / `ROADMAP.md` / `README.md` | Release docs | Modify |

---

## Task 1: Characterization tests for the diff engine

Lock the *current* (`JsonPatch.Net`-backed) `DiffEngine` behaviour with a broader test suite **before** swapping the implementation. These tests pass against the current engine and must keep passing against `Json6902` — they are the regression net for Task 3.

**Files:**
- Modify: `tests/Moongazing.OrionAudit.Tests/DiffEngineTests.cs`

- [ ] **Step 1: Add the new tests**

Append these tests to the `DiffEngineTests` class in `tests/Moongazing.OrionAudit.Tests/DiffEngineTests.cs` (keep the five existing tests as-is). Ensure the file's `using` block has `using System.Text.Json.Nodes;` (it already does).

```csharp
    [Fact]
    public void Compute_NestedObjectChange_ProducesScopedReplace()
    {
        var before = new JsonObject { ["Address"] = new JsonObject { ["City"] = "Ankara" } };
        var after = new JsonObject { ["Address"] = new JsonObject { ["City"] = "Istanbul" } };

        var diff = DiffEngine.Compute(before, after);
        var result = DiffEngine.Apply(before, diff);

        Assert.Contains("/Address/City", diff);
        Assert.Equal("Istanbul", result["Address"]!["City"]!.GetValue<string>());
    }

    [Fact]
    public void ComputeThenApply_ArrayGrows_RoundTrips()
    {
        var before = new JsonObject { ["Tags"] = new JsonArray("a") };
        var after = new JsonObject { ["Tags"] = new JsonArray("a", "b", "c") };

        var result = DiffEngine.Apply(before, DiffEngine.Compute(before, after));

        Assert.Equal(3, result["Tags"]!.AsArray().Count);
        Assert.Equal("c", result["Tags"]![2]!.GetValue<string>());
    }

    [Fact]
    public void ComputeThenApply_ArrayShrinks_RoundTrips()
    {
        var before = new JsonObject { ["Tags"] = new JsonArray("a", "b", "c") };
        var after = new JsonObject { ["Tags"] = new JsonArray("a") };

        var result = DiffEngine.Apply(before, DiffEngine.Compute(before, after));

        Assert.Equal(1, result["Tags"]!.AsArray().Count);
        Assert.Equal("a", result["Tags"]![0]!.GetValue<string>());
    }

    [Fact]
    public void ComputeThenApply_ArrayElementChanges_RoundTrips()
    {
        var before = new JsonObject { ["Tags"] = new JsonArray("a", "b") };
        var after = new JsonObject { ["Tags"] = new JsonArray("a", "z") };

        var result = DiffEngine.Apply(before, DiffEngine.Compute(before, after));

        Assert.Equal("z", result["Tags"]![1]!.GetValue<string>());
    }

    [Fact]
    public void ComputeThenApply_ValueTypeChanges_RoundTrips()
    {
        var before = new JsonObject { ["Field"] = "text" };
        var after = new JsonObject { ["Field"] = 42 };

        var result = DiffEngine.Apply(before, DiffEngine.Compute(before, after));

        Assert.Equal(42, result["Field"]!.GetValue<int>());
    }

    [Fact]
    public void ComputeThenApply_NullValue_RoundTrips()
    {
        var before = new JsonObject { ["Field"] = "text" };
        var after = new JsonObject { ["Field"] = null };

        var result = DiffEngine.Apply(before, DiffEngine.Compute(before, after));

        Assert.True(result.ContainsKey("Field"));
        Assert.Null(result["Field"]);
    }

    [Fact]
    public void ComputeThenApply_PointerEscapedKeys_RoundTrip()
    {
        var before = new JsonObject();
        var after = new JsonObject { ["a/b"] = 1, ["m~n"] = 2 };

        var result = DiffEngine.Apply(before, DiffEngine.Compute(before, after));

        Assert.Equal(1, result["a/b"]!.GetValue<int>());
        Assert.Equal(2, result["m~n"]!.GetValue<int>());
    }

    [Fact]
    public void Apply_HistoricalPatchWithMoveCopyTest_Replays()
    {
        // A hand-authored patch in the JsonPatch.Net-era format: move/copy/test ops
        // never emitted by the new Compute, but Apply must still replay them.
        var target = new JsonObject { ["a"] = 1 };
        var patch =
            "[{\"op\":\"add\",\"path\":\"/b\",\"value\":2}," +
            "{\"op\":\"copy\",\"from\":\"/a\",\"path\":\"/c\"}," +
            "{\"op\":\"move\",\"from\":\"/b\",\"path\":\"/d\"}," +
            "{\"op\":\"test\",\"path\":\"/a\",\"value\":1}," +
            "{\"op\":\"remove\",\"path\":\"/a\"}]";

        var result = DiffEngine.Apply(target, patch);

        Assert.False(result.ContainsKey("a"));
        Assert.False(result.ContainsKey("b"));
        Assert.Equal(1, result["c"]!.GetValue<int>());
        Assert.Equal(2, result["d"]!.GetValue<int>());
    }

    [Fact]
    public void Apply_FailingTestOp_Throws()
    {
        var target = new JsonObject { ["a"] = 1 };
        var patch = "[{\"op\":\"test\",\"path\":\"/a\",\"value\":999}]";

        Assert.Throws<OrionAuditException>(() => DiffEngine.Apply(target, patch));
    }

    [Fact]
    public void Apply_MalformedPatch_Throws()
    {
        var target = new JsonObject { ["a"] = 1 };

        Assert.Throws<OrionAuditException>(() => DiffEngine.Apply(target, "not json"));
    }
```

`OrionAuditException` lives in namespace `Moongazing.OrionAudit`; add `using Moongazing.OrionAudit;` to the test file's `using` block if it is not already resolvable. (`DiffEngineTests` is in `Moongazing.OrionAudit.Tests`, so `Moongazing.OrionAudit` is *not* automatically in scope — add the `using`.)

- [ ] **Step 2: Run the tests to verify they pass against the current engine**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests/Moongazing.OrionAudit.Tests.csproj --filter "FullyQualifiedName~DiffEngineTests"`
Expected: PASS — all tests green (the current `JsonPatch.Net` engine handles every case). This proves the tests describe behaviour the new engine must preserve.

- [ ] **Step 3: Commit**

```bash
git add tests/Moongazing.OrionAudit.Tests/DiffEngineTests.cs
git commit -m "test(diff): characterization tests for diff engine before AOT-clean rewrite"
```

---

## Task 2: Create the `Json6902` engine

Create the reflection-free RFC 6902 engine. It is `internal` and tested through the public `DiffEngine` facade (rewired in Task 3) — so this task only adds the file and confirms the solution still compiles.

**Files:**
- Create: `src/Moongazing.OrionAudit/Capture/Json6902.cs`

- [ ] **Step 1: Create `Json6902.cs`**

Create `src/Moongazing.OrionAudit/Capture/Json6902.cs` with this exact content:

```csharp
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
        if (NodesEqual(before, after))
        {
            return;
        }

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

    private static JsonObject Op(string op, string path, JsonNode? value, bool withValue)
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

    private static bool NodesEqual(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a switch
        {
            JsonObject objA when b is JsonObject objB => ObjectsEqual(objA, objB),
            JsonArray arrA when b is JsonArray arrB => ArraysEqual(arrA, arrB),
            JsonValue valA when b is JsonValue valB => valA.ToJsonString() == valB.ToJsonString(),
            _ => false,
        };
    }

    private static bool ObjectsEqual(JsonObject a, JsonObject b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        foreach (var (key, valueA) in a)
        {
            if (!b.TryGetPropertyValue(key, out var valueB) || !NodesEqual(valueA, valueB))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ArraysEqual(JsonArray a, JsonArray b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (var i = 0; i < a.Count; i++)
        {
            if (!NodesEqual(a[i], b[i]))
            {
                return false;
            }
        }
        return true;
    }
}
```

- [ ] **Step 2: Build to verify the new file compiles**

Run: `dotnet build src/Moongazing.OrionAudit/Moongazing.OrionAudit.csproj --configuration Release`
Expected: PASS — build succeeds with no errors. `Json6902` is not yet referenced; this confirms it compiles cleanly under `TreatWarningsAsErrors`.

- [ ] **Step 3: Commit**

```bash
git add src/Moongazing.OrionAudit/Capture/Json6902.cs
git commit -m "feat(diff): add reflection-free RFC 6902 Json6902 engine"
```

---

## Task 3: Rewire `DiffEngine` onto `Json6902`

Replace `DiffEngine`'s `JsonPatch.Net` internals with delegation to `Json6902`. Public signatures are unchanged. The full test suite is the green checkpoint — if `Json6902` is wrong, tests from Task 1 fail here.

**Files:**
- Modify: `src/Moongazing.OrionAudit/Capture/DiffEngine.cs`

- [ ] **Step 1: Replace the contents of `DiffEngine.cs`**

Overwrite `src/Moongazing.OrionAudit/Capture/DiffEngine.cs` with this exact content:

```csharp
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
```

This drops the `using Json.Patch;` import and the `JsonPatch.Net` calls (`CreatePatch`, `JsonSerializer.Deserialize<JsonPatch>`, `patch.Apply`). The patch format on the wire is unchanged.

- [ ] **Step 2: Run the full diff test suite**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests/Moongazing.OrionAudit.Tests.csproj --filter "FullyQualifiedName~DiffEngineTests"`
Expected: PASS — all 15 tests green (5 original + 10 from Task 1). This is the proof that `Json6902` matches the persisted patch format and replays historical `move`/`copy`/`test` patches.

- [ ] **Step 3: Run the entire solution test suite**

Run: `dotnet test OrionAudit.sln --configuration Release`
Expected: PASS — every test project green. `AuditReconstructor` replays diffs via `DiffEngine.Apply`, so the integration tests exercise the new engine end to end.

- [ ] **Step 4: Commit**

```bash
git add src/Moongazing.OrionAudit/Capture/DiffEngine.cs
git commit -m "feat(diff): rewire DiffEngine onto the in-house Json6902 engine"
```

---

## Task 4: Remove the `JsonPatch.Net` dependency

`DiffEngine` was the only consumer of `JsonPatch.Net` (verified: no other `Json.Patch` references in `src/`). Remove the package reference.

**Files:**
- Modify: `src/Moongazing.OrionAudit/Moongazing.OrionAudit.csproj`

- [ ] **Step 1: Delete the package reference**

In `src/Moongazing.OrionAudit/Moongazing.OrionAudit.csproj`, delete this line from the first `<ItemGroup>`:

```xml
    <PackageReference Include="JsonPatch.Net" Version="3.3.0" />
```

Leave the `<PackageTags>` value untouched — it still contains `json-patch`, which is accurate (the persisted format is RFC 6902 JSON Patch).

- [ ] **Step 2: Restore and build the solution**

Run: `dotnet restore OrionAudit.sln` then `dotnet build OrionAudit.sln --configuration Release`
Expected: PASS — build succeeds. A failure mentioning `Json.Patch` means a stray reference was missed; search `src/` for `Json.Patch` and resolve it.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test OrionAudit.sln --configuration Release`
Expected: PASS — every test project green with `JsonPatch.Net` gone.

- [ ] **Step 4: Commit**

```bash
git add src/Moongazing.OrionAudit/Moongazing.OrionAudit.csproj
git commit -m "build(diff): drop the JsonPatch.Net package dependency"
```

---

## Task 5: Restore the Native AOT probe project

Restore the `aot/Moongazing.OrionAudit.AotProbe` project that commit `658f107` removed. It Native-AOT publishes OrionAudit's reflection-free surface and fails the build on any `IL2*`/`IL3*` warning.

**Files:**
- Create: `aot/Moongazing.OrionAudit.AotProbe/Program.cs`
- Create: `aot/Moongazing.OrionAudit.AotProbe/Moongazing.OrionAudit.AotProbe.csproj`
- Modify: `OrionAudit.sln`

- [ ] **Step 1: Restore the probe files from git history**

Run (PowerShell, from the repo root):

```powershell
New-Item -ItemType Directory -Force aot/Moongazing.OrionAudit.AotProbe | Out-Null
git show 658f107^:aot/Moongazing.OrionAudit.AotProbe/Program.cs | Set-Content -NoNewline aot/Moongazing.OrionAudit.AotProbe/Program.cs
git show "658f107^:aot/Moongazing.OrionAudit.AotProbe/Moongazing.OrionAudit.AotProbe.csproj" | Set-Content -NoNewline aot/Moongazing.OrionAudit.AotProbe/Moongazing.OrionAudit.AotProbe.csproj
```

Expected: both files exist under `aot/Moongazing.OrionAudit.AotProbe/`. Verify with `Get-Content aot/Moongazing.OrionAudit.AotProbe/Program.cs` — it should start with `using System.Text.Json.Nodes;` and the `Moongazing.OrionAudit.AotProbe` csproj should have `<PublishAot>true</PublishAot>`.

- [ ] **Step 2: Register the probe project in the solution**

In `OrionAudit.sln`, add the AOT solution folder and probe project. Immediately **after** these two lines:

```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Moongazing.OrionAudit.Generators", "src\Moongazing.OrionAudit.Generators\Moongazing.OrionAudit.Generators.csproj", "{8BDD7A93-4057-4A24-BEB1-F30A5ED08C49}"
EndProject
```

insert:

```
Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "aot", "aot", "{D3107F82-A6F5-CE3A-5ADD-CE5D91A4D647}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Moongazing.OrionAudit.AotProbe", "aot\Moongazing.OrionAudit.AotProbe\Moongazing.OrionAudit.AotProbe.csproj", "{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F}"
EndProject
```

In the `GlobalSection(ProjectConfigurationPlatforms) = postSolution` block, immediately **after** the last line beginning `{8BDD7A93-4057-4A24-BEB1-F30A5ED08C49}.Release|x86.Build.0`, insert:

```
		{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F}.Debug|x64.ActiveCfg = Debug|Any CPU
		{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F}.Debug|x64.Build.0 = Debug|Any CPU
		{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F}.Debug|x86.ActiveCfg = Debug|Any CPU
		{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F}.Debug|x86.Build.0 = Debug|Any CPU
		{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F}.Release|Any CPU.Build.0 = Release|Any CPU
		{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F}.Release|x64.ActiveCfg = Release|Any CPU
		{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F}.Release|x64.Build.0 = Release|Any CPU
		{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F}.Release|x86.ActiveCfg = Release|Any CPU
		{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F}.Release|x86.Build.0 = Release|Any CPU
```

In the `GlobalSection(NestedProjects) = preSolution` block, immediately **after** the line `{8BDD7A93-4057-4A24-BEB1-F30A5ED08C49} = {827E0CD3-B72D-47B6-A68D-7590B98EB39B}`, insert:

```
		{C4BE8E05-476A-4A88-8C25-5A2987F7AE5F} = {D3107F82-A6F5-CE3A-5ADD-CE5D91A4D647}
```

- [ ] **Step 3: Build the solution including the probe**

Run: `dotnet build OrionAudit.sln --configuration Release`
Expected: PASS — the probe project compiles as part of the solution.

- [ ] **Step 4: Native-AOT publish the probe locally (if the toolchain is available)**

Run: `dotnet publish aot/Moongazing.OrionAudit.AotProbe -c Release -r win-x64`
(On a Linux host use `-r linux-x64` and ensure `clang` + `zlib1g-dev` are installed.)
Expected: PASS — publish completes with no `IL2*`/`IL3*` warnings. The probe project sets `TreatWarningsAsErrors=true`, so any trim/AOT warning fails the publish. If the local machine has no Native AOT toolchain (C++ build tools / `clang`), skip this step — CI Task 6 is the authoritative gate — and note the skip in the commit body.

- [ ] **Step 5: Commit**

```bash
git add aot/Moongazing.OrionAudit.AotProbe/Program.cs aot/Moongazing.OrionAudit.AotProbe/Moongazing.OrionAudit.AotProbe.csproj OrionAudit.sln
git commit -m "build(aot): restore the Native AOT probe project"
```

---

## Task 6: Restore the `aot-publish-check` CI job

Bring back the CI job that Native-AOT publishes the probe and gates `publish` behind it.

**Files:**
- Modify: `.github/workflows/ci-cd.yml`

- [ ] **Step 1: Add the `aot-publish-check` job**

In `.github/workflows/ci-cd.yml`, immediately **after** the `build-and-test` job (the line `      - run: dotnet test ${{ env.SOLUTION_PATH }} --no-build --configuration Release`) and **before** the `  publish:` line, insert:

```yaml

  aot-publish-check:
    needs: build-and-test
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: |
            8.0.x
            9.0.x
            10.0.x
      - name: Install Native AOT prerequisites
        run: sudo apt-get update && sudo apt-get install -y clang zlib1g-dev
      - name: Native AOT publish the OrionAudit probe
        # The probe exercises OrionAudit's reflection-free surface (SnapshotBuilder, DiffEngine,
        # the source generator, AuditKey, AuditScope) with PublishAot=true + TreatWarningsAsErrors.
        # Any IL2*/IL3* trim/AOT warning fails the build. EF Core itself is not AOT-compatible,
        # which is why the probe — not the EF-coupled sample — is the AOT target.
        run: dotnet publish aot/Moongazing.OrionAudit.AotProbe -c Release -r linux-x64
```

- [ ] **Step 2: Gate `publish` behind the new job**

In the same file, change the `publish` job's dependency line:

```yaml
  publish:
    needs: build-and-test
```

to:

```yaml
  publish:
    needs: [ build-and-test, aot-publish-check ]
```

- [ ] **Step 3: Validate the workflow YAML**

Run: `dotnet build OrionAudit.sln --configuration Release`
(Build is unaffected by the workflow file; this step just confirms nothing else regressed.) Visually re-read the inserted YAML to confirm 2-space indentation matches the surrounding jobs.
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci-cd.yml
git commit -m "ci(aot): restore aot-publish-check job and gate publish on it"
```

---

## Task 7: Bump version and telemetry to 0.4.0

**Files:**
- Modify: `Directory.Build.props`
- Modify: `src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs`

- [ ] **Step 1: Bump the package version**

In `Directory.Build.props`, change:

```xml
    <Version>0.3.0</Version>
```

to:

```xml
    <Version>0.4.0</Version>
```

- [ ] **Step 2: Bump the telemetry version**

In `src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs`, change both version strings (currently `"0.2.0"` — it was not bumped in v0.3.0):

```csharp
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.2.0");
    internal static readonly Meter Meter = new(MeterName, "0.2.0");
```

to:

```csharp
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.4.0");
    internal static readonly Meter Meter = new(MeterName, "0.4.0");
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build OrionAudit.sln --configuration Release`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs
git commit -m "release: bump version and telemetry to 0.4.0"
```

---

## Task 8: Update documentation

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `ROADMAP.md`
- Modify: `README.md`

- [ ] **Step 1: Add the CHANGELOG entry**

In `CHANGELOG.md`, immediately **after** the line `and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).` and **before** `## [0.3.0] - 2026-05-20`, insert:

```markdown

## [0.4.0] - 2026-05-20

AOT-Clean Diff Engine release. Replaces the `JsonPatch.Net` dependency with an in-house,
reflection-free RFC 6902 engine, making OrionAudit's capture/reconstruct path Native-AOT clean.

### Added

- **`Json6902` engine.** A reflection-free RFC 6902 compute/apply implementation built only on
  `System.Text.Json.Nodes`, with no `[RequiresDynamicCode]` surface. `DiffEngine` is now a thin
  facade over it; its public `Compute` / `Apply` signatures are unchanged.
- **Native AOT CI gate restored.** The `aot/Moongazing.OrionAudit.AotProbe` project and the
  `aot-publish-check` workflow job return. The probe Native-AOT publishes OrionAudit's
  reflection-free surface with `TreatWarningsAsErrors`; any `IL2*` / `IL3*` warning fails the
  build. The `publish` job depends on it again.

### Changed

- `DiffEngine.Compute` / `Apply` no longer depend on `JsonPatch.Net`. `Compute` emits only
  `add` / `remove` / `replace` operations; `Apply` supports all six RFC 6902 operations
  (`add` / `remove` / `replace` / `move` / `copy` / `test`) so historical patches written by
  `JsonPatch.Net` (which can carry `move` / `copy`) still replay.
- `OrionAudit` `ActivitySource` / `Meter` version bumped to `0.4.0`.

### Removed

- **`JsonPatch.Net` package dependency.** OrionAudit no longer pulls in `JsonPatch.Net` or its
  transitive `Json.Pointer` / `Json.More.Net` graph.

### Migration from v0.3.0

- **No code changes required.** Every v0.3.0 API works unchanged; `DiffEngine`'s public surface
  is identical.
- **No schema or data migration.** The persisted `AuditLog.Diff` format is unchanged RFC 6902
  JSON. Existing audit history replays as-is.
```

- [ ] **Step 2: Mark v0.4.0 shipped in the ROADMAP**

In `ROADMAP.md`, change the heading:

```markdown
## v0.4.0 — AOT-Clean Diff Engine *(planned)*
```

to:

```markdown
## v0.4.0 — AOT-Clean Diff Engine *(shipped)*
```

In the same file, in the "Release cadence" table, change the `v0.4.0` row's target-window cell:

```markdown
| v0.4.0    | AOT-clean diff engine               | replace JsonPatch.Net        |
```

to:

```markdown
| v0.4.0    | shipped — AOT-clean diff engine     | replace JsonPatch.Net        |
```

- [ ] **Step 3: Update the README**

In `README.md`, make these four edits:

1. The feature-comparison table row (line ~42) — change:

```markdown
| NativeAOT clean                        |  Planned   |     -     |        -         |      -      |
```

to:

```markdown
| NativeAOT clean                        |    Yes     |     -     |        -         |      -      |
```

2. The "JSON Patch (RFC 6902) diffs" section (line ~114) — change:

```markdown
Diffs are computed via [`JsonPatch.Net`](https://github.com/gregsdennis/json-everything) and
stored in the `Diff` column as compact JSON. They are replayable — that's what makes time-travel
reconstruction possible.
```

to:

```markdown
Diffs are computed by OrionAudit's in-house, reflection-free RFC 6902 engine and stored in the
`Diff` column as compact JSON. They are replayable — that's what makes time-travel
reconstruction possible.
```

3. The source-generator note (line ~258-261) — change:

```markdown
The generator ships *inside* the `OrionAudit` NuGet (`analyzers/dotnet/cs/`) — no extra
package to install. The reflective `ScanAssembly` path still works and now carries
`[RequiresUnreferencedCode]` so trim/AOT publishes flag it. Full Native AOT is a
[v0.4 goal](ROADMAP.md) — it's blocked on replacing the `JsonPatch.Net` diff dependency.
```

to:

```markdown
The generator ships *inside* the `OrionAudit` NuGet (`analyzers/dotnet/cs/`) — no extra
package to install. The reflective `ScanAssembly` path still works and now carries
`[RequiresUnreferencedCode]` so trim/AOT publishes flag it. As of v0.4.0 the diff engine is
in-house and reflection-free, so the capture/reconstruct surface is Native-AOT clean.
```

4. The performance-section note (line ~355-356) — change:

```markdown
NativeAOT compatibility and source-generated `[Auditable]` discovery are on the
[v0.3 roadmap](ROADMAP.md).
```

to:

```markdown
Source-generated `[Auditable]` discovery (v0.3.0) and a reflection-free, Native-AOT-clean
diff engine (v0.4.0) have shipped — see the [roadmap](ROADMAP.md).
```

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md ROADMAP.md README.md
git commit -m "docs(release): document v0.4.0 — AOT-clean diff engine"
```

---

## Task 9: Final verification and release

**Files:** none modified — verification and tagging only.

- [ ] **Step 1: Clean build of the whole solution**

Run: `dotnet build OrionAudit.sln --configuration Release`
Expected: PASS — zero warnings, zero errors (the repo builds with `TreatWarningsAsErrors=true`).

- [ ] **Step 2: Full test suite**

Run: `dotnet test OrionAudit.sln --configuration Release`
Expected: PASS — every test project green, including `DiffEngineTests` and the integration tests.

- [ ] **Step 3: Confirm `JsonPatch.Net` is fully gone**

Run: `git grep -n "JsonPatch\|Json.Patch" -- src tests`
Expected: no matches in source or test `.cs`/`.csproj` files (mentions inside `docs/` and `CHANGELOG.md` describing the removal are fine). If a match appears in `src/` or `tests/`, resolve it before tagging.

- [ ] **Step 4: Verify the package version**

Run: `git grep -n "0.4.0" -- Directory.Build.props src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs`
Expected: `<Version>0.4.0</Version>` in `Directory.Build.props` and both telemetry strings at `0.4.0`.

- [ ] **Step 5: Tag the release**

> **Stop and confirm with the user before running this step** — tagging and pushing is an outward-facing action that triggers the CI `publish` job (NuGet + GitHub Packages) on the GitHub release.

```bash
git tag v0.4.0
git push origin master
git push origin v0.4.0
```

Then create the GitHub release for tag `v0.4.0` (the `publish` workflow job runs on the release `published` event). Use the CHANGELOG `[0.4.0]` section as the release notes.

Expected: CI runs `build-and-test` → `aot-publish-check` → `publish`; the `publish` job packs `OrionAudit`, `OrionAudit.AspNetCore`, `OrionAudit.Testing` and pushes them to NuGet.org and GitHub Packages.

---

## Self-Review Notes

- **Spec coverage:** §4 `Json6902` → Task 2; §5 `DiffEngine` facade → Task 3; §6 dependency removal → Task 4; §7 AOT CI → Tasks 5–6; §8 `[RequiresUnreferencedCode]` audit → covered by the AOT probe in Tasks 5–6 (the probe failing on any `IL2*`/`IL3*` warning *is* the audit; no code change expected); §9 version/telemetry → Task 7; §10 docs → Task 8; §11 testing → Tasks 1 & 3; §12 release → Task 9. §3 backward compatibility is enforced by the `Apply_HistoricalPatchWithMoveCopyTest_Replays` test (Task 1) and the unchanged-format proof of the original tests passing (Task 3).
- **Type consistency:** `Json6902.Compute(JsonNode?, JsonNode?) → JsonArray` and `Json6902.Apply(JsonNode, JsonArray) → JsonNode` are the only public members of `Json6902`; `DiffEngine` (Task 3) calls exactly those signatures. `OrionAuditException` has both `(string)` and `(string, Exception)` constructors — both are used.
