# OrionAudit v0.4.0 — Design Spec

**Date:** 2026-05-20
**Status:** Approved — ready for implementation planning.
**Authors:** Tunahan Ali Ozturk
**Family:** Orion (sibling of OrionGuard)
**Predecessors:** [v0.1.0][s1] / [v0.2.0][s2] / [v0.3.0][s3]

[s1]: 2026-05-13-orionaudit-v0.1.0-design.md
[s2]: 2026-05-19-orionaudit-v0.2.0-design.md
[s3]: 2026-05-19-orionaudit-v0.3.0-design.md

## 1. Goal

Make OrionAudit's capture/reconstruct path **Native-AOT clean end to end**. v0.3.0 removed the
runtime assembly scan and snapshot-serialisation reflection, but the *diff* path still depends
on `JsonPatch.Net` — whose `CreatePatch` and `JsonPatch` (de)serialisation carry
`[RequiresUnreferencedCode]` / `[RequiresDynamicCode]` and are not AOT-compatible. v0.4.0
replaces that dependency with a hand-rolled, source-gen-friendly RFC 6902 emitter built only
on `System.Text.Json.Nodes`, and brings the Native AOT CI smoke test back as a hard gate.

## 2. Scope

### In scope (v0.4.0)

1. **`Json6902` engine** — a new `internal static` RFC 6902 compute/apply implementation over
   `System.Text.Json.Nodes`, with zero reflection and no `[RequiresDynamicCode]` surface.
2. **`DiffEngine` becomes a thin facade** — its public `Compute` / `Apply` signatures are
   preserved byte-for-byte; internals delegate to `Json6902`. The persisted patch format stays
   RFC 6902 JSON, so existing `AuditLog.Diff` rows remain readable.
3. **`JsonPatch.Net` package reference removed** from `Moongazing.OrionAudit.csproj`.
4. **Native AOT CI returns** — the `aot/Moongazing.OrionAudit.AotProbe` project and the
   `aot-publish-check` CI job are restored; `publish` depends on it again.
5. **`[RequiresUnreferencedCode]` audit** — confirm the diff path no longer contributes
   IL2*/IL3* warnings; legitimately-reflective surfaces stay annotated.
6. **Version + telemetry bump** to `0.4.0`; docs (CHANGELOG, ROADMAP, README) updated.

### Considered but not committed for v0.4.0

- **Drop `net8.0` target.** ROADMAP floated this; not a technical requirement for the diff
  engine. TFMs stay `net8.0;net9.0;net10.0` — dropping a TFM mid-`0.x` annoys consumers for no
  AOT gain.
- **`move`/`copy` detection in `Compute`.** Minimal-patch optimisation; marginal benefit on
  entity snapshots, large test surface. `Compute` emits `add`/`remove`/`replace` only.
- **LCS-based minimal array diffing.** Positional array diffing is correct; LCS is extra
  complexity for a rare edge case (collection-valued audited properties).

## 3. Backward compatibility — the load-bearing constraint

Production databases hold `AuditLog.Diff` strings written by `JsonPatch.Net` across v0.1–v0.3.
`JsonPatch.Net`'s `CreatePatch` can emit `move` and `copy` (and the spec permits `test`).
Therefore:

- **`Json6902.Apply` MUST support all six RFC 6902 operations** — `add`, `remove`, `replace`,
  `move`, `copy`, `test` — so it can replay every historical patch.
- **`Json6902.Compute` emits only `add` / `remove` / `replace`.** New patches are a strict
  subset of the format; old readers (and `Apply`) handle them trivially.
- The patch wire format is unchanged RFC 6902 JSON. No schema migration, no data migration.

## 4. `Json6902` engine

New file: `src/Moongazing.OrionAudit/Capture/Json6902.cs`, `internal static class Json6902`.

### 4.1 `Compute(JsonNode? before, JsonNode? after) → JsonArray`

Recursive structural diff producing an ordered `JsonArray` of operation objects:

- **Object** — for each key: present in `before` only → `remove`; in `after` only → `add`;
  in both and not deep-equal → recurse when both sides are object/array, else `replace`.
- **Array** — positional. Compare indices `0..min(len)`: differing element recurses (object/
  array) or `replace`s. If `after` is longer → `add` the tail elements. If `before` is longer
  → `remove` the tail, **highest index first**, so earlier indices stay valid.
- **Value** — `replace` when not deep-equal.
- Returns an empty `JsonArray` (`[]`) when `before` and `after` are deep-equal.
- JSON Pointer paths escaped per RFC 6901: `~` → `~0`, `/` → `~1`.

### 4.2 `Apply(JsonNode target, JsonArray patch) → JsonNode`

- Operates on a deep clone of `target`; never mutates the input.
- Each op object read by `op` / `path` / `value` / `from`.
- All six ops implemented. `add` to an array honours the `-` end-of-array token. `remove` /
  `replace` require the pointer to resolve. `move` / `copy` resolve `from`. `test` asserts the
  value at `path` is deep-equal to `value`, throwing `OrionAuditException` on mismatch.
- Malformed or inapplicable patches throw `OrionAuditException` with a descriptive message
  (parity with the current `DiffEngine.Apply` failure contract).

### 4.3 Deep equality

A private `JsonNode` deep-equality helper backs both `Compute` (change detection) and `Apply`
(`test` op). Compares `JsonObject` (key set + recurse), `JsonArray` (length + ordered recurse),
and `JsonValue` (by underlying value).

## 5. `DiffEngine` facade

`DiffEngine.cs` keeps its public surface exactly:

- `Compute(JsonObject before, JsonObject after) → string` — delegates to `Json6902.Compute`,
  then `JsonArray.ToJsonString()`.
- `Apply(JsonObject target, string patchJson) → JsonObject` — `JsonNode.Parse(patchJson)` to a
  `JsonArray`, delegates to `Json6902.Apply`, returns `.AsObject()`.
- Null/empty argument guards unchanged. Empty-patch (`"[]"`) handling unchanged.
- XML doc updated: drop the "`JsonPatch.Net` library" sentence; describe the in-house engine.

`JsonNode.Parse` and `ToJsonString()` are reflection-free (`System.Text.Json.Nodes`), so the
facade is AOT-clean.

## 6. Dependency removal

Remove `<PackageReference Include="JsonPatch.Net" Version="3.3.0" />` from
`src/Moongazing.OrionAudit/Moongazing.OrionAudit.csproj`. The `json-patch` package tag stays —
the persisted format is still RFC 6902 JSON Patch. `DiffEngine` is the only consumer (verified:
no other `Json.Patch` / `JsonPatch` references in `src/`).

## 7. Native AOT CI restoration

- Restore `aot/Moongazing.OrionAudit.AotProbe/` (`Program.cs` + `.csproj`) from commit
  `658f107^`. The probe exercises the reflection-free surface — `SnapshotBuilder`,
  `DiffEngine`, the `[OrionAuditModule]` generator, `AuditKey`, `AuditScope` — with
  `PublishAot=true`, `IsAotCompatible=true`, `TreatWarningsAsErrors=true`.
- Re-add the probe project to `OrionAudit.sln`.
- Restore the `aot-publish-check` job in `.github/workflows/ci-cd.yml` (installs `clang` +
  `zlib1g-dev`, runs `dotnet publish aot/Moongazing.OrionAudit.AotProbe -c Release -r
  linux-x64`); make `publish` depend on `[ build-and-test, aot-publish-check ]` again.
- Any `IL2*` / `IL3*` trim/AOT warning fails the build.

## 8. `[RequiresUnreferencedCode]` audit

With the diff engine AOT-safe, confirm `DiffEngine` no longer contributes IL warnings — the
probe proves this. Surfaces that remain legitimately reflective stay annotated, unchanged:

- `AuditableTypeDiscovery.Discover` and `OrionAuditOptions.ScanAssembly` — the opt-in assembly
  scan, the documented non-AOT path.
- The reflective fallback in `SnapshotBuilder` / `AuditReconstructor` when no
  `JsonSerializerContext` is supplied (`UseJsonContext` is the AOT path).

No new annotations expected; this step is a verification pass, not a code change — unless the
probe surfaces a warning, which is then fixed.

## 9. Versioning & metadata

- `Directory.Build.props`: `<Version>0.4.0</Version>`.
- `OrionAuditTelemetry` `ActivitySource` / `Meter` version → `0.4.0`.
- Target frameworks unchanged: `net8.0;net9.0;net10.0`.

## 10. Documentation

- `CHANGELOG.md` — new `## [0.4.0] - 2026-05-20` section: hand-rolled RFC 6902 engine,
  `JsonPatch.Net` removed, AOT CI restored. A "Migration from v0.3.0" note: no code changes,
  no schema/data migration, patch format unchanged.
- `ROADMAP.md` — move v0.4.0 from *(planned)* to *(shipped)*; update the release-cadence table.
- `README.md` — update if it lists `JsonPatch.Net` as a dependency or claims the diff engine is
  third-party.

## 11. Testing

- **`Json6902` / `DiffEngine` unit tests** — `Compute` → `Apply` round-trip for: flat objects,
  nested objects, arrays (grow, shrink, mid-element change), value-type changes, null handling,
  and the empty-diff (`[]`) case.
- **Backward-compatibility test (critical)** — `Apply` consumes hand-authored patches
  containing `move`, `copy`, and `test` operations, proving historical `JsonPatch.Net`-format
  diffs still replay.
- **JSON Pointer escaping** — keys containing `~` and `/` round-trip correctly.
- **`test` op failure** — a `test` op whose value mismatches throws `OrionAuditException`.
- **Existing `DiffEngine` tests pass unchanged** — direct evidence the persisted patch format
  is unchanged.

## 12. Release

Commit the implementation, tag `v0.4.0`, push the tag. The CI `publish` job runs on the
GitHub release event and pushes the package to NuGet.

## 13. Out of scope

- Native-AOT publishing of the EF Core-coupled surface (interceptor, reconstructor,
  `ApplyOrionAuditConfigurations`). EF Core itself is not AOT-compatible; the probe targets
  OrionAudit's own reflection-free surface only.
- The `OrionAudit.Viewer` companion and CLI diff renderer — v0.5.0 (Developer Experience).
