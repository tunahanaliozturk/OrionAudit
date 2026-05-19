# OrionAudit v0.3.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (or
> subagent-driven-development) to implement this plan task-by-task. Steps use checkbox
> (`- [ ]`) syntax for tracking.

**Goal:** Ship v0.3.0 (AOT & Source-Gen). Eliminate runtime reflection on the hot path, make
`PublishAot=true` clean.

**Spec:** [docs/superpowers/specs/2026-05-19-orionaudit-v0.3.0-design.md][spec]

**Predecessor:** v0.2.0 — released `2026-05-19` from commit `36db602`.

**NuGet IDs unchanged:** `OrionAudit`, `OrionAudit.AspNetCore`, `OrionAudit.Testing`. The
generator ships inside the existing `OrionAudit` package under `analyzers/dotnet/cs/`, not as a
separate NuGet ID.

[spec]: ../specs/2026-05-19-orionaudit-v0.3.0-design.md

---

## Task ordering rationale

1. **Generators project skeleton (Task 1)** establishes the netstandard2.0 Roslyn project +
   single-NuGet packaging — no behaviour yet, just plumbing.
2. **`[OrionAuditModule]` attribute (Task 2)** is the consumer's only opt-in surface; lives
   in the core runtime library.
3. **Discovery generator (Task 3)** emits `RegisterAuditedTypes` — the first useful generator
   output, validated via snapshot tests.
4. **JsonSerializerContext generator (Task 4)** extends the same generator with a per-entity
   STJ source-gen context.
5. **`UseJsonContext` wiring (Task 5)** plumbs the generated context through `SnapshotBuilder`
   and `AuditReconstructor`.
6. **Trim/AOT annotations (Task 6)** marks the remaining reflective fallbacks with
   `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`.
7. **Sample migration (Task 7)** rewrites the console sample to use the generator path, so the
   AOT smoke test in Task 8 has something to publish cleanly.
8. **AOT smoke job in CI (Task 8)** locks in the trim/AOT guarantee.
9. **Release (Task 9)** version bump, CHANGELOG, ROADMAP, tag, publish.

---

## Task 1: Generators project skeleton

**Files:**
- Create: `src/Moongazing.OrionAudit.Generators/Moongazing.OrionAudit.Generators.csproj`
- Create: `src/Moongazing.OrionAudit.Generators/Directory.Build.props` (kill multi-target inheritance)
- Create: `src/Moongazing.OrionAudit.Generators/Placeholder.cs` (so the assembly compiles)
- Modify: `src/Moongazing.OrionAudit/Moongazing.OrionAudit.csproj` (ProjectReference with analyzer asset; pack hook)
- Modify: `OrionAudit.sln` (add new project)

### Step 1: Create the project file

Generator hosts require `netstandard2.0`. Roslyn API version 4.10+ for `IIncrementalGenerator`
+ `ForAttributeWithMetadataName`.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsRoslynComponent>true</IsRoslynComponent>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.10.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

### Step 2: Add `Directory.Build.props` to stop multi-targeting inheritance

The repo root sets `TargetFrameworks=net8.0;net9.0;net10.0`. We need just netstandard2.0 here.
Same trick as `bench/`.

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <TargetFrameworks></TargetFrameworks>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

### Step 3: Add a placeholder to make the project compile

`src/Moongazing.OrionAudit.Generators/Placeholder.cs`:

```csharp
namespace Moongazing.OrionAudit.Generators;

// Real generator types land in Task 3+; placeholder keeps the assembly buildable.
internal static class Placeholder { }
```

### Step 4: Wire core library to consume + pack the generator

Append to `src/Moongazing.OrionAudit/Moongazing.OrionAudit.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\Moongazing.OrionAudit.Generators\Moongazing.OrionAudit.Generators.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>

<ItemGroup>
  <None Include="..\Moongazing.OrionAudit.Generators\bin\$(Configuration)\netstandard2.0\Moongazing.OrionAudit.Generators.dll"
        Pack="true"
        PackagePath="analyzers/dotnet/cs"
        Visible="false" />
</ItemGroup>
```

### Step 5: Add to solution + build

```bash
dotnet sln add src/Moongazing.OrionAudit.Generators/Moongazing.OrionAudit.Generators.csproj
dotnet build OrionAudit.sln -c Debug
```

Expected: build succeeds across all targets.

### Step 6: Commit

```bash
git add src/Moongazing.OrionAudit.Generators \
        src/Moongazing.OrionAudit/Moongazing.OrionAudit.csproj \
        OrionAudit.sln
git commit -m "build(generators): scaffold Moongazing.OrionAudit.Generators (netstandard2.0)"
```

---

## Task 2: `[OrionAuditModule]` attribute

**Files:**
- Create: `src/Moongazing.OrionAudit/OrionAuditModuleAttribute.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/OrionAuditModuleAttributeTests.cs`

### Step 1: Failing test

```csharp
using System.Reflection;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.Tests;

public class OrionAuditModuleAttributeTests
{
    [OrionAuditModule]
    public partial class AppModule { }

    [Fact]
    public void OrionAuditModule_IsClassLevel_AndDetectable()
    {
        var attr = typeof(AppModule).GetCustomAttribute<OrionAuditModuleAttribute>();
        Assert.NotNull(attr);
    }
}
```

### Step 2: Implement

```csharp
namespace Moongazing.OrionAudit;

/// <summary>
/// Marks a partial class as the OrionAudit registration module for the consuming project.
/// The source generator emits a <c>RegisterAuditedTypes(AuditConfigurationBuilder)</c> method
/// and a static <c>SerializerContext</c> property on the marked class.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class OrionAuditModuleAttribute : Attribute { }
```

### Step 3: Build + commit

```bash
dotnet build src/Moongazing.OrionAudit -c Debug
tests/Moongazing.OrionAudit.Tests/bin/Debug/net10.0/Moongazing.OrionAudit.Tests.exe
git add src/Moongazing.OrionAudit/OrionAuditModuleAttribute.cs \
        tests/Moongazing.OrionAudit.Tests/OrionAuditModuleAttributeTests.cs
git commit -m "feat(core): add [OrionAuditModule] opt-in attribute for the source generator"
```

---

## Task 3: Source generator — emit `RegisterAuditedTypes`

**Files:**
- Create: `src/Moongazing.OrionAudit.Generators/OrionAuditModuleGenerator.cs`
- Create: `src/Moongazing.OrionAudit.Generators/EmissionContext.cs` (collected entity info)
- Test: `tests/Moongazing.OrionAudit.Tests/SourceGenSmokeTests.cs` (compiles + executes against a real module)

### Step 1: Smoke test — declare a generator-backed module + verify registration

```csharp
namespace Moongazing.OrionAudit.Tests;

[OrionAuditModule]
public partial class TestModule { }

[Auditable]
public sealed class Widget { public int Id { get; set; } public string Name { get; set; } = ""; }

public class SourceGenSmokeTests
{
    [Fact]
    public void RegisterAuditedTypes_IsEmittedByGenerator_AndRegistersAuditableTypes()
    {
        var builder = new AuditConfigurationBuilder();
        TestModule.RegisterAuditedTypes(builder);   // <-- this method is emitted by the generator
        var config = builder.Build();
        Assert.True(config.IsAudited(typeof(Widget)));
    }
}
```

### Step 2: Implement `OrionAuditModuleGenerator`

```csharp
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Moongazing.OrionAudit.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class OrionAuditModuleGenerator : IIncrementalGenerator
{
    private const string ModuleAttributeFqn = "Moongazing.OrionAudit.OrionAuditModuleAttribute";
    private const string AuditableAttributeFqn = "Moongazing.OrionAudit.AuditableAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var modules = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ModuleAttributeFqn,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol)
            .Where(static sym => sym is not null)
            .Collect();

        var auditableTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AuditableAttributeFqn,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol)
            .Where(static sym => sym is not null && !sym.IsAbstract)
            .Collect();

        context.RegisterSourceOutput(modules.Combine(auditableTypes), Emit);
    }

    private static void Emit(SourceProductionContext spc, (ImmutableArray<INamedTypeSymbol> Modules, ImmutableArray<INamedTypeSymbol> Types) input)
    {
        foreach (var module in input.Modules)
        {
            var ns = module.ContainingNamespace.IsGlobalNamespace ? null : module.ContainingNamespace.ToDisplayString();
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using Moongazing.OrionAudit;");
            sb.AppendLine("using Moongazing.OrionAudit.Configuration;");
            if (ns is not null) { sb.Append("namespace ").Append(ns).AppendLine(";"); }

            var modifiers = module.DeclaredAccessibility switch
            {
                Accessibility.Public => "public",
                Accessibility.Internal => "internal",
                _ => "internal",
            };

            sb.Append(modifiers).Append(" partial class ").AppendLine(module.Name);
            sb.AppendLine("{");
            sb.AppendLine("    public static void RegisterAuditedTypes(AuditConfigurationBuilder builder)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (builder is null) throw new global::System.ArgumentNullException(nameof(builder));");
            foreach (var t in input.Types)
            {
                sb.Append("        builder.Audit(typeof(").Append(t.ToDisplayString()).AppendLine("));");
            }
            sb.AppendLine("    }");
            sb.AppendLine("}");

            spc.AddSource($"{module.Name}.OrionAuditModule.g.cs", sb.ToString());
        }
    }
}
```

### Step 3: Run smoke test

```bash
dotnet build tests/Moongazing.OrionAudit.Tests -c Debug
tests/Moongazing.OrionAudit.Tests/bin/Debug/net10.0/Moongazing.OrionAudit.Tests.exe
```

Expected: `RegisterAuditedTypes_IsEmittedByGenerator_AndRegistersAuditableTypes` passes.
If it fails to compile (method not generated), inspect `obj/Debug/net10.0/generated/` for the
output file. If empty, the generator didn't run — usually a Roslyn version mismatch.

### Step 4: Commit

```bash
git add src/Moongazing.OrionAudit.Generators/OrionAuditModuleGenerator.cs \
        tests/Moongazing.OrionAudit.Tests/SourceGenSmokeTests.cs
git commit -m "feat(generators): emit RegisterAuditedTypes from [OrionAuditModule] classes"
```

---

## Task 4: Source generator — emit `JsonSerializerContext`

Extend the same generator. Adds a `SerializerContext` static property on the module and a
nested `[JsonSerializable]`-attributed partial class.

**Files:**
- Modify: `src/Moongazing.OrionAudit.Generators/OrionAuditModuleGenerator.cs`
- Test: extend `SourceGenSmokeTests.cs`

### Step 1: Failing test — assert the generated context knows about audited types

```csharp
[Fact]
public void SerializerContext_KnowsAboutAuditableTypes()
{
    var ctx = TestModule.SerializerContext;
    Assert.NotNull(ctx);
    Assert.NotNull(ctx.GetTypeInfo(typeof(Widget)));
}
```

### Step 2: Extend the emitter

In the `Emit` method, after `RegisterAuditedTypes`, add:

```csharp
sb.AppendLine();
sb.AppendLine("    public static global::System.Text.Json.Serialization.JsonSerializerContext SerializerContext");
sb.Append("        => ").Append(module.Name).AppendLine("GeneratedJsonContext.Default;");
sb.AppendLine("}");

// Nested context class
sb.AppendLine();
sb.Append("[global::System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = false)]");
foreach (var t in input.Types)
{
    sb.AppendLine();
    sb.Append("[global::System.Text.Json.Serialization.JsonSerializable(typeof(")
      .Append(t.ToDisplayString())
      .AppendLine("))]");
}
sb.Append(modifiers).Append(" partial class ").Append(module.Name).AppendLine("GeneratedJsonContext : global::System.Text.Json.Serialization.JsonSerializerContext { }");
```

Re-order so the close brace of the module class appears before the context emission.

### Step 3: Run + commit

```bash
tests/Moongazing.OrionAudit.Tests/bin/Debug/net10.0/Moongazing.OrionAudit.Tests.exe
git add src/Moongazing.OrionAudit.Generators/OrionAuditModuleGenerator.cs \
        tests/Moongazing.OrionAudit.Tests/SourceGenSmokeTests.cs
git commit -m "feat(generators): also emit a JsonSerializerContext for audited types"
```

---

## Task 5: `UseJsonContext` wiring

**Files:**
- Modify: `src/Moongazing.OrionAudit/DependencyInjection/OrionAuditOptions.cs` (add `UseJsonContext`)
- Modify: `src/Moongazing.OrionAudit/DependencyInjection/AuditServiceCollectionExtensions.cs` (register the context)
- Modify: `src/Moongazing.OrionAudit/Capture/SnapshotBuilder.cs` (accept + use `JsonSerializerContext?`)
- Modify: `src/Moongazing.OrionAudit/Read/AuditReconstructor.cs` (accept + use it on `Deserialize<T>`)
- Modify: `src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs` (resolve from SP, pass through)
- Test: `tests/Moongazing.OrionAudit.Tests/JsonContextWiringTests.cs`

### Step 1: Failing test

```csharp
[Fact]
public async Task SaveChanges_WithUseJsonContext_NoReflectionFallback()
{
    var services = new ServiceCollection();
    services.AddOrionAudit<TestContext>(o =>
    {
        TestModule.RegisterAuditedTypes(o.ConfigurationBuilder);
        o.UseJsonContext(TestModule.SerializerContext);
    });
    services.AddDbContext<TestContext>((sp, o) =>
        o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));

    await using var sp = services.BuildServiceProvider();
    await using var scope = sp.CreateAsyncScope();
    var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
    ctx.Widgets.Add(new Widget { Id = 1, Name = "test" });
    await ctx.SaveChangesAsync();

    var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
    Assert.Contains("\"test\"", entry.Diff);
}
```

### Step 2: Implement

Add `OrionAuditOptions.UseJsonContext(JsonSerializerContext context)` setter; register the
context as singleton in DI; thread it through `SnapshotBuilder.Build(... JsonSerializerContext?
context = null)` and `AuditReconstructor.Replay<T>(... JsonSerializerContext? context = null)`.

In `SnapshotBuilder.ConvertToNode`, after the primitive switch's default branch:

```csharp
if (jsonContext is not null)
{
    var info = jsonContext.GetTypeInfo(value.GetType());
    if (info is not null)
    {
        return JsonSerializer.SerializeToNode(value, info);
    }
}
return JsonSerializer.SerializeToNode(value, value.GetType());   // reflective fallback
```

In `AuditReconstructor.Replay<T>`, replace `JsonSerializer.Deserialize<T>(state)` with:

```csharp
return jsonContext is not null
    ? (T?)JsonSerializer.Deserialize(state, typeof(T), jsonContext)
    : JsonSerializer.Deserialize<T>(state);
```

### Step 3: Run + commit

```bash
git commit -m "feat(capture,read): plumb JsonSerializerContext through SnapshotBuilder and AuditReconstructor"
```

---

## Task 6: Trim/AOT annotations

**Files:**
- Modify: `src/Moongazing.OrionAudit/Configuration/AuditableTypeDiscovery.cs`
- Modify: `src/Moongazing.OrionAudit/Configuration/AuditConfigurationBuilder.cs` (the reflective `Audit<T>`)
- Modify: `src/Moongazing.OrionAudit/Capture/SnapshotBuilder.cs` (reflective fallback)
- Modify: `src/Moongazing.OrionAudit/Read/AuditReconstructor.cs` (reflective fallback)

Each gets `[RequiresUnreferencedCode]` (and `[RequiresDynamicCode]` where applicable). The
generator-backed call sites do not.

### Steps

For each method that hits the reflective path:

```csharp
[RequiresUnreferencedCode(
    "Uses runtime reflection over T's properties. For trim/AOT consumers, use the source generator " +
    "by adding [OrionAuditModule] to a partial class and calling its emitted RegisterAuditedTypes.")]
[RequiresDynamicCode("Uses JsonSerializer reflection; pass an AOT-safe JsonSerializerContext via UseJsonContext.")]
public AuditConfigurationBuilder Audit<T>(...) ...
```

Build + verify no new errors. Trim warnings only fire when consumers publish AOT.

### Commit

```bash
git commit -m "feat(trim): annotate remaining reflective surface with [RequiresUnreferencedCode] / [RequiresDynamicCode]"
```

---

## Task 7: Migrate sample console to the source-gen path

**Files:**
- Modify: `sample/Moongazing.OrionAudit.Sample.Console/Program.cs`
- Modify: `sample/Moongazing.OrionAudit.Sample.Console/SampleTypes.cs` (add the module)

### Steps

Add to `SampleTypes.cs`:

```csharp
[OrionAuditModule]
public partial class SampleAuditModule { }
```

Modify `AddOrionAudit` call in `Program.cs`:

```csharp
services.AddOrionAudit<ShopDb>(o =>
{
    SampleAuditModule.RegisterAuditedTypes(o.ConfigurationBuilder);
    o.UseJsonContext(SampleAuditModule.SerializerContext);
    // existing TenantResolver / UserResolver / sensitive-field overrides stay
    o.Audit<Customer>(b => b.Hash(c => c.Email).Redact(c => c.ApiKey));
});
```

Run the sample to confirm output unchanged.

### Commit

```bash
git commit -m "docs(sample): use the [OrionAuditModule] source-gen path"
```

---

## Task 8: AOT smoke test in CI

**Files:**
- Modify: `.github/workflows/ci-cd.yml` (add `aot-publish-check` job)

### Steps

Append after `build-and-test`:

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
      - name: AOT publish sample
        run: |
          dotnet publish sample/Moongazing.OrionAudit.Sample.Console \
            -c Release -r linux-x64 \
            -p:PublishAot=true -p:TreatWarningsAsErrors=true
```

Suppress known EF Core trim warnings in the sample csproj via `<TrimmerRootDescriptor>` only
if necessary; document each suppression.

### Commit

```bash
git commit -m "ci: add aot-publish-check job gated on build-and-test"
```

---

## Task 9: Release v0.3.0

**Files:**
- Modify: `Directory.Build.props` (`<Version>0.3.0</Version>`)
- Modify: `CHANGELOG.md` (new `[0.3.0]` entry above `[0.2.0]`)
- Modify: `ROADMAP.md` (move v0.3 from Planned → Shipped)
- Modify: `README.md` (refresh v-aware hero callout, update comparison table)
- Modify: `ECOSYSTEM.md` (update OrionAudit row in §2)

### Steps

1. Bump version, write CHANGELOG, update ROADMAP labels.
2. Run full Release build + all tests + pack 3 NuGet packages to verify.
3. Commit, tag, push, gh release create. Workflow publishes to nuget.org + GitHub Packages.

```bash
git commit -am "release: v0.3.0 — AOT-clean source generator for [Auditable] discovery and JSON snapshots"
git tag -a v0.3.0 -m "v0.3.0 — AOT & Source-Gen"
git push origin master v0.3.0
gh release create v0.3.0 \
  --title "v0.3.0 — AOT & Source-Gen" \
  --notes-file <(awk '/^## \[0\.3\.0\]/{f=1; next} /^## \[/{f=0} f' CHANGELOG.md) \
  --verify-tag --latest
```

---

## Definition of Done (mirrors spec § 7)

- All v0.2.0 tests still pass unchanged.
- Generator emits `RegisterAuditedTypes` + `SerializerContext` for every `[OrionAuditModule]`
  class in the consuming compilation.
- Sample console uses the generator path; publishes with `PublishAot=true` clean.
- CI gains the `aot-publish-check` job; fails the build on `IL2*` / `IL3*` / `AOT0*` warnings.
- 10+ new tests covering the generator + JSON context wiring.
- 3 NuGet packages updated to v0.3.0 (IDs unchanged).
- CHANGELOG entry shipped under `[0.3.0]`.
- ROADMAP v0.3 items moved to Shipped.
- ECOSYSTEM.md OrionAudit row updated to v0.3.0.

## Self-review checklist

- Generator emits `// <auto-generated/>` headers on every output file (suppresses style
  diagnostics in the generated source).
- Generator's package shipping uses `Pack="true" PackagePath="analyzers/dotnet/cs"`; no
  consumer needs a separate `dotnet add package`.
- `SnapshotBuilder.ConvertToNode` reflective fallback is still reachable for consumers that
  don't wire `UseJsonContext`; it just emits a trim warning.
- The new `[OrionAuditModule]` attribute is `AllowMultiple = false` and class-only.
- No `Co-Authored-By` trailer on any commit in the plan.
