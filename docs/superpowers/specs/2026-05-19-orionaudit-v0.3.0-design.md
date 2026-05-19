# OrionAudit v0.3.0 — Design Spec

**Date:** 2026-05-19
**Status:** Draft (design); pending scope confirmation + implementation plan
**Authors:** Tunahan Ali Ozturk
**Family:** Orion (sibling of OrionGuard)
**Predecessors:** [v0.1.0][s1] / [v0.2.0][s2]

[s1]: 2026-05-13-orionaudit-v0.1.0-design.md
[s2]: 2026-05-19-orionaudit-v0.2.0-design.md

## 1. Goal

Eliminate runtime reflection on the hot path. Make OrionAudit clean to publish with
**`PublishAot=true`** and trim warnings free. v0.2 paid down the operational debt; v0.3 pays
down the *deployment* debt — apps with strict trim or AOT requirements (mobile back-ends, edge
workers, ahead-of-time native publishing) can adopt OrionAudit without forking it.

## 2. Scope

### In scope (v0.3.0)

1. **`Moongazing.OrionAudit.Generators` — new analyzer/source-generator project.** Emitted at
   compile time, lives outside `lib/` in the NuGet (`analyzers/dotnet/cs/`), no runtime cost.
2. **Source-generated `[Auditable]` discovery.** Replaces the runtime
   `AuditableTypeDiscovery.Discover(IEnumerable<Assembly>)`. Consumers opt in by adding an
   `[OrionAuditModule]` attribute to a partial class; the generator emits a
   `RegisterAuditedTypes(AuditConfigurationBuilder)` method. `AddOrionAudit` accepts this
   delegate, skipping the assembly scan entirely.
3. **Source-generated JSON conversion for snapshots.** Per-audited-entity
   `JsonSerializerContext` (System.Text.Json source-gen) emitted alongside the discovery code.
   `SnapshotBuilder.ConvertToNode` and `JsonSerializer.Deserialize<T>` in the reconstructor
   pick up the generated context, removing `JsonSerializer.SerializeToNode(value, value.GetType())`
   reflection and `Deserialize<T>` reflection on hot paths.
4. **Trim & AOT annotations on remaining reflective surface.** The fluent fallback path
   (`AuditConfigurationBuilder.Audit<T>` reflecting over `T`'s properties for `[NotAuditable]`
   / `[HashedAudit]` / `[RedactedAudit]`) and the legacy `AuditableTypeDiscovery` get
   `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]` so trim/AOT publishes surface them
   as warnings consumers can suppress with intent (or migrate away from).
5. **Native AOT smoke test in CI.** New job (or matrix entry) that runs
   `dotnet publish sample/Moongazing.OrionAudit.Sample.Console -c Release -r linux-x64`
   with `PublishAot=true`. Failure on trim/AOT warnings is a hard stop.

### Considered but not committed for v0.3.0

- **Drop `net8.0` target.** Adds complexity to multi-targeting without a hard requirement;
  defer until adoption signal warrants it.
- **Source-gen for diff/patch.** `JsonPatch.Net` doesn't currently expose a source-gen entry
  point. Replacing it with a hand-rolled emitter is a larger effort — split into a future task.

### Explicitly *not* in scope

- Rewriting `JsonPatch.Net` or shipping a hand-rolled patcher.
- New runtime features. v0.3 is a *quality* release; no surface area changes except the new
  module attribute and the deprecation annotations.
- Roslyn analyzer rules beyond the source generator (no `OAUDIT0001`-style warnings yet).

## 3. Architecture

### 3.1 The new module attribute and generator

Consumers declare an "audit module":

```csharp
using Moongazing.OrionAudit;

[OrionAuditModule]
public partial class AppAuditModule
{
    // Generator fills in:
    //     public static void RegisterAuditedTypes(AuditConfigurationBuilder builder) { ... }
    //     public static JsonSerializerContext SerializerContext => OrionAuditGeneratedContext.Default;
}
```

The generator:

1. Walks the compilation for types decorated with `[Auditable]`.
2. For each type, emits a `builder.Audit<T>()` call with the appropriate
   `[NotAuditable]` / `[HashedAudit]` / `[RedactedAudit]` / `[SoftDelete]` properties
   resolved at compile time (no reflection at runtime).
3. Emits a `JsonSerializerContext` derived class declaring each audited type as a known type
   for trim-safe (de)serialisation.

Consumers wire it up:

```csharp
services.AddOrionAudit<AppDb>(o =>
{
    AppAuditModule.RegisterAuditedTypes(o.ConfigurationBuilder);  // generated
    o.UseJsonContext(AppAuditModule.SerializerContext);            // generated
    o.SnapshotEvery(50);
});
```

### 3.2 SnapshotBuilder / Reconstructor pickup

`SnapshotBuilder.Build` learns to accept an optional `JsonSerializerContext`:

```csharp
public static JsonObject Build(
    Type entityType,
    IReadOnlyDictionary<string, object?> propertyValues,
    IAuditConfiguration configuration,
    JsonSerializerContext? jsonContext = null)
```

When `jsonContext` is supplied, the primitive-fallback path becomes:

```csharp
var info = jsonContext.GetTypeInfo(value.GetType());
return info is null
    ? JsonSerializer.SerializeToNode(value, value.GetType())   // last-resort reflection
    : JsonSerializer.SerializeToNode(value, info);             // trim-safe
```

Likewise `AuditReconstructor.Replay<T>` picks `JsonSerializer.Deserialize<T>(state, jsonContext)`
when the context is registered.

The serializer context lives behind a new DI registration:

```csharp
services.TryAddSingleton<JsonSerializerContext>(...)
```

When none is registered, the existing reflective paths kick in (annotated with
`[RequiresUnreferencedCode]` so AOT consumers see a warning).

### 3.3 Trim/AOT annotations

| Surface | Annotation |
| ------- | ---------- |
| `AuditableTypeDiscovery.Discover` | `[RequiresUnreferencedCode]`, `[RequiresDynamicCode]` |
| `AuditConfigurationBuilder.Audit<T>` reflection-over-T-properties path | `[RequiresUnreferencedCode]` |
| `SnapshotBuilder.ConvertToNode` reflective fallback | `[RequiresUnreferencedCode]` |
| `AuditReconstructor.Replay<T>` reflective `Deserialize<T>` | `[RequiresUnreferencedCode]`, `[RequiresDynamicCode]` |

Consumers calling the generator-backed path (`AppAuditModule.RegisterAuditedTypes`) get no
warnings.

### 3.4 Generator delivery

The generator project ships in the **same `OrionAudit` NuGet package** under
`analyzers/dotnet/cs/`:

```
OrionAudit.0.3.0.nupkg
├── analyzers/
│   └── dotnet/
│       └── cs/
│           └── Moongazing.OrionAudit.Generators.dll
├── lib/
│   ├── net8.0/Moongazing.OrionAudit.dll
│   ├── net9.0/Moongazing.OrionAudit.dll
│   └── net10.0/Moongazing.OrionAudit.dll
└── docs/...
```

Consumers don't need a separate `dotnet add package`. The csproj entry that makes this work:

```xml
<ItemGroup>
  <ProjectReference Include="..\Moongazing.OrionAudit.Generators\Moongazing.OrionAudit.Generators.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false"
                    PrivateAssets="all" />
</ItemGroup>
<ItemGroup>
  <None Include="$(OutputPath)\..\netstandard2.0\Moongazing.OrionAudit.Generators.dll"
        Pack="true"
        PackagePath="analyzers/dotnet/cs"
        Visible="false" />
</ItemGroup>
```

(Generator targets `netstandard2.0` — Roslyn's analyzer host requirement.)

### 3.5 Native AOT smoke test

A new GitHub Actions job:

```yaml
aot-publish-check:
  runs-on: ubuntu-latest
  needs: build-and-test
  steps:
    - uses: actions/checkout@v4
    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: |
          8.0.x
          9.0.x
          10.0.x
    - run: dotnet publish sample/Moongazing.OrionAudit.Sample.Console -c Release -r linux-x64 -p:PublishAot=true
```

Sample app must be wired with the source-gen module + `UseJsonContext(...)` so it doesn't trip
the deprecated reflective paths. Build fails if any `IL2*`, `IL3*`, or `AOT0*` warning is
emitted.

## 4. Public API additions

```csharp
namespace Moongazing.OrionAudit;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class OrionAuditModuleAttribute : Attribute { }

public sealed partial class OrionAuditOptions
{
    public OrionAuditOptions UseJsonContext(JsonSerializerContext context);
    // Existing methods unchanged.
}
```

No breaking changes. The reflective `AuditConfigurationBuilder.Audit<T>(Action<...>?)` path
stays — only the warning attribute is added.

## 5. Migration from v0.2.0

- **No code changes required** for consumers that don't publish AOT and don't care about trim.
  They keep getting the reflective registration; performance is unchanged.
- **AOT/trim consumers** declare an `[OrionAuditModule] partial class`, call its generated
  `RegisterAuditedTypes` and `UseJsonContext` from `AddOrionAudit`, and any remaining
  `[RequiresUnreferencedCode]` call sites flag the migration.
- The new `OrionAudit_Snapshot_Cursors` migration from v0.2 is unaffected.

## 6. Performance characteristics

Expected (numbers to be measured in the v0.3 release commit):

- **SnapshotBuilder primitive-fallback path:** -30 to -60% allocation when source-gen context
  is wired (no `JsonSerializer.SerializeToNode` reflection cache pressure).
- **Reconstructor `Deserialize<T>`:** similar drop.
- **Capture overhead (interceptor):** unchanged for primitive fields (already on the fast path
  since v0.1.0); win shows on entities with user-defined property types.
- **Startup cost:** -X ms per `AddOrionAudit` call when the generator-emitted module replaces
  the assembly scan. (Negligible for small projects; meaningful in apps with > 10 assemblies.)

## 7. Definition of Done

- New `Moongazing.OrionAudit.Generators` project, packaged in the same `OrionAudit` NuGet
  under `analyzers/dotnet/cs/`.
- Sample console migrated to the source-gen module and publishes with `PublishAot=true` clean
  (zero `IL2*` / `IL3*` / `AOT0*` warnings).
- Existing 95 tests still pass + 10–15 new tests for the generator (snapshot-style tests of
  emitted code, plus integration tests that wire the generated module into a context and
  verify capture/reconstruct work end-to-end).
- CI gains an `aot-publish-check` job, gated on the existing `build-and-test` job.
- CHANGELOG entry under `[0.3.0]`; ROADMAP v0.3 items moved to *Shipped*.
- README hero callout updated; comparison table marks AOT-clean as "Yes".

## 8. Test plan

| Area | Cases |
| ---- | ----- |
| Generator emission | snapshot tests on the generated source for each combination of attributes |
| Generator: no `[Auditable]` types | emits empty `RegisterAuditedTypes` body + empty context |
| Generator: composite-key entity | round-trip key + reconstruct works against generated module |
| Generator: `[SoftDelete]` flip detection | works through the generated wiring |
| End-to-end capture + reconstruct | uses generated module; no reflection in stack trace |
| AOT publish | smoke job in CI |
| Backward compat | existing reflective `Audit<T>(...)` API still works, just warns under AOT |

## 9. Open questions

- **Two attributes vs. one for the module?** Current design: single `[OrionAuditModule]` with
  generator scanning the whole compilation for `[Auditable]`. Alternative: explicit list:
  `[OrionAuditModule(typeof(Order), typeof(Customer))]`. Decision: implicit scan first (less
  ceremony); add the explicit overload only if assembly-wide scanning has unwanted edge cases.
- **Should the generator emit a separate `[GenerateValidator]`-style attribute per entity?**
  OrionGuard does this. For audit it's less useful — we don't run per-entity logic, just
  registration. Keeping single module-level attribute for v0.3; revisit if user feedback asks.
- **`netstandard2.0` for the generator vs. `net9.0` for Roslyn 4.10+ features?** All current
  Roslyn analyzer hosts require `netstandard2.0`; stick to that.

## 10. Risks

- **Source generator complexity.** Adding the generator project requires a separate
  `netstandard2.0` build, careful packaging, and snapshot tests. Roughly 60–70% of v0.3 effort.
- **AOT-clean test surface.** Some EF Core paths still emit `IL2*` warnings (their problem,
  not ours), so the smoke test will need targeted `<TrimmerRootDescriptor>` suppressions to
  avoid false positives. Document each.
- **NuGet packaging.** Single-package delivery for analyzer + library has known footguns
  (analyzer.dll loaded in wrong context). Verified via CI sample build before publishing.
