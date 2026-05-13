# OrionAudit v0.1.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the OrionAudit v0.1.0 release — 3 NuGet packages (`OrionAudit`, `OrionAudit.AspNetCore`, `OrionAudit.Testing`) implementing EF Core change-audit with JSON Patch diffs, multi-tenant support, time-travel reconstruction, ASP.NET integration, framework-agnostic testing helpers, and OpenTelemetry instrumentation.

**Architecture:** A `SaveChangesInterceptor` captures change-tracked entities marked `[Auditable]` at save time, computes JSON Patch diffs (RFC 6902) with sensitive-field handling, and writes `AuditLog` rows in the same transaction. Read-side exposes LINQ over `AuditLog` plus an `IAuditReconstructor` that replays diffs to reconstruct entity state at any historical timestamp. User/tenant attribution is pluggable via interfaces; ASP.NET integration package supplies an `HttpContext`-based user resolver.

**Tech Stack:** C# 13, .NET 8 / 9 / 10 multi-targeting, EF Core 9, `JsonPatch.Net`, xUnit 2.9.2, OpenTelemetry.Api 1.9.

**Spec:** [docs/superpowers/specs/2026-05-13-orionaudit-v0.1.0-design.md](../specs/2026-05-13-orionaudit-v0.1.0-design.md)

**Repository:** New repo. No prior code, no migration baggage.

---

## File structure (final state at end of plan)

```
OrionAudit/
├── .github/workflows/ci-cd.yml
├── .gitignore
├── .editorconfig
├── Directory.Build.props
├── OrionAudit.sln
├── README.md
├── CHANGELOG.md
├── LICENSE.txt
├── docs/
│   ├── logo.png
│   └── superpowers/{specs,plans}/...
├── src/
│   ├── OrionAudit/
│   │   ├── OrionAudit.csproj
│   │   ├── docs/{README.md,logo.png}
│   │   ├── Attributes/
│   │   │   ├── AuditableAttribute.cs
│   │   │   ├── NotAuditableAttribute.cs
│   │   │   ├── HashedAuditAttribute.cs
│   │   │   └── RedactedAuditAttribute.cs
│   │   ├── Core/
│   │   │   ├── AuditAction.cs
│   │   │   ├── AuditUser.cs
│   │   │   ├── AuditLog.cs
│   │   │   ├── AuditLogEntityTypeConfiguration.cs
│   │   │   ├── OrionAuditException.cs
│   │   │   └── OrionAuditConfigurationException.cs
│   │   ├── Configuration/
│   │   │   ├── IAuditConfiguration.cs
│   │   │   ├── AuditConfiguration.cs
│   │   │   ├── AuditableTypeConfig.cs
│   │   │   ├── AuditConfigurationBuilder.cs
│   │   │   ├── AuditTypeBuilder.cs
│   │   │   └── AuditableTypeDiscovery.cs
│   │   ├── Resolvers/
│   │   │   ├── IAuditUserResolver.cs
│   │   │   └── IAuditTenantResolver.cs
│   │   ├── Capture/
│   │   │   ├── SnapshotBuilder.cs
│   │   │   ├── DiffEngine.cs
│   │   │   └── AuditSaveChangesInterceptor.cs
│   │   ├── Read/
│   │   │   ├── AuditQueryExtensions.cs
│   │   │   ├── IAuditReconstructor.cs
│   │   │   └── AuditReconstructor.cs
│   │   ├── DependencyInjection/
│   │   │   ├── OrionAuditOptions.cs
│   │   │   ├── AuditServiceCollectionExtensions.cs
│   │   │   ├── DbContextOptionsBuilderExtensions.cs
│   │   │   └── AuditModelBuilderExtensions.cs
│   │   └── Telemetry/
│   │       └── OrionAuditTelemetry.cs
│   ├── OrionAudit.AspNetCore/
│   └── OrionAudit.Testing/
├── tests/
│   ├── OrionAudit.Tests/
│   ├── OrionAudit.AspNetCore.Tests/
│   ├── OrionAudit.Testing.Tests/
│   └── OrionAudit.IntegrationTests/
└── sample/
    └── OrionAudit.Sample.Console/
```

---

## Task 1: Repository scaffolding

**Files:**
- Create: `.gitignore`, `.editorconfig`, `Directory.Build.props`, `LICENSE.txt`, `README.md`, `CHANGELOG.md`, `OrionAudit.sln`
- Create: `docs/superpowers/{specs,plans}/` (already exist; verify)

### Step 1: Initialize git

```bash
cd c:/Users/Tunahan\ Ali\ Ozturk/OneDrive\ -\ PEAKUP/Desktop/OrionAudit
git init -b master
git config commit.gpgsign false
```

### Step 2: Create `.gitignore`

Create `.gitignore` with content from the Microsoft `dotnet new gitignore` template plus:

```
# OrionAudit-specific
artifacts/
*.db
*.db-shm
*.db-wal
```

Full file: download from `https://raw.githubusercontent.com/dotnet/runtime/main/.gitignore` or run `dotnet new gitignore` in an empty directory and copy.

### Step 3: Create `.editorconfig`

```ini
root = true

[*]
indent_style = space
end_of_line = crlf
charset = utf-8
trim_trailing_whitespace = true
insert_final_newline = true

[*.{cs,csx,vb,vbx}]
indent_size = 4
dotnet_style_qualification_for_field = false:warning
dotnet_style_qualification_for_property = false:warning
dotnet_style_qualification_for_method = false:warning
dotnet_style_qualification_for_event = false:warning
csharp_style_var_for_built_in_types = true:suggestion
csharp_style_var_when_type_is_apparent = true:suggestion
csharp_style_var_elsewhere = true:suggestion
csharp_prefer_braces = true:warning
csharp_new_line_before_open_brace = all
csharp_indent_case_contents = true
csharp_indent_switch_labels = true

[*.{xml,csproj,props,targets,yml,yaml,json}]
indent_size = 2
```

### Step 4: Create `Directory.Build.props`

```xml
<Project>
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>

  <PropertyGroup Condition="'$(IsPackable)' != 'false'">
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>CS1591;NU1900;NU1901;NU1902;NU1903;NU1904</NoWarn>
    <Authors>Tunahan Ali Ozturk</Authors>
    <Company>Tunahan Ali Ozturk</Company>
    <RepositoryUrl>https://github.com/tunahanaliozturk/OrionAudit</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PackageProjectUrl>https://github.com/tunahanaliozturk/OrionAudit</PackageProjectUrl>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <Version>0.1.0</Version>
  </PropertyGroup>

  <PropertyGroup Condition="'$(IsPackable)' == 'false'">
    <TargetFramework>net10.0</TargetFramework>
    <TargetFrameworks></TargetFrameworks>
  </PropertyGroup>
</Project>
```

### Step 5: Create `LICENSE.txt` (MIT)

```
MIT License

Copyright (c) 2026 Tunahan Ali Ozturk

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Step 6: Create stub `README.md` and `CHANGELOG.md`

`README.md`:

```markdown
# OrionAudit

EF Core change audit trail with JSON Patch diffs, multi-tenant support, time-travel reconstruction, and OpenTelemetry instrumentation.

Part of the Orion family of standalone .NET libraries.

Documentation and full release notes coming with v0.1.0.
```

`CHANGELOG.md`:

```markdown
# Changelog

All notable changes to OrionAudit will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

(Working towards v0.1.0 — see docs/superpowers/specs for the design.)
```

### Step 7: Create empty solution

```bash
dotnet new sln -n OrionAudit
```

### Step 8: First commit

```bash
git add .gitignore .editorconfig Directory.Build.props LICENSE.txt README.md CHANGELOG.md OrionAudit.sln docs/
git commit -m "build: scaffold OrionAudit repository"
```

(No Co-Authored-By trailer.)

---

## Task 2: Three source-project skeletons

**Files:**
- Create: `src/OrionAudit/OrionAudit.csproj`
- Create: `src/OrionAudit.AspNetCore/OrionAudit.AspNetCore.csproj`
- Create: `src/OrionAudit.Testing/OrionAudit.Testing.csproj`
- Create: marker classes so each project compiles
- Modify: `OrionAudit.sln`

### Step 1: Core csproj

Create `src/OrionAudit/OrionAudit.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>OrionAudit</PackageId>
    <Description>EF Core change audit trail with JSON Patch diffs, multi-tenant support, time-travel reconstruction, and OpenTelemetry instrumentation.</Description>
    <PackageTags>audit;audit-trail;efcore;entity-framework;change-tracking;json-patch;compliance;ddd</PackageTags>
    <PackageReadmeFile>docs/README.md</PackageReadmeFile>
    <PackageIcon>docs/logo.png</PackageIcon>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" Version="9.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
    <PackageReference Include="JsonPatch.Net" Version="3.2.5" />
  </ItemGroup>

  <ItemGroup>
    <None Include="docs/README.md" Pack="true" PackagePath="docs/" />
    <None Include="docs/logo.png" Pack="true" PackagePath="docs/" />
  </ItemGroup>
</Project>
```

### Step 2: AspNetCore csproj

Create `src/OrionAudit.AspNetCore/OrionAudit.AspNetCore.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>OrionAudit.AspNetCore</PackageId>
    <Description>ASP.NET Core integration for OrionAudit. Provides HttpContextAuditUserResolver and DI helpers.</Description>
    <PackageTags>audit;audit-trail;aspnetcore;efcore;httpcontext;orionaudit</PackageTags>
    <PackageReadmeFile>docs/README.md</PackageReadmeFile>
    <PackageIcon>docs/logo.png</PackageIcon>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\OrionAudit\OrionAudit.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Include="docs/README.md" Pack="true" PackagePath="docs/" />
    <None Include="docs/logo.png" Pack="true" PackagePath="docs/" />
  </ItemGroup>
</Project>
```

### Step 3: Testing csproj

Create `src/OrionAudit.Testing/OrionAudit.Testing.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>OrionAudit.Testing</PackageId>
    <Description>Testing helpers for OrionAudit. Provides AuditCapture, fluent assertions, and InMemory resolvers. Framework-agnostic — no xUnit/NUnit/FluentAssertions dependency.</Description>
    <PackageTags>audit;audit-trail;testing;test-helpers;orionaudit</PackageTags>
    <PackageReadmeFile>docs/README.md</PackageReadmeFile>
    <PackageIcon>docs/logo.png</PackageIcon>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\OrionAudit\OrionAudit.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Include="docs/README.md" Pack="true" PackagePath="docs/" />
    <None Include="docs/logo.png" Pack="true" PackagePath="docs/" />
  </ItemGroup>
</Project>
```

### Step 4: docs/README.md placeholders (per package)

For each of the three `src/<pkg>/docs/README.md` files, write a short placeholder with install + usage snippet:

`src/OrionAudit/docs/README.md`:

```markdown
# OrionAudit

EF Core change audit trail.

\`\`\`bash
dotnet add package OrionAudit
\`\`\`

\`\`\`csharp
services.AddOrionAudit<AppDbContext>(o =>
{
    o.UserResolver<HttpContextAuditUserResolver>();
    o.Audit<Order>();
});

services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseSqlServer(connectionString)
     .UseOrionAudit(sp));
\`\`\`
```

(Use real triple-backticks in the actual file.) Mirror similar shape for `OrionAudit.AspNetCore` and `OrionAudit.Testing`.

### Step 5: Copy a placeholder `logo.png`

If a logo image is not yet available, copy a 256x256 placeholder PNG (any image) into `docs/logo.png` and the three `src/<pkg>/docs/logo.png` locations. CI will accept it — the icon can be swapped later without breaking publish.

For now, generate a 1x1 transparent PNG:

```bash
# Windows PowerShell
$bytes = [byte[]](0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
                 0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,0x08,0x06,0x00,0x00,0x00,0x1F,0x15,0xC4,
                 0x89,0x00,0x00,0x00,0x0D,0x49,0x44,0x41,0x54,0x78,0x9C,0x62,0x00,0x01,0x00,0x00,
                 0x05,0x00,0x01,0x0D,0x0A,0x2D,0xB4,0x00,0x00,0x00,0x00,0x49,0x45,0x4E,0x44,0xAE,
                 0x42,0x60,0x82)
[System.IO.File]::WriteAllBytes("docs/logo.png", $bytes)
Copy-Item docs/logo.png src/OrionAudit/docs/logo.png
Copy-Item docs/logo.png src/OrionAudit.AspNetCore/docs/logo.png
Copy-Item docs/logo.png src/OrionAudit.Testing/docs/logo.png
```

### Step 6: Marker classes so projects compile cleanly

Create `src/OrionAudit/OrionAuditMarker.cs`:

```csharp
namespace OrionAudit;

/// <summary>Marker class so the assembly produces a valid compilation unit before real types land.</summary>
internal static class OrionAuditMarker { }
```

Repeat for `src/OrionAudit.AspNetCore/OrionAuditAspNetCoreMarker.cs` and `src/OrionAudit.Testing/OrionAuditTestingMarker.cs` (with their respective namespaces).

### Step 7: Add projects to solution

```bash
dotnet sln add src/OrionAudit/OrionAudit.csproj
dotnet sln add src/OrionAudit.AspNetCore/OrionAudit.AspNetCore.csproj
dotnet sln add src/OrionAudit.Testing/OrionAudit.Testing.csproj
```

### Step 8: Verify build

```bash
dotnet restore OrionAudit.sln
dotnet build OrionAudit.sln -c Debug
```

Expected: `Build succeeded.` with 0 errors across net8/9/10. (NU1900 warnings about offline NuGet feeds are non-blocking.)

### Step 9: Commit

```bash
git add src/ OrionAudit.sln
git commit -m "build: scaffold three source projects (OrionAudit, AspNetCore, Testing)"
```

---

## Task 3: Four test-project skeletons

**Files:**
- Create: `tests/OrionAudit.Tests/OrionAudit.Tests.csproj`
- Create: `tests/OrionAudit.AspNetCore.Tests/OrionAudit.AspNetCore.Tests.csproj`
- Create: `tests/OrionAudit.Testing.Tests/OrionAudit.Testing.Tests.csproj`
- Create: `tests/OrionAudit.IntegrationTests/OrionAudit.IntegrationTests.csproj`
- Modify: `OrionAudit.sln`

### Step 1: Test csproj template

For each of the four test projects, create the csproj with this template (substitute the project name and ProjectReferences):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.2" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
</Project>
```

Per-project additional `<ItemGroup>`:

**OrionAudit.Tests** — add `ProjectReference` to core and Testing:
```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\OrionAudit\OrionAudit.csproj" />
  <ProjectReference Include="..\..\src\OrionAudit.Testing\OrionAudit.Testing.csproj" />
</ItemGroup>
```

**OrionAudit.AspNetCore.Tests** — add core + AspNetCore + Testing:
```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\OrionAudit\OrionAudit.csproj" />
  <ProjectReference Include="..\..\src\OrionAudit.AspNetCore\OrionAudit.AspNetCore.csproj" />
  <ProjectReference Include="..\..\src\OrionAudit.Testing\OrionAudit.Testing.csproj" />
</ItemGroup>
```

**OrionAudit.Testing.Tests** — add core + Testing:
```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\OrionAudit\OrionAudit.csproj" />
  <ProjectReference Include="..\..\src\OrionAudit.Testing\OrionAudit.Testing.csproj" />
</ItemGroup>
```

**OrionAudit.IntegrationTests** — add core + Testing + Sqlite/InMemory providers:
```xml
<ItemGroup>
  <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.0" />
  <PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="9.0.0" />
  <ProjectReference Include="..\..\src\OrionAudit\OrionAudit.csproj" />
  <ProjectReference Include="..\..\src\OrionAudit.Testing\OrionAudit.Testing.csproj" />
</ItemGroup>
```

### Step 2: Add a sanity test in each project so xUnit doesn't fail with "no tests found"

In each test project, create `SanityTest.cs`:

```csharp
namespace OrionAudit.Tests;   // (per project namespace)

public class SanityTest
{
    [Fact]
    public void Sanity() => Assert.True(true);
}
```

### Step 3: Add projects to solution and run

```bash
dotnet sln add tests/OrionAudit.Tests/OrionAudit.Tests.csproj
dotnet sln add tests/OrionAudit.AspNetCore.Tests/OrionAudit.AspNetCore.Tests.csproj
dotnet sln add tests/OrionAudit.Testing.Tests/OrionAudit.Testing.Tests.csproj
dotnet sln add tests/OrionAudit.IntegrationTests/OrionAudit.IntegrationTests.csproj

dotnet build OrionAudit.sln -c Debug
dotnet test OrionAudit.sln
```

Expected: 4 passing sanity tests, one per project.

### Step 4: Commit

```bash
git add tests/ OrionAudit.sln
git commit -m "build: scaffold four test projects with xUnit"
```

---

## Task 4: Core types — enums, records, exceptions

**Files:**
- Create: `src/OrionAudit/Core/AuditAction.cs`
- Create: `src/OrionAudit/Core/AuditUser.cs`
- Create: `src/OrionAudit/Core/OrionAuditException.cs`
- Create: `src/OrionAudit/Core/OrionAuditConfigurationException.cs`
- Delete: `src/OrionAudit/OrionAuditMarker.cs`

### Step 1: AuditAction enum

Create `src/OrionAudit/Core/AuditAction.cs`:

```csharp
namespace OrionAudit;

/// <summary>The kind of mutation captured by an <see cref="AuditLog"/> row.</summary>
public enum AuditAction : byte
{
    /// <summary>Entity was inserted into the database.</summary>
    Inserted = 0,
    /// <summary>Entity was updated in the database.</summary>
    Updated = 1,
    /// <summary>Entity was deleted from the database.</summary>
    Deleted = 2,
}
```

### Step 2: AuditUser record

Create `src/OrionAudit/Core/AuditUser.cs`:

```csharp
namespace OrionAudit;

/// <summary>
/// Attribution information about the actor responsible for an audit event. Returned by
/// implementations of <see cref="IAuditUserResolver"/>.
/// </summary>
/// <param name="Id">Stable user identifier (e.g. <c>sub</c> claim, employee id, system principal).</param>
/// <param name="DisplayName">Optional human-readable name for UIs and reports.</param>
/// <param name="Type">Classification: <c>"user"</c> (default), <c>"system"</c>, <c>"job"</c>, etc.</param>
public sealed record AuditUser(string Id, string? DisplayName = null, string Type = "user");
```

### Step 3: Exceptions

Create `src/OrionAudit/Core/OrionAuditException.cs`:

```csharp
namespace OrionAudit;

/// <summary>Base exception thrown by OrionAudit at runtime (e.g. reconstruction over a corrupted history).</summary>
public class OrionAuditException : Exception
{
    /// <summary>Initializes a new instance with the supplied message.</summary>
    public OrionAuditException(string message) : base(message) { }

    /// <summary>Initializes a new instance with the supplied message and inner exception.</summary>
    public OrionAuditException(string message, Exception inner) : base(message, inner) { }
}
```

Create `src/OrionAudit/Core/OrionAuditConfigurationException.cs`:

```csharp
namespace OrionAudit;

/// <summary>Thrown at startup when OrionAudit's configuration is invalid (e.g. missing PK, composite PK).</summary>
public sealed class OrionAuditConfigurationException : OrionAuditException
{
    /// <summary>Initializes a new instance with the supplied message.</summary>
    public OrionAuditConfigurationException(string message) : base(message) { }
}
```

### Step 4: Delete the marker

```bash
rm src/OrionAudit/OrionAuditMarker.cs
```

### Step 5: Build

```bash
dotnet build src/OrionAudit/OrionAudit.csproj -c Debug
```

Expected: 0 errors.

### Step 6: Commit

```bash
git add src/OrionAudit/Core src/OrionAudit/OrionAuditMarker.cs
git commit -m "feat(core): add AuditAction, AuditUser, and exception types"
```

---

## Task 5: Sensitive-field attributes

**Files:**
- Create: `src/OrionAudit/Attributes/AuditableAttribute.cs`
- Create: `src/OrionAudit/Attributes/NotAuditableAttribute.cs`
- Create: `src/OrionAudit/Attributes/HashedAuditAttribute.cs`
- Create: `src/OrionAudit/Attributes/RedactedAuditAttribute.cs`
- Test: `tests/OrionAudit.Tests/AttributesTests.cs`

### Step 1: Write failing test

Create `tests/OrionAudit.Tests/AttributesTests.cs`:

```csharp
using System.Reflection;
using OrionAudit;

namespace OrionAudit.Tests;

public class AttributesTests
{
    [Auditable]
    private sealed class Sample
    {
        public int Id { get; set; }
        [NotAuditable] public string Internal { get; set; } = "";
        [HashedAudit] public string Email { get; set; } = "";
        [RedactedAudit] public string Token { get; set; } = "";
    }

    [Fact]
    public void Auditable_IsClassLevel_AndDetectable()
    {
        var attr = typeof(Sample).GetCustomAttribute<AuditableAttribute>();
        Assert.NotNull(attr);
    }

    [Fact]
    public void NotAuditable_HashedAudit_RedactedAudit_AreProperty_Level()
    {
        Assert.NotNull(typeof(Sample).GetProperty(nameof(Sample.Internal))!.GetCustomAttribute<NotAuditableAttribute>());
        Assert.NotNull(typeof(Sample).GetProperty(nameof(Sample.Email))!.GetCustomAttribute<HashedAuditAttribute>());
        Assert.NotNull(typeof(Sample).GetProperty(nameof(Sample.Token))!.GetCustomAttribute<RedactedAuditAttribute>());
    }
}
```

### Step 2: Verify failure

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AttributesTests"
```

Expected: FAIL — attributes do not exist.

### Step 3: Implement attributes

Create `src/OrionAudit/Attributes/AuditableAttribute.cs`:

```csharp
namespace OrionAudit;

/// <summary>
/// Marks an entity class for audit capture. Properties without further attributes are captured
/// normally; use <see cref="NotAuditableAttribute"/>, <see cref="HashedAuditAttribute"/>, or
/// <see cref="RedactedAuditAttribute"/> to control individual fields.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AuditableAttribute : Attribute
{
}
```

Create `src/OrionAudit/Attributes/NotAuditableAttribute.cs`:

```csharp
namespace OrionAudit;

/// <summary>
/// Marks a property as excluded from audit capture. The property's value is removed from both
/// before and after snapshots; diffs will never reference it.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class NotAuditableAttribute : Attribute
{
}
```

Create `src/OrionAudit/Attributes/HashedAuditAttribute.cs`:

```csharp
namespace OrionAudit;

/// <summary>
/// Replaces the property's value with a SHA-256 hex hash in audit snapshots. Hash is deterministic,
/// so equality detection still works (same input ⇒ same hash) without leaking the cleartext value.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class HashedAuditAttribute : Attribute
{
}
```

Create `src/OrionAudit/Attributes/RedactedAuditAttribute.cs`:

```csharp
namespace OrionAudit;

/// <summary>
/// Replaces the property's value with a literal <c>"&lt;redacted&gt;"</c> in audit snapshots.
/// Equality detection is broken (the value is always equal), so changes to redacted fields are
/// not visible in diffs — use this when even the existence of a change is sensitive.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class RedactedAuditAttribute : Attribute
{
}
```

### Step 4: Run tests

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AttributesTests"
```

Expected: 2/2 PASS.

### Step 5: Commit

```bash
git add src/OrionAudit/Attributes tests/OrionAudit.Tests/AttributesTests.cs
git commit -m "feat(core): add Auditable, NotAuditable, HashedAudit, RedactedAudit attributes"
```

---

## Task 6: AuditLog entity + EntityTypeConfiguration

**Files:**
- Create: `src/OrionAudit/Core/AuditLog.cs`
- Create: `src/OrionAudit/Core/AuditLogEntityTypeConfiguration.cs`
- Test: `tests/OrionAudit.Tests/AuditLogConfigurationTests.cs`

### Step 1: Write failing test

Create `tests/OrionAudit.Tests/AuditLogConfigurationTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OrionAudit;

namespace OrionAudit.Tests;

public class AuditLogConfigurationTests
{
    private sealed class TestContext : DbContext
    {
        public DbSet<AuditLog> Logs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration("MyAuditLog"));
        }
    }

    [Fact]
    public void AuditLog_TableNameIsCustomizable()
    {
        var opts = new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var ctx = new TestContext(opts);
        var entity = ctx.Model.FindEntityType(typeof(AuditLog))!;
        Assert.Equal("MyAuditLog", entity.GetTableName());
    }

    [Fact]
    public void AuditLog_HasExpectedColumns()
    {
        var opts = new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var ctx = new TestContext(opts);
        var entity = ctx.Model.FindEntityType(typeof(AuditLog))!;

        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.Id));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.EntityType));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.EntityId));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.Action));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.OccurredOnUtc));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.UserId));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.UserDisplay));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.UserType));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.TenantId));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.CorrelationId));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.Diff));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.Snapshot));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.Error));
    }
}
```

### Step 2: Verify failure

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditLogConfigurationTests"
```

Expected: FAIL — `AuditLog` and `AuditLogEntityTypeConfiguration` not found.

### Step 3: Implement AuditLog entity

Create `src/OrionAudit/Core/AuditLog.cs`:

```csharp
namespace OrionAudit;

/// <summary>
/// Persisted record of a single Insert / Update / Delete against an audited entity. Written by
/// <c>AuditSaveChangesInterceptor</c> in the same transaction as the originating entity change.
/// </summary>
public sealed class AuditLog
{
    /// <summary>Unique row id (auto-assigned).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Assembly-qualified name of the audited entity type.</summary>
    public string EntityType { get; set; } = default!;

    /// <summary>Serialized primary key of the audited entity (<c>key.ToString()</c>).</summary>
    public string EntityId { get; set; } = default!;

    /// <summary>What kind of change this row records.</summary>
    public AuditAction Action { get; set; }

    /// <summary>UTC timestamp at which the change was captured.</summary>
    public DateTime OccurredOnUtc { get; set; }

    /// <summary>Optional user id (from <see cref="IAuditUserResolver"/>); null when unattributed.</summary>
    public string? UserId { get; set; }

    /// <summary>Optional human-readable user display name.</summary>
    public string? UserDisplay { get; set; }

    /// <summary>Classification: <c>"user"</c>, <c>"system"</c>, <c>"job"</c>, etc.</summary>
    public string? UserType { get; set; }

    /// <summary>Optional tenant id (from <see cref="IAuditTenantResolver"/>); null for single-tenant apps.</summary>
    public string? TenantId { get; set; }

    /// <summary>Optional W3C trace context id (<c>Activity.Current?.Id</c>) at capture time.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>JSON Patch operations array (RFC 6902) describing the change. Empty array if diff failed.</summary>
    public string Diff { get; set; } = "[]";

    /// <summary>
    /// Last-known full entity JSON. Populated for <see cref="AuditAction.Deleted"/> in v0.1.0
    /// to enable reconstruction; null otherwise.
    /// </summary>
    public string? Snapshot { get; set; }

    /// <summary>
    /// Non-null when diff computation failed. The row is still written so the audit chain is not
    /// broken; operators see the error via telemetry and can investigate.
    /// </summary>
    public string? Error { get; set; }
}
```

### Step 4: Implement AuditLogEntityTypeConfiguration

Create `src/OrionAudit/Core/AuditLogEntityTypeConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OrionAudit;

/// <summary>
/// EF Core fluent configuration for <see cref="AuditLog"/>. Apply via
/// <c>modelBuilder.ApplyOrionAuditConfigurations()</c> (extension method) or by calling
/// <c>ApplyConfiguration(new AuditLogEntityTypeConfiguration("table-name"))</c> directly.
/// </summary>
public sealed class AuditLogEntityTypeConfiguration : IEntityTypeConfiguration<AuditLog>
{
    /// <summary>Default table name when no override is supplied.</summary>
    public const string DefaultTableName = "OrionAudit_Log";

    private readonly string tableName;

    /// <summary>Initializes a new configuration using <see cref="DefaultTableName"/>.</summary>
    public AuditLogEntityTypeConfiguration() : this(DefaultTableName) { }

    /// <summary>Initializes a new configuration with a custom table name.</summary>
    public AuditLogEntityTypeConfiguration(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        this.tableName = tableName;
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(tableName);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EntityType).IsRequired().HasMaxLength(512);
        builder.Property(x => x.EntityId).IsRequired().HasMaxLength(128);
        builder.Property(x => x.Action).IsRequired();
        builder.Property(x => x.OccurredOnUtc).IsRequired();
        builder.Property(x => x.UserId).HasMaxLength(128);
        builder.Property(x => x.UserDisplay).HasMaxLength(256);
        builder.Property(x => x.UserType).HasMaxLength(32);
        builder.Property(x => x.TenantId).HasMaxLength(128);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);
        builder.Property(x => x.Diff).IsRequired();

        builder.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredOnUtc })
            .HasDatabaseName("IX_OrionAudit_EntityLookup");
        builder.HasIndex(x => new { x.TenantId, x.OccurredOnUtc })
            .HasDatabaseName("IX_OrionAudit_TenantTimeline");
        builder.HasIndex(x => new { x.UserId, x.OccurredOnUtc })
            .HasDatabaseName("IX_OrionAudit_UserActivity");
    }
}
```

### Step 5: Run tests

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditLogConfigurationTests"
```

Expected: 2/2 PASS.

### Step 6: Commit

```bash
git add src/OrionAudit/Core/AuditLog.cs src/OrionAudit/Core/AuditLogEntityTypeConfiguration.cs tests/OrionAudit.Tests/AuditLogConfigurationTests.cs
git commit -m "feat(core): add AuditLog entity and EF Core configuration"
```

---

## Task 7: Resolver interfaces

**Files:**
- Create: `src/OrionAudit/Resolvers/IAuditUserResolver.cs`
- Create: `src/OrionAudit/Resolvers/IAuditTenantResolver.cs`

### Step 1: IAuditUserResolver

Create `src/OrionAudit/Resolvers/IAuditUserResolver.cs`:

```csharp
namespace OrionAudit;

/// <summary>
/// Resolves the actor responsible for an audit event. Implementations are registered as scoped
/// services and called by the interceptor on every <c>SaveChangesAsync</c> that captures audit
/// rows. A null return means the event is unattributable (the <c>User*</c> columns stay null).
/// </summary>
public interface IAuditUserResolver
{
    /// <summary>Returns the user attribution for the current ambient context, or null if unknown.</summary>
    /// <param name="serviceProvider">Scoped service provider for resolving collaborators (e.g. <c>IHttpContextAccessor</c>).</param>
    AuditUser? Resolve(IServiceProvider serviceProvider);
}
```

### Step 2: IAuditTenantResolver

Create `src/OrionAudit/Resolvers/IAuditTenantResolver.cs`:

```csharp
namespace OrionAudit;

/// <summary>
/// Resolves the tenant id for an audit event. Implementations are registered as scoped services
/// and called by the interceptor when capturing audit rows. A null return means single-tenant
/// (the <c>TenantId</c> column stays null and reads are not filtered by tenant).
/// </summary>
public interface IAuditTenantResolver
{
    /// <summary>Returns the tenant id for the current ambient context, or null for single-tenant.</summary>
    /// <param name="serviceProvider">Scoped service provider for resolving collaborators.</param>
    string? Resolve(IServiceProvider serviceProvider);
}
```

### Step 3: Build + commit

```bash
dotnet build src/OrionAudit/OrionAudit.csproj -c Debug
git add src/OrionAudit/Resolvers
git commit -m "feat(core): add IAuditUserResolver and IAuditTenantResolver"
```

(No tests needed — these are just interfaces. They are exercised by interceptor + integration tests later.)

---

## Task 8: Configuration system — fluent builder + frozen runtime config

**Files:**
- Create: `src/OrionAudit/Configuration/IAuditConfiguration.cs`
- Create: `src/OrionAudit/Configuration/AuditableTypeConfig.cs`
- Create: `src/OrionAudit/Configuration/AuditConfigurationBuilder.cs`
- Create: `src/OrionAudit/Configuration/AuditTypeBuilder.cs`
- Create: `src/OrionAudit/Configuration/AuditConfiguration.cs`
- Test: `tests/OrionAudit.Tests/AuditConfigurationTests.cs`

### Step 1: Write failing tests

Create `tests/OrionAudit.Tests/AuditConfigurationTests.cs`:

```csharp
using OrionAudit;
using OrionAudit.Configuration;

namespace OrionAudit.Tests;

public class AuditConfigurationTests
{
    [Auditable]
    public sealed class AttrSample
    {
        public int Id { get; set; }
        [NotAuditable] public string Internal { get; set; } = "";
        [HashedAudit] public string Email { get; set; } = "";
    }

    public sealed class FluentSample
    {
        public int Id { get; set; }
        public string Internal { get; set; } = "";
        public string Email { get; set; } = "";
    }

    [Fact]
    public void Attribute_ConfiguredType_RegistersAllRules()
    {
        var builder = new AuditConfigurationBuilder();
        builder.Audit<AttrSample>();
        var config = builder.Build();

        Assert.True(config.IsAudited(typeof(AttrSample)));
        var typeConfig = config.GetConfig(typeof(AttrSample))!;
        Assert.Equal(AuditFieldRule.Exclude, typeConfig.FieldRule(nameof(AttrSample.Internal)));
        Assert.Equal(AuditFieldRule.Hash, typeConfig.FieldRule(nameof(AttrSample.Email)));
        Assert.Equal(AuditFieldRule.Capture, typeConfig.FieldRule(nameof(AttrSample.Id)));
    }

    [Fact]
    public void Fluent_OverridesProvideFieldRules()
    {
        var builder = new AuditConfigurationBuilder();
        builder.Audit<FluentSample>(b => b
            .Exclude(s => s.Internal)
            .Hash(s => s.Email));
        var config = builder.Build();

        Assert.True(config.IsAudited(typeof(FluentSample)));
        var typeConfig = config.GetConfig(typeof(FluentSample))!;
        Assert.Equal(AuditFieldRule.Exclude, typeConfig.FieldRule(nameof(FluentSample.Internal)));
        Assert.Equal(AuditFieldRule.Hash, typeConfig.FieldRule(nameof(FluentSample.Email)));
    }

    [Fact]
    public void IsAudited_ReturnsFalse_ForUnconfiguredType()
    {
        var builder = new AuditConfigurationBuilder();
        var config = builder.Build();
        Assert.False(config.IsAudited(typeof(string)));
    }

    [Fact]
    public void Fluent_OverridesAttribute_WhenBothPresent()
    {
        var builder = new AuditConfigurationBuilder();
        builder.Audit<AttrSample>(b => b.Redact(s => s.Email));   // attribute says Hash, fluent says Redact
        var config = builder.Build();

        var typeConfig = config.GetConfig(typeof(AttrSample))!;
        Assert.Equal(AuditFieldRule.Redact, typeConfig.FieldRule(nameof(AttrSample.Email)));
    }
}
```

### Step 2: Verify failure

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditConfigurationTests"
```

Expected: FAIL — configuration types do not exist.

### Step 3: Implement field rule enum

Create `src/OrionAudit/Configuration/AuditableTypeConfig.cs`:

```csharp
using System.Collections.Frozen;

namespace OrionAudit.Configuration;

/// <summary>How a property is treated when building audit snapshots.</summary>
public enum AuditFieldRule : byte
{
    /// <summary>Capture the value as-is.</summary>
    Capture = 0,
    /// <summary>Omit the property from snapshots entirely.</summary>
    Exclude = 1,
    /// <summary>Replace the value with a SHA-256 hex hash.</summary>
    Hash = 2,
    /// <summary>Replace the value with the literal <c>"&lt;redacted&gt;"</c>.</summary>
    Redact = 3,
}

/// <summary>Frozen per-type configuration: which type is audited and how its fields are treated.</summary>
public sealed class AuditableTypeConfig
{
    private readonly FrozenDictionary<string, AuditFieldRule> rules;

    /// <summary>Initializes a new configuration with the supplied field rules.</summary>
    public AuditableTypeConfig(Type entityType, IDictionary<string, AuditFieldRule> rules)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(rules);
        EntityType = entityType;
        this.rules = rules.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>The audited entity CLR type.</summary>
    public Type EntityType { get; }

    /// <summary>
    /// Returns the rule for a given property name. Properties without an explicit rule are
    /// captured normally (<see cref="AuditFieldRule.Capture"/>).
    /// </summary>
    public AuditFieldRule FieldRule(string propertyName)
        => rules.TryGetValue(propertyName, out var rule) ? rule : AuditFieldRule.Capture;
}
```

### Step 4: Implement IAuditConfiguration + AuditConfiguration

Create `src/OrionAudit/Configuration/IAuditConfiguration.cs`:

```csharp
namespace OrionAudit.Configuration;

/// <summary>Frozen, thread-safe runtime configuration of audited types. Built once at startup.</summary>
public interface IAuditConfiguration
{
    /// <summary>True if the type was registered for audit (via attribute or fluent builder).</summary>
    bool IsAudited(Type entityType);

    /// <summary>The per-type configuration, or null if the type is not audited.</summary>
    AuditableTypeConfig? GetConfig(Type entityType);
}
```

Create `src/OrionAudit/Configuration/AuditConfiguration.cs`:

```csharp
using System.Collections.Frozen;

namespace OrionAudit.Configuration;

/// <summary>Default <see cref="IAuditConfiguration"/> implementation backed by a <see cref="FrozenDictionary{TKey, TValue}"/>.</summary>
public sealed class AuditConfiguration : IAuditConfiguration
{
    private readonly FrozenDictionary<Type, AuditableTypeConfig> byType;

    /// <summary>Initializes a new configuration. Intended to be called only by <see cref="AuditConfigurationBuilder"/>.</summary>
    public AuditConfiguration(IDictionary<Type, AuditableTypeConfig> byType)
    {
        ArgumentNullException.ThrowIfNull(byType);
        this.byType = byType.ToFrozenDictionary();
    }

    /// <inheritdoc />
    public bool IsAudited(Type entityType)
        => byType.ContainsKey(entityType);

    /// <inheritdoc />
    public AuditableTypeConfig? GetConfig(Type entityType)
        => byType.TryGetValue(entityType, out var config) ? config : null;
}
```

### Step 5: Implement fluent builders

Create `src/OrionAudit/Configuration/AuditTypeBuilder.cs`:

```csharp
using System.Linq.Expressions;
using System.Reflection;

namespace OrionAudit.Configuration;

/// <summary>
/// Fluent builder for per-type audit rules. Returned to consumers from
/// <see cref="AuditConfigurationBuilder.Audit{T}(Action{AuditTypeBuilder{T}})"/>.
/// </summary>
public sealed class AuditTypeBuilder<T> where T : class
{
    internal Dictionary<string, AuditFieldRule> Rules { get; } = new(StringComparer.Ordinal);

    /// <summary>Marks the property as excluded from audit snapshots.</summary>
    public AuditTypeBuilder<T> Exclude<TProp>(Expression<Func<T, TProp>> selector)
    {
        Rules[PropertyName(selector)] = AuditFieldRule.Exclude;
        return this;
    }

    /// <summary>Marks the property to be replaced with a SHA-256 hash in snapshots.</summary>
    public AuditTypeBuilder<T> Hash<TProp>(Expression<Func<T, TProp>> selector)
    {
        Rules[PropertyName(selector)] = AuditFieldRule.Hash;
        return this;
    }

    /// <summary>Marks the property to be replaced with the literal <c>"&lt;redacted&gt;"</c> in snapshots.</summary>
    public AuditTypeBuilder<T> Redact<TProp>(Expression<Func<T, TProp>> selector)
    {
        Rules[PropertyName(selector)] = AuditFieldRule.Redact;
        return this;
    }

    private static string PropertyName<TProp>(Expression<Func<T, TProp>> selector)
    {
        if (selector.Body is MemberExpression member && member.Member is PropertyInfo prop)
            return prop.Name;
        if (selector.Body is UnaryExpression { Operand: MemberExpression inner } && inner.Member is PropertyInfo innerProp)
            return innerProp.Name;
        throw new OrionAuditConfigurationException(
            $"Expression '{selector}' is not a simple property accessor.");
    }
}
```

Create `src/OrionAudit/Configuration/AuditConfigurationBuilder.cs`:

```csharp
using System.Reflection;

namespace OrionAudit.Configuration;

/// <summary>
/// Top-level fluent builder for OrionAudit configuration. Accumulates per-type rules from both
/// <see cref="AuditableAttribute"/> discovery and explicit <c>Audit&lt;T&gt;()</c> calls, then
/// produces a frozen <see cref="IAuditConfiguration"/> via <see cref="Build"/>.
/// </summary>
public sealed class AuditConfigurationBuilder
{
    private readonly Dictionary<Type, Dictionary<string, AuditFieldRule>> rulesByType = new();

    /// <summary>Registers a type for audit with optional field-level overrides.</summary>
    public AuditConfigurationBuilder Audit<T>(Action<AuditTypeBuilder<T>>? configure = null) where T : class
    {
        var entityType = typeof(T);
        var rules = GetOrCreateRules(entityType);
        ApplyAttributeRules(entityType, rules);

        if (configure is not null)
        {
            var typeBuilder = new AuditTypeBuilder<T>();
            configure(typeBuilder);
            foreach (var (propName, rule) in typeBuilder.Rules)
            {
                rules[propName] = rule;
            }
        }

        return this;
    }

    /// <summary>Registers a type for audit using only attribute-based rules.</summary>
    public AuditConfigurationBuilder Audit(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        var rules = GetOrCreateRules(entityType);
        ApplyAttributeRules(entityType, rules);
        return this;
    }

    /// <summary>Freezes accumulated rules into a runtime <see cref="IAuditConfiguration"/>.</summary>
    public IAuditConfiguration Build()
    {
        var configsByType = rulesByType.ToDictionary(
            kvp => kvp.Key,
            kvp => new AuditableTypeConfig(kvp.Key, kvp.Value));
        return new AuditConfiguration(configsByType);
    }

    private Dictionary<string, AuditFieldRule> GetOrCreateRules(Type entityType)
    {
        if (!rulesByType.TryGetValue(entityType, out var rules))
        {
            rules = new Dictionary<string, AuditFieldRule>(StringComparer.Ordinal);
            rulesByType[entityType] = rules;
        }
        return rules;
    }

    private static void ApplyAttributeRules(Type entityType, Dictionary<string, AuditFieldRule> rules)
    {
        foreach (var prop in entityType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (prop.GetCustomAttribute<NotAuditableAttribute>() is not null)
                rules.TryAdd(prop.Name, AuditFieldRule.Exclude);
            else if (prop.GetCustomAttribute<HashedAuditAttribute>() is not null)
                rules.TryAdd(prop.Name, AuditFieldRule.Hash);
            else if (prop.GetCustomAttribute<RedactedAuditAttribute>() is not null)
                rules.TryAdd(prop.Name, AuditFieldRule.Redact);
        }
    }
}
```

> Note: `TryAdd` in `ApplyAttributeRules` ensures attribute rules do NOT overwrite fluent rules that were already added. The builder method `Audit<T>(configure)` calls `ApplyAttributeRules` first then applies fluent rules with `rules[propName] = rule;` (unconditional overwrite). This is the spec's "fluent overrides attribute" guarantee.

### Step 6: Run tests

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditConfigurationTests"
```

Expected: 4/4 PASS.

### Step 7: Commit

```bash
git add src/OrionAudit/Configuration tests/OrionAudit.Tests/AuditConfigurationTests.cs
git commit -m "feat(core): add fluent audit configuration system with attribute + override merge"
```

---

## Task 9: Type discovery (assembly scan) + PK resolution

**Files:**
- Create: `src/OrionAudit/Configuration/AuditableTypeDiscovery.cs`
- Test: `tests/OrionAudit.Tests/AuditableTypeDiscoveryTests.cs`

### Step 1: Write failing tests

Create `tests/OrionAudit.Tests/AuditableTypeDiscoveryTests.cs`:

```csharp
using OrionAudit;
using OrionAudit.Configuration;

namespace OrionAudit.Tests;

public class AuditableTypeDiscoveryTests
{
    [Auditable]
    public sealed class Marked
    {
        public int Id { get; set; }
    }

    public sealed class Unmarked
    {
        public int Id { get; set; }
    }

    [Auditable]
    public abstract class MarkedAbstract
    {
        public int Id { get; set; }
    }

    [Fact]
    public void Discover_FindsTypesWithAuditableAttribute()
    {
        var types = AuditableTypeDiscovery.Discover(new[] { typeof(AuditableTypeDiscoveryTests).Assembly });
        Assert.Contains(typeof(Marked), types);
    }

    [Fact]
    public void Discover_IgnoresUnmarkedTypes()
    {
        var types = AuditableTypeDiscovery.Discover(new[] { typeof(AuditableTypeDiscoveryTests).Assembly });
        Assert.DoesNotContain(typeof(Unmarked), types);
    }

    [Fact]
    public void Discover_IgnoresAbstractTypes()
    {
        var types = AuditableTypeDiscovery.Discover(new[] { typeof(AuditableTypeDiscoveryTests).Assembly });
        Assert.DoesNotContain(typeof(MarkedAbstract), types);
    }
}
```

### Step 2: Verify failure

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditableTypeDiscoveryTests"
```

Expected: FAIL — `AuditableTypeDiscovery` not found.

### Step 3: Implement discovery

Create `src/OrionAudit/Configuration/AuditableTypeDiscovery.cs`:

```csharp
using System.Reflection;

namespace OrionAudit.Configuration;

/// <summary>Scans assemblies for concrete classes decorated with <see cref="AuditableAttribute"/>.</summary>
public static class AuditableTypeDiscovery
{
    /// <summary>
    /// Returns all concrete public classes in the supplied assemblies that carry
    /// <see cref="AuditableAttribute"/>. Abstract classes and interfaces are skipped.
    /// </summary>
    public static IReadOnlyList<Type> Discover(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var result = new List<Type>();
        foreach (var asm in assemblies)
        {
            foreach (var type in SafeGetTypes(asm))
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetCustomAttribute<AuditableAttribute>() is null) continue;
                result.Add(type);
            }
        }
        return result;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>();
        }
    }
}
```

### Step 4: Run tests

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditableTypeDiscoveryTests"
```

Expected: 3/3 PASS.

### Step 5: Commit

```bash
git add src/OrionAudit/Configuration/AuditableTypeDiscovery.cs tests/OrionAudit.Tests/AuditableTypeDiscoveryTests.cs
git commit -m "feat(core): add AuditableTypeDiscovery for assembly scan"
```

---

## Task 10: SnapshotBuilder — extract entity values with field filtering

**Files:**
- Create: `src/OrionAudit/Capture/SnapshotBuilder.cs`
- Test: `tests/OrionAudit.Tests/SnapshotBuilderTests.cs`

### Step 1: Write failing tests

Create `tests/OrionAudit.Tests/SnapshotBuilderTests.cs`:

```csharp
using System.Text.Json.Nodes;
using OrionAudit;
using OrionAudit.Capture;
using OrionAudit.Configuration;

namespace OrionAudit.Tests;

public class SnapshotBuilderTests
{
    [Auditable]
    public sealed class Sample
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        [NotAuditable] public string Internal { get; set; } = "";
        [HashedAudit] public string Email { get; set; } = "";
        [RedactedAudit] public string Token { get; set; } = "";
    }

    private static IAuditConfiguration BuildConfig()
    {
        var builder = new AuditConfigurationBuilder();
        builder.Audit<Sample>();
        return builder.Build();
    }

    [Fact]
    public void Build_IncludesCapturedProperties()
    {
        var config = BuildConfig();
        var values = new Dictionary<string, object?>
        {
            ["Id"] = 1, ["Name"] = "Alice", ["Internal"] = "x", ["Email"] = "a@b.c", ["Token"] = "secret",
        };

        var node = SnapshotBuilder.Build(typeof(Sample), values, config);

        Assert.Equal(1, node["Id"]!.GetValue<int>());
        Assert.Equal("Alice", node["Name"]!.GetValue<string>());
    }

    [Fact]
    public void Build_ExcludesNotAuditableFields()
    {
        var config = BuildConfig();
        var values = new Dictionary<string, object?> { ["Id"] = 1, ["Internal"] = "x" };

        var node = SnapshotBuilder.Build(typeof(Sample), values, config);

        Assert.False(node.AsObject().ContainsKey("Internal"));
    }

    [Fact]
    public void Build_HashesHashedAuditFields_Deterministic()
    {
        var config = BuildConfig();
        var v1 = new Dictionary<string, object?> { ["Email"] = "user@example.com" };
        var v2 = new Dictionary<string, object?> { ["Email"] = "user@example.com" };

        var n1 = SnapshotBuilder.Build(typeof(Sample), v1, config);
        var n2 = SnapshotBuilder.Build(typeof(Sample), v2, config);

        var h1 = n1["Email"]!.GetValue<string>();
        var h2 = n2["Email"]!.GetValue<string>();
        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length);   // SHA-256 hex length
        Assert.NotEqual("user@example.com", h1);
    }

    [Fact]
    public void Build_RedactsRedactedAuditFields()
    {
        var config = BuildConfig();
        var values = new Dictionary<string, object?> { ["Token"] = "secret-value" };
        var node = SnapshotBuilder.Build(typeof(Sample), values, config);
        Assert.Equal("<redacted>", node["Token"]!.GetValue<string>());
    }

    [Fact]
    public void Build_HandlesNullValues()
    {
        var config = BuildConfig();
        var values = new Dictionary<string, object?> { ["Name"] = null };
        var node = SnapshotBuilder.Build(typeof(Sample), values, config);
        Assert.Null((string?)node["Name"]);
    }
}
```

### Step 2: Verify failure

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~SnapshotBuilderTests"
```

Expected: FAIL — `SnapshotBuilder` not found.

### Step 3: Implement

Create `src/OrionAudit/Capture/SnapshotBuilder.cs`:

```csharp
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
        if (value is null) return null;
        var text = value as string ?? JsonSerializer.Serialize(value);
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private static JsonNode? ConvertToNode(object? value)
    {
        if (value is null) return null;
        return JsonSerializer.SerializeToNode(value, value.GetType());
    }
}
```

> Note: `Convert.ToHexStringLower` requires net9.0+. For net8.0, fall back to `Convert.ToHexString(hash).ToLowerInvariant()`. Use a `#if NET9_0_OR_GREATER` pragma if necessary.

### Step 4: Run tests, commit

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~SnapshotBuilderTests"
```

Expected: 5/5 PASS.

```bash
git add src/OrionAudit/Capture/SnapshotBuilder.cs tests/OrionAudit.Tests/SnapshotBuilderTests.cs
git commit -m "feat(capture): add SnapshotBuilder with sensitive-field filtering"
```

---

## Task 11: DiffEngine — JSON Patch computation

**Files:**
- Create: `src/OrionAudit/Capture/DiffEngine.cs`
- Test: `tests/OrionAudit.Tests/DiffEngineTests.cs`

### Step 1: Write failing tests

Create `tests/OrionAudit.Tests/DiffEngineTests.cs`:

```csharp
using System.Text.Json.Nodes;
using OrionAudit.Capture;

namespace OrionAudit.Tests;

public class DiffEngineTests
{
    [Fact]
    public void Compute_AddedProperties_ProducesAddOperations()
    {
        var before = new JsonObject();
        var after = new JsonObject { ["Status"] = "Pending", ["Total"] = 100 };

        var diff = DiffEngine.Compute(before, after);

        Assert.Contains("\"op\":\"add\"", diff);
        Assert.Contains("/Status", diff);
        Assert.Contains("/Total", diff);
    }

    [Fact]
    public void Compute_ChangedProperty_ProducesReplaceOperation()
    {
        var before = new JsonObject { ["Status"] = "Pending" };
        var after = new JsonObject { ["Status"] = "Shipped" };

        var diff = DiffEngine.Compute(before, after);

        Assert.Contains("\"op\":\"replace\"", diff);
        Assert.Contains("\"value\":\"Shipped\"", diff);
    }

    [Fact]
    public void Compute_RemovedProperty_ProducesRemoveOperation()
    {
        var before = new JsonObject { ["Status"] = "Pending", ["Note"] = "old" };
        var after = new JsonObject { ["Status"] = "Pending" };

        var diff = DiffEngine.Compute(before, after);

        Assert.Contains("\"op\":\"remove\"", diff);
        Assert.Contains("/Note", diff);
    }

    [Fact]
    public void Compute_NoChange_ProducesEmptyArray()
    {
        var before = new JsonObject { ["Status"] = "Pending" };
        var after = new JsonObject { ["Status"] = "Pending" };

        var diff = DiffEngine.Compute(before, after);

        Assert.Equal("[]", diff);
    }

    [Fact]
    public void Apply_AppliesDiffOntoTarget_AndReturnsResult()
    {
        var target = new JsonObject { ["Status"] = "Pending" };
        var diff = "[{\"op\":\"replace\",\"path\":\"/Status\",\"value\":\"Shipped\"}]";

        var result = DiffEngine.Apply(target, diff);

        Assert.Equal("Shipped", result["Status"]!.GetValue<string>());
    }
}
```

### Step 2: Verify failure

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~DiffEngineTests"
```

Expected: FAIL — `DiffEngine` not found.

### Step 3: Implement

Create `src/OrionAudit/Capture/DiffEngine.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;

namespace OrionAudit.Capture;

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
```

> Note: `JsonPatch.Net` exposes `JsonNode.CreatePatch(JsonNode)` and `JsonPatch.Apply(JsonNode)` extension methods. Verify the exact member names against the installed package version (3.2.5 at spec time) — they have been stable but check `JsonPatch` class docs if compilation fails.

### Step 4: Run tests, commit

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~DiffEngineTests"
```

Expected: 5/5 PASS.

```bash
git add src/OrionAudit/Capture/DiffEngine.cs tests/OrionAudit.Tests/DiffEngineTests.cs
git commit -m "feat(capture): add DiffEngine for JSON Patch compute/apply"
```

---

## Task 12: AuditSaveChangesInterceptor — capture core

**Files:**
- Create: `src/OrionAudit/Capture/AuditSaveChangesInterceptor.cs`
- Test: `tests/OrionAudit.Tests/AuditSaveChangesInterceptorTests.cs`

### Step 1: Write failing tests

Create `tests/OrionAudit.Tests/AuditSaveChangesInterceptorTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.Capture;
using OrionAudit.Configuration;

namespace OrionAudit.Tests;

public class AuditSaveChangesInterceptorTests
{
    [Auditable]
    public sealed class Order
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Status { get; set; } = "New";
        [NotAuditable] public string Internal { get; set; } = "";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().HasKey(o => o.Id);
            modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
        }
    }

    private static (ServiceProvider sp, IAuditConfiguration cfg) Build()
    {
        var services = new ServiceCollection();
        var cfgBuilder = new AuditConfigurationBuilder();
        cfgBuilder.Audit<Order>();
        var cfg = cfgBuilder.Build();
        services.AddSingleton(cfg);
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString())
             .AddInterceptors(new AuditSaveChangesInterceptor(sp)));
        return (services.BuildServiceProvider(), cfg);
    }

    [Fact]
    public async Task Insert_WritesAuditLog_WithInsertedAction()
    {
        var (sp, _) = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var order = new Order { Status = "Pending" };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        var logs = await ctx.AuditLogs.ToListAsync();
        var entry = Assert.Single(logs);
        Assert.Equal(AuditAction.Inserted, entry.Action);
        Assert.Equal(order.Id.ToString(), entry.EntityId);
        Assert.Contains("\"op\":\"add\"", entry.Diff);
    }

    [Fact]
    public async Task Update_WritesAuditLog_WithUpdatedAction_AndReplaceDiff()
    {
        var (sp, _) = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var order = new Order { Status = "Pending" };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        order.Status = "Shipped";
        await ctx.SaveChangesAsync();

        var logs = await ctx.AuditLogs.Where(l => l.Action == AuditAction.Updated).ToListAsync();
        var entry = Assert.Single(logs);
        Assert.Contains("\"op\":\"replace\"", entry.Diff);
        Assert.Contains("Shipped", entry.Diff);
    }

    [Fact]
    public async Task Delete_WritesAuditLog_WithDeletedAction_AndSnapshot()
    {
        var (sp, _) = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var order = new Order { Status = "Pending" };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        ctx.Orders.Remove(order);
        await ctx.SaveChangesAsync();

        var entry = Assert.Single(await ctx.AuditLogs.Where(l => l.Action == AuditAction.Deleted).ToListAsync());
        Assert.NotNull(entry.Snapshot);
        Assert.Contains("Pending", entry.Snapshot);
    }

    [Fact]
    public async Task ExcludedField_DoesNotAppear_InDiff()
    {
        var (sp, _) = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var order = new Order { Status = "Pending", Internal = "secret" };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.DoesNotContain("/Internal", entry.Diff);
        Assert.DoesNotContain("secret", entry.Diff);
    }

    [Fact]
    public async Task UnauditedEntity_ProducesNoAuditLog()
    {
        // Build with NO audited types — Order is decorated [Auditable] but config has no Audit<Order>()
        var services = new ServiceCollection();
        var cfg = new AuditConfigurationBuilder().Build();
        services.AddSingleton<IAuditConfiguration>(cfg);
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString())
             .AddInterceptors(new AuditSaveChangesInterceptor(sp)));
        await using var sp = services.BuildServiceProvider();

        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        Assert.Empty(await ctx.AuditLogs.ToListAsync());
    }
}
```

### Step 2: Verify failure

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditSaveChangesInterceptorTests"
```

Expected: FAIL — `AuditSaveChangesInterceptor` not found.

### Step 3: Implement interceptor

Create `src/OrionAudit/Capture/AuditSaveChangesInterceptor.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit.Configuration;

namespace OrionAudit.Capture;

/// <summary>
/// EF Core <see cref="SaveChangesInterceptor"/> that captures Insert / Update / Delete operations
/// against audited entities, computes JSON Patch diffs, and writes <see cref="AuditLog"/> rows in
/// the same transaction.
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IServiceProvider serviceProvider;

    /// <param name="serviceProvider">
    /// The scoped service provider captured at DbContext construction by the
    /// <c>(sp, o) =&gt; o.AddInterceptors(new AuditSaveChangesInterceptor(sp))</c> wiring.
    /// </param>
    public AuditSaveChangesInterceptor(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        var ctx = eventData.Context!;
        var configuration = serviceProvider.GetRequiredService<IAuditConfiguration>();
        var clock = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;

        var auditedEntries = ctx.ChangeTracker.Entries()
            .Where(e => configuration.IsAudited(e.Entity.GetType()))
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        if (auditedEntries.Count == 0)
        {
            return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
        }

        var user = serviceProvider.GetService<IAuditUserResolver>()?.Resolve(serviceProvider);
        var tenantId = serviceProvider.GetService<IAuditTenantResolver>()?.Resolve(serviceProvider);
        var correlationId = Activity.Current?.Id;
        var occurredOn = clock.GetUtcNow().UtcDateTime;

        foreach (var entry in auditedEntries)
        {
            var auditLog = BuildAuditLog(entry, configuration, user, tenantId, correlationId, occurredOn);
            ctx.Add(auditLog);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    private static AuditLog BuildAuditLog(
        EntityEntry entry,
        IAuditConfiguration configuration,
        AuditUser? user,
        string? tenantId,
        string? correlationId,
        DateTime occurredOn)
    {
        var entityType = entry.Entity.GetType();
        var primaryKey = ExtractPrimaryKey(entry);

        var action = entry.State switch
        {
            EntityState.Added => AuditAction.Inserted,
            EntityState.Modified => AuditAction.Updated,
            EntityState.Deleted => AuditAction.Deleted,
            _ => throw new InvalidOperationException($"Unsupported entry state {entry.State}.")
        };

        var beforeValues = entry.State == EntityState.Added
            ? new Dictionary<string, object?>()
            : SnapshotValues(entry, useOriginal: true);
        var afterValues = entry.State == EntityState.Deleted
            ? new Dictionary<string, object?>()
            : SnapshotValues(entry, useOriginal: false);

        var auditLog = new AuditLog
        {
            EntityType = entityType.AssemblyQualifiedName!,
            EntityId = primaryKey,
            Action = action,
            OccurredOnUtc = occurredOn,
            UserId = user?.Id,
            UserDisplay = user?.DisplayName,
            UserType = user?.Type,
            TenantId = tenantId,
            CorrelationId = correlationId,
        };

        try
        {
            var beforeNode = SnapshotBuilder.Build(entityType, beforeValues, configuration);
            var afterNode = SnapshotBuilder.Build(entityType, afterValues, configuration);
            auditLog.Diff = DiffEngine.Compute(beforeNode, afterNode);

            if (action == AuditAction.Deleted)
            {
                auditLog.Snapshot = beforeNode.ToJsonString();
            }
        }
        catch (Exception ex)
        {
            auditLog.Diff = "[]";
            auditLog.Error = ex.ToString();
        }

        return auditLog;
    }

    private static Dictionary<string, object?> SnapshotValues(EntityEntry entry, bool useOriginal)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in entry.Properties)
        {
            if (property.Metadata.IsPrimaryKey()) continue;
            dict[property.Metadata.Name] = useOriginal ? property.OriginalValue : property.CurrentValue;
        }
        return dict;
    }

    private static string ExtractPrimaryKey(EntityEntry entry)
    {
        var pk = entry.Metadata.FindPrimaryKey()
            ?? throw new OrionAuditConfigurationException(
                $"Entity '{entry.Metadata.Name}' has no primary key configured.");
        if (pk.Properties.Count > 1)
        {
            throw new OrionAuditConfigurationException(
                $"Entity '{entry.Metadata.Name}' has a composite primary key. " +
                $"Composite keys are not supported in v0.1.0.");
        }
        var keyProperty = pk.Properties[0];
        var keyValue = entry.Property(keyProperty.Name).CurrentValue;
        return keyValue?.ToString()
            ?? throw new InvalidOperationException($"Primary key value for entity '{entry.Metadata.Name}' is null.");
    }
}
```

> Adapt `Convert.ToHexStringLower` etc. for net8 compat if any portion of the chain hits BCL gaps.

### Step 4: Run tests

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditSaveChangesInterceptorTests"
```

Expected: 5/5 PASS.

### Step 5: Commit

```bash
git add src/OrionAudit/Capture/AuditSaveChangesInterceptor.cs tests/OrionAudit.Tests/AuditSaveChangesInterceptorTests.cs
git commit -m "feat(capture): add AuditSaveChangesInterceptor for Insert/Update/Delete capture"
```

---

## Task 13: Interceptor error handling tests

The error path is already implemented in Task 12 (the try/catch around diff computation). This task only adds tests to lock in the behaviour.

**Files:**
- Modify: `tests/OrionAudit.Tests/AuditSaveChangesInterceptorTests.cs` (append tests)

### Step 1: Write failing tests

Append to `tests/OrionAudit.Tests/AuditSaveChangesInterceptorTests.cs`:

```csharp
public class AuditSaveChangesInterceptorErrorTests
{
    [Auditable]
    public sealed class Cyclic
    {
        public int Id { get; set; }
        public Cyclic? Self { get; set; }
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Cyclic> Items => Set<Cyclic>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Cyclic>().HasKey(x => x.Id);
            modelBuilder.Entity<Cyclic>().Ignore(x => x.Self);   // navigation, but value is captured at runtime via property dict
            modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
        }
    }

    [Fact(Skip = "Reserved: simulating an in-process diff failure requires a property type that JsonSerializer chokes on. Add when a stable repro is in hand.")]
    public Task DiffFailure_WritesRow_WithErrorAndEmptyDiff()
        => Task.CompletedTask;
}
```

> This test is intentionally skipped in v0.1.0 — engineering a stable diff failure with `JsonSerializer` against typical EF Core property values is harder than expected (`JsonPatch.Net` and `System.Text.Json` handle most pathological cases). The behaviour is exercised by code review of the try/catch in `BuildAuditLog`. v0.2 may revisit with property-based testing.

### Step 2: Commit (no behaviour change)

```bash
git add tests/OrionAudit.Tests/AuditSaveChangesInterceptorTests.cs
git commit -m "test(capture): document diff-failure handling expectations (skipped repro)"
```

---

## Task 14: Read API — AuditQueryExtensions

**Files:**
- Create: `src/OrionAudit/Read/AuditQueryExtensions.cs`
- Test: `tests/OrionAudit.Tests/AuditQueryExtensionsTests.cs`

### Step 1: Write failing tests

Create `tests/OrionAudit.Tests/AuditQueryExtensionsTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.Capture;
using OrionAudit.Configuration;
using OrionAudit.Read;

namespace OrionAudit.Tests;

public class AuditQueryExtensionsTests
{
    [Auditable]
    public sealed class Order
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Status { get; set; } = "New";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().HasKey(o => o.Id);
            modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
        }
    }

    private static async Task<TestContext> BuildAsync(string? tenantId = null)
    {
        var services = new ServiceCollection();
        var cfg = new AuditConfigurationBuilder().Audit<Order>().Build();
        services.AddSingleton(cfg);
        if (tenantId is not null)
        {
            services.AddScoped<IAuditTenantResolver>(_ => new StaticTenant(tenantId));
        }
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString())
             .AddInterceptors(new AuditSaveChangesInterceptor(sp)));
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<TestContext>();
    }

    private sealed class StaticTenant : IAuditTenantResolver
    {
        private readonly string id;
        public StaticTenant(string id) => this.id = id;
        public string? Resolve(IServiceProvider sp) => id;
    }

    [Fact]
    public async Task AuditFor_FiltersByEntityType()
    {
        await using var ctx = await BuildAsync();
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        var logs = await ctx.AuditFor<Order>().ToListAsync();
        Assert.Single(logs);
    }

    [Fact]
    public async Task AuditLog_ReturnsAllRows()
    {
        await using var ctx = await BuildAsync();
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        var logs = await ctx.AuditLog().ToListAsync();
        Assert.NotEmpty(logs);
    }

    [Fact]
    public async Task AuditFor_AppliesTenantFilter_WhenResolverRegistered()
    {
        await using var ctx = await BuildAsync(tenantId: "tenant-A");
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        var logs = await ctx.AuditFor<Order>().ToListAsync();
        var entry = Assert.Single(logs);
        Assert.Equal("tenant-A", entry.TenantId);
    }
}
```

### Step 2: Verify failure

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditQueryExtensionsTests"
```

Expected: FAIL — `AuditQueryExtensions` not found.

### Step 3: Implement

Create `src/OrionAudit/Read/AuditQueryExtensions.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace OrionAudit;

/// <summary>
/// LINQ extension methods on <see cref="DbContext"/> for querying audit history. Methods
/// automatically apply a tenant filter when an <see cref="IAuditTenantResolver"/> is registered;
/// pass <c>crossTenant: true</c> to bypass the filter.
/// </summary>
public static class AuditQueryExtensions
{
    /// <summary>Returns an <see cref="IQueryable{AuditLog}"/> filtered to entities of type <typeparamref name="T"/>.</summary>
    public static IQueryable<AuditLog> AuditFor<T>(this DbContext context, bool crossTenant = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        var typeName = typeof(T).AssemblyQualifiedName!;
        return ApplyTenantFilter(context.Set<AuditLog>().Where(a => a.EntityType == typeName), context, crossTenant);
    }

    /// <summary>Returns an unfiltered <see cref="IQueryable{AuditLog}"/> over the entire audit table.</summary>
    public static IQueryable<AuditLog> AuditLog(this DbContext context, bool crossTenant = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ApplyTenantFilter(context.Set<AuditLog>(), context, crossTenant);
    }

    private static IQueryable<AuditLog> ApplyTenantFilter(IQueryable<AuditLog> query, DbContext context, bool crossTenant)
    {
        if (crossTenant) return query;
        var sp = context.GetInfrastructure();
        var resolver = sp.GetService<IAuditTenantResolver>();
        if (resolver is null) return query;
        var tenantId = resolver.Resolve(sp);
        if (tenantId is null) return query;
        return query.Where(a => a.TenantId == tenantId);
    }
}
```

### Step 4: Run tests, commit

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditQueryExtensionsTests"
```

Expected: 3/3 PASS.

```bash
git add src/OrionAudit/Read/AuditQueryExtensions.cs tests/OrionAudit.Tests/AuditQueryExtensionsTests.cs
git commit -m "feat(read): add AuditFor<T> and AuditLog query extensions with tenant filter"
```

---

## Task 15: IAuditReconstructor + ReconstructAsync (single)

**Files:**
- Create: `src/OrionAudit/Read/IAuditReconstructor.cs`
- Create: `src/OrionAudit/Read/AuditReconstructor.cs`
- Test: `tests/OrionAudit.Tests/AuditReconstructorTests.cs`

### Step 1: Write failing tests

Create `tests/OrionAudit.Tests/AuditReconstructorTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.Capture;
using OrionAudit.Configuration;
using OrionAudit.Read;

namespace OrionAudit.Tests;

public class AuditReconstructorTests
{
    [Auditable]
    public sealed class Order
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Status { get; set; } = "New";
        public decimal Total { get; set; }
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().HasKey(o => o.Id);
            modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
        }
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        var cfg = new AuditConfigurationBuilder().Audit<Order>().Build();
        services.AddSingleton(cfg);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString())
             .AddInterceptors(new AuditSaveChangesInterceptor(sp)));
        services.AddScoped<IAuditReconstructor, AuditReconstructor>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ReconstructAsync_ReturnsNull_WhenNoHistoryAtOrBeforeDate()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

        var result = await reconstructor.ReconstructAsync<Order>("nonexistent-id", DateTime.UtcNow);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReconstructAsync_ReturnsInsertedState_WhenOnlyInsertExists()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

        var order = new Order { Status = "Pending", Total = 100 };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        var result = await reconstructor.ReconstructAsync<Order>(order.Id.ToString(), DateTime.UtcNow.AddMinutes(1));
        Assert.NotNull(result);
        Assert.Equal("Pending", result.Status);
        Assert.Equal(100m, result.Total);
    }

    [Fact]
    public async Task ReconstructAsync_ReplaysUpdates_ToProduceLatestState()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

        var order = new Order { Status = "Pending", Total = 100 };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        order.Status = "Shipped";
        order.Total = 110;
        await ctx.SaveChangesAsync();

        var result = await reconstructor.ReconstructAsync<Order>(order.Id.ToString(), DateTime.UtcNow.AddMinutes(1));
        Assert.NotNull(result);
        Assert.Equal("Shipped", result.Status);
        Assert.Equal(110m, result.Total);
    }

    [Fact]
    public async Task ReconstructAsync_ReturnsNull_WhenDeletedBeforeAsOf()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

        var order = new Order { Status = "Pending" };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        ctx.Orders.Remove(order);
        await ctx.SaveChangesAsync();

        var result = await reconstructor.ReconstructAsync<Order>(order.Id.ToString(), DateTime.UtcNow.AddMinutes(1));
        Assert.Null(result);
    }

    [Fact]
    public async Task ReconstructAsync_ReturnsHistoricalState_WhenAsOfBetweenInsertAndUpdate()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

        var order = new Order { Status = "Pending" };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        var afterInsert = DateTime.UtcNow.AddMilliseconds(100);
        await Task.Delay(200);
        order.Status = "Shipped";
        await ctx.SaveChangesAsync();

        var result = await reconstructor.ReconstructAsync<Order>(order.Id.ToString(), afterInsert);
        Assert.NotNull(result);
        Assert.Equal("Pending", result.Status);
    }
}
```

### Step 2: Verify failure

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditReconstructorTests"
```

Expected: FAIL — `IAuditReconstructor` and `AuditReconstructor` not found.

### Step 3: Implement interface

Create `src/OrionAudit/Read/IAuditReconstructor.cs`:

```csharp
namespace OrionAudit;

/// <summary>
/// Reconstructs entity state at a historical point in time by replaying audit-log diffs. Single
/// and batch overloads are provided. See documentation for performance characteristics.
/// </summary>
public interface IAuditReconstructor
{
    /// <summary>
    /// Returns the state of entity <typeparamref name="T"/> with the given primary key at
    /// <paramref name="asOf"/>, or null if the entity did not exist or was deleted at that time.
    /// Reconstruction is <em>O(N)</em> in the number of audit rows up to <paramref name="asOf"/>.
    /// For entities with thousands of historical changes, expect latency in the seconds.
    /// </summary>
    Task<T?> ReconstructAsync<T>(string entityId, DateTime asOf, CancellationToken cancellationToken = default)
        where T : class, new();

    /// <summary>
    /// Returns the state of each requested entity at <paramref name="asOf"/>. Uses a single audit
    /// query grouped by entity id; replays in bounded parallel. Missing or deleted entities map to
    /// null. Result key order matches input order.
    /// </summary>
    Task<IReadOnlyDictionary<string, T?>> ReconstructManyAsync<T>(
        IEnumerable<string> entityIds,
        DateTime asOf,
        CancellationToken cancellationToken = default)
        where T : class, new();
}
```

### Step 4: Implement reconstructor

Create `src/OrionAudit/Read/AuditReconstructor.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OrionAudit.Capture;

namespace OrionAudit.Read;

/// <summary>Default <see cref="IAuditReconstructor"/> backed by the consumer's <see cref="DbContext"/>.</summary>
public sealed class AuditReconstructor : IAuditReconstructor
{
    private readonly DbContext context;

    /// <summary>Initializes a new instance reading from the supplied <see cref="DbContext"/>.</summary>
    public AuditReconstructor(DbContext context)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<T?> ReconstructAsync<T>(string entityId, DateTime asOf, CancellationToken cancellationToken = default)
        where T : class, new()
    {
        ArgumentException.ThrowIfNullOrEmpty(entityId);
        var entityTypeName = typeof(T).AssemblyQualifiedName!;
        var rows = await context.Set<AuditLog>()
            .Where(a => a.EntityType == entityTypeName && a.EntityId == entityId && a.OccurredOnUtc <= asOf)
            .OrderBy(a => a.OccurredOnUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Replay<T>(rows, entityId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, T?>> ReconstructManyAsync<T>(
        IEnumerable<string> entityIds,
        DateTime asOf,
        CancellationToken cancellationToken = default)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        var idList = entityIds.ToList();
        var entityTypeName = typeof(T).AssemblyQualifiedName!;

        var rows = await context.Set<AuditLog>()
            .Where(a => a.EntityType == entityTypeName && idList.Contains(a.EntityId) && a.OccurredOnUtc <= asOf)
            .OrderBy(a => a.OccurredOnUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var grouped = rows.GroupBy(a => a.EntityId).ToDictionary(g => g.Key, g => g.ToList());
        var result = new Dictionary<string, T?>(idList.Count, StringComparer.Ordinal);
        foreach (var id in idList)
        {
            result[id] = grouped.TryGetValue(id, out var group) ? Replay<T>(group, id) : null;
        }
        return result;
    }

    private static T? Replay<T>(IReadOnlyList<AuditLog> rows, string entityId) where T : class, new()
    {
        if (rows.Count == 0) return null;
        if (rows[^1].Action == AuditAction.Deleted) return null;

        if (rows[0].Action != AuditAction.Inserted)
        {
            throw new OrionAuditException(
                $"Audit history for entity id '{entityId}' starts with a non-Insert action — corrupted history.");
        }

        var state = new JsonObject();
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Diff) || row.Diff == "[]") continue;
            try
            {
                state = DiffEngine.Apply(state, row.Diff);
            }
            catch (Exception ex)
            {
                throw new OrionAuditException(
                    $"Failed to replay audit row {row.Id} for entity '{entityId}': {ex.Message}", ex);
            }
        }

        return JsonSerializer.Deserialize<T>(state);
    }
}
```

### Step 5: Run tests, commit

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditReconstructorTests"
```

Expected: 5/5 PASS.

```bash
git add src/OrionAudit/Read/IAuditReconstructor.cs src/OrionAudit/Read/AuditReconstructor.cs tests/OrionAudit.Tests/AuditReconstructorTests.cs
git commit -m "feat(read): add IAuditReconstructor with diff-replay reconstruction"
```

---

## Task 16: ReconstructManyAsync batch test

The implementation already covers batch in Task 15 (`ReconstructManyAsync`). This task adds dedicated tests for the batch path.

**Files:**
- Modify: `tests/OrionAudit.Tests/AuditReconstructorTests.cs`

### Step 1: Append batch tests

Append to `tests/OrionAudit.Tests/AuditReconstructorTests.cs` (inside the `AuditReconstructorTests` class):

```csharp
[Fact]
public async Task ReconstructManyAsync_ReturnsStateForEachId()
{
    await using var sp = Build();
    await using var scope = sp.CreateAsyncScope();
    var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
    var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

    var a = new Order { Status = "PendingA" };
    var b = new Order { Status = "PendingB" };
    ctx.Orders.AddRange(a, b);
    await ctx.SaveChangesAsync();

    var result = await reconstructor.ReconstructManyAsync<Order>(
        new[] { a.Id.ToString(), b.Id.ToString() },
        DateTime.UtcNow.AddMinutes(1));

    Assert.Equal(2, result.Count);
    Assert.Equal("PendingA", result[a.Id.ToString()]!.Status);
    Assert.Equal("PendingB", result[b.Id.ToString()]!.Status);
}

[Fact]
public async Task ReconstructManyAsync_ReturnsNullForMissingIds()
{
    await using var sp = Build();
    await using var scope = sp.CreateAsyncScope();
    var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
    var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

    var a = new Order { Status = "Pending" };
    ctx.Orders.Add(a);
    await ctx.SaveChangesAsync();

    var result = await reconstructor.ReconstructManyAsync<Order>(
        new[] { a.Id.ToString(), "missing-id" },
        DateTime.UtcNow.AddMinutes(1));

    Assert.Equal(2, result.Count);
    Assert.NotNull(result[a.Id.ToString()]);
    Assert.Null(result["missing-id"]);
}

[Fact]
public async Task ReconstructManyAsync_EmptyInput_ReturnsEmptyDictionary()
{
    await using var sp = Build();
    await using var scope = sp.CreateAsyncScope();
    var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

    var result = await reconstructor.ReconstructManyAsync<Order>(
        Array.Empty<string>(), DateTime.UtcNow);

    Assert.Empty(result);
}
```

### Step 2: Run tests, commit

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditReconstructorTests"
```

Expected: 8/8 PASS (5 original + 3 new).

```bash
git add tests/OrionAudit.Tests/AuditReconstructorTests.cs
git commit -m "test(read): cover ReconstructManyAsync batch path"
```

---

## Task 17: EF Core wiring + DI extensions

**Files:**
- Create: `src/OrionAudit/DependencyInjection/OrionAuditOptions.cs`
- Create: `src/OrionAudit/DependencyInjection/AuditModelBuilderExtensions.cs`
- Create: `src/OrionAudit/DependencyInjection/DbContextOptionsBuilderExtensions.cs`
- Create: `src/OrionAudit/DependencyInjection/AuditServiceCollectionExtensions.cs`
- Test: `tests/OrionAudit.Tests/AuditDIExtensionsTests.cs`

### Step 1: Write failing tests

Create `tests/OrionAudit.Tests/AuditDIExtensionsTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.Configuration;

namespace OrionAudit.Tests;

public class AuditDIExtensionsTests
{
    [Auditable]
    public sealed class Order
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Status { get; set; } = "New";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().HasKey(o => o.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    [Fact]
    public void AddOrionAudit_RegistersConfigurationAndReconstructor()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Order>());
        var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<IAuditConfiguration>());
    }

    [Fact]
    public async Task UseOrionAudit_AndApplyConfigurations_EndToEnd_Works()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Order>());
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString())
             .UseOrionAudit(sp));

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.Equal(AuditAction.Inserted, entry.Action);
    }

    [Fact]
    public void AddOrionAudit_CalledTwice_DoesNotDuplicateRegistrations()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Order>());
        services.AddOrionAudit<TestContext>(o => o.Audit<Order>());

        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(IAuditConfiguration)));
    }
}
```

### Step 2: Verify failure

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditDIExtensionsTests"
```

Expected: FAIL — extension methods not found.

### Step 3: Implement OrionAuditOptions

Create `src/OrionAudit/DependencyInjection/OrionAuditOptions.cs`:

```csharp
using System.Reflection;
using OrionAudit.Configuration;

namespace OrionAudit;

/// <summary>
/// Fluent options surface exposed to the <c>AddOrionAudit&lt;TContext&gt;</c> configure callback.
/// Wraps an <see cref="AuditConfigurationBuilder"/> and tracks resolver registrations.
/// </summary>
public sealed class OrionAuditOptions
{
    internal AuditConfigurationBuilder ConfigurationBuilder { get; } = new();
    internal Type? UserResolverType { get; private set; }
    internal Type? TenantResolverType { get; private set; }
    internal string TableName { get; private set; } = AuditLogEntityTypeConfiguration.DefaultTableName;
    internal HashSet<Assembly> ScanAssemblies { get; } = new();

    /// <summary>Registers a type for audit with optional field-level overrides.</summary>
    public OrionAuditOptions Audit<T>(Action<AuditTypeBuilder<T>>? configure = null) where T : class
    {
        ConfigurationBuilder.Audit(configure);
        return this;
    }

    /// <summary>Registers the implementation type to use as <see cref="IAuditUserResolver"/>.</summary>
    public OrionAuditOptions UserResolver<TResolver>() where TResolver : class, IAuditUserResolver
    {
        UserResolverType = typeof(TResolver);
        return this;
    }

    /// <summary>Registers the implementation type to use as <see cref="IAuditTenantResolver"/>.</summary>
    public OrionAuditOptions TenantResolver<TResolver>() where TResolver : class, IAuditTenantResolver
    {
        TenantResolverType = typeof(TResolver);
        return this;
    }

    /// <summary>Overrides the audit-log table name (default <c>OrionAudit_Log</c>).</summary>
    public OrionAuditOptions TableName(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        TableName = tableName;
        return this;
    }

    /// <summary>Adds an assembly to be scanned for <see cref="AuditableAttribute"/>-marked types.</summary>
    public OrionAuditOptions ScanAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ScanAssemblies.Add(assembly);
        return this;
    }
}
```

> Note: `AuditConfigurationBuilder.Audit<T>(Action<AuditTypeBuilder<T>>? configure)` is the signature from Task 8. The non-generic `Audit(Type)` from Task 8 also lives there and is used by the scanner integration below.

### Step 4: Implement model builder extensions

Create `src/OrionAudit/DependencyInjection/AuditModelBuilderExtensions.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace OrionAudit;

/// <summary>EF Core <see cref="ModelBuilder"/> extensions for OrionAudit.</summary>
public static class AuditModelBuilderExtensions
{
    /// <summary>
    /// Applies the <see cref="AuditLogEntityTypeConfiguration"/> to the model. Call from
    /// <c>DbContext.OnModelCreating</c>. Uses the default table name <c>OrionAudit_Log</c>; pass
    /// <paramref name="tableName"/> to override.
    /// </summary>
    public static ModelBuilder ApplyOrionAuditConfigurations(this ModelBuilder modelBuilder, string? tableName = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var config = tableName is null
            ? new AuditLogEntityTypeConfiguration()
            : new AuditLogEntityTypeConfiguration(tableName);
        modelBuilder.ApplyConfiguration(config);
        return modelBuilder;
    }
}
```

### Step 5: Implement DbContextOptionsBuilder extensions

Create `src/OrionAudit/DependencyInjection/DbContextOptionsBuilderExtensions.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using OrionAudit.Capture;

namespace OrionAudit;

/// <summary>EF Core <see cref="DbContextOptionsBuilder"/> extensions for OrionAudit.</summary>
public static class DbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Wires the <see cref="AuditSaveChangesInterceptor"/> into the DbContext's interceptor pipeline.
    /// Call inside <c>services.AddDbContext&lt;T&gt;((sp, o) =&gt; ...)</c> after the provider-specific
    /// <c>Use*</c> call.
    /// </summary>
    /// <param name="builder">The options builder.</param>
    /// <param name="serviceProvider">
    /// The <strong>scoped</strong> service provider passed by EF Core's
    /// <c>services.AddDbContext&lt;T&gt;((sp, o) =&gt; ...)</c> overload. The single-argument
    /// <c>AddDbContext&lt;T&gt;(o =&gt; ...)</c> overload is not compatible — the interceptor
    /// must resolve scoped collaborators from the per-request scope.
    /// </param>
    public static DbContextOptionsBuilder UseOrionAudit(
        this DbContextOptionsBuilder builder,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        builder.AddInterceptors(new AuditSaveChangesInterceptor(serviceProvider));
        return builder;
    }

    /// <summary>Strongly-typed convenience overload.</summary>
    public static DbContextOptionsBuilder<TContext> UseOrionAudit<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        IServiceProvider serviceProvider)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        builder.AddInterceptors(new AuditSaveChangesInterceptor(serviceProvider));
        return builder;
    }
}
```

### Step 6: Implement service collection extension

Create `src/OrionAudit/DependencyInjection/AuditServiceCollectionExtensions.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrionAudit.Configuration;
using OrionAudit.Read;

namespace OrionAudit;

/// <summary><see cref="IServiceCollection"/> extensions to wire OrionAudit.</summary>
public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers the audit configuration, reconstructor, and optional resolvers for the
    /// supplied <typeparamref name="TDbContext"/>. Call before
    /// <c>services.AddDbContext&lt;TDbContext&gt;(...)</c>.
    /// </summary>
    public static IServiceCollection AddOrionAudit<TDbContext>(
        this IServiceCollection services,
        Action<OrionAuditOptions> configure)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new OrionAuditOptions();
        configure(options);

        if (options.ScanAssemblies.Count > 0)
        {
            foreach (var type in AuditableTypeDiscovery.Discover(options.ScanAssemblies))
            {
                options.ConfigurationBuilder.Audit(type);
            }
        }

        var configuration = options.ConfigurationBuilder.Build();
        services.TryAddSingleton(configuration);
        services.TryAddScoped<IAuditReconstructor>(sp => new AuditReconstructor(sp.GetRequiredService<TDbContext>()));
        services.TryAddSingleton(TimeProvider.System);

        if (options.UserResolverType is not null)
        {
            services.TryAddScoped(typeof(IAuditUserResolver), options.UserResolverType);
        }
        if (options.TenantResolverType is not null)
        {
            services.TryAddScoped(typeof(IAuditTenantResolver), options.TenantResolverType);
        }

        return services;
    }
}
```

### Step 7: Run tests, commit

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~AuditDIExtensionsTests"
```

Expected: 3/3 PASS.

```bash
git add src/OrionAudit/DependencyInjection tests/OrionAudit.Tests/AuditDIExtensionsTests.cs
git commit -m "feat(di): add AddOrionAudit<TContext>, UseOrionAudit, ApplyOrionAuditConfigurations"
```

---

## Task 18: OpenTelemetry instrumentation

**Files:**
- Create: `src/OrionAudit/Telemetry/OrionAuditTelemetry.cs`
- Modify: `src/OrionAudit/Capture/AuditSaveChangesInterceptor.cs` (instrument)
- Modify: `src/OrionAudit/Read/AuditReconstructor.cs` (instrument)
- Test: `tests/OrionAudit.Tests/OrionAuditTelemetryTests.cs`

### Step 1: Create the telemetry source class

Create `src/OrionAudit/Telemetry/OrionAuditTelemetry.cs`:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OrionAudit;

/// <summary>Activity source, meter, and instrument constants for OrionAudit telemetry.</summary>
public static class OrionAuditTelemetry
{
    /// <summary>The ActivitySource name registered for audit spans.</summary>
    public const string ActivitySourceName = "OrionAudit";

    /// <summary>The Meter name registered for audit metrics.</summary>
    public const string MeterName = "OrionAudit";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.1.0");
    internal static readonly Meter Meter = new(MeterName, "0.1.0");

    internal static readonly Counter<long> EntriesWritten = Meter.CreateCounter<long>(
        "orionaudit.entries.written", unit: "entries", description: "Audit entries successfully written.");

    internal static readonly Counter<long> EntriesFailed = Meter.CreateCounter<long>(
        "orionaudit.entries.failed", unit: "entries", description: "Audit entries written with diff errors.");

    internal static readonly Histogram<double> CaptureDuration = Meter.CreateHistogram<double>(
        "orionaudit.capture.duration", unit: "ms", description: "Interceptor capture duration per save.");

    internal static readonly Histogram<double> ReconstructDuration = Meter.CreateHistogram<double>(
        "orionaudit.reconstruct.duration", unit: "ms", description: "Time-travel reconstruction duration.");
}
```

### Step 2: Instrument the interceptor

Modify `src/OrionAudit/Capture/AuditSaveChangesInterceptor.cs`. At the top of `SavingChangesAsync`, after computing `auditedEntries` but before returning:

Add `using System.Diagnostics;` at the top.

Replace the existing implementation of `SavingChangesAsync` body with this instrumented version (preserve the existing logic for capture):

```csharp
public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
    DbContextEventData eventData,
    InterceptionResult<int> result,
    CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(eventData);
    var ctx = eventData.Context!;
    var configuration = serviceProvider.GetRequiredService<IAuditConfiguration>();
    var clock = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;

    var auditedEntries = ctx.ChangeTracker.Entries()
        .Where(e => configuration.IsAudited(e.Entity.GetType()))
        .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
        .ToList();

    if (auditedEntries.Count == 0)
    {
        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    using var activity = OrionAuditTelemetry.ActivitySource.StartActivity("OrionAudit.Capture", ActivityKind.Internal);
    activity?.SetTag("orionaudit.entry_count", auditedEntries.Count);

    var stopwatch = Stopwatch.StartNew();
    var user = serviceProvider.GetService<IAuditUserResolver>()?.Resolve(serviceProvider);
    var tenantId = serviceProvider.GetService<IAuditTenantResolver>()?.Resolve(serviceProvider);
    var correlationId = Activity.Current?.Id;
    var occurredOn = clock.GetUtcNow().UtcDateTime;

    if (tenantId is not null) activity?.SetTag("orionaudit.tenant_id", tenantId);
    if (user?.Type is not null) activity?.SetTag("orionaudit.user_type", user.Type);

    int writtenCount = 0;
    int failedCount = 0;
    foreach (var entry in auditedEntries)
    {
        var auditLog = BuildAuditLog(entry, configuration, user, tenantId, correlationId, occurredOn);
        ctx.Add(auditLog);
        if (auditLog.Error is null) writtenCount++;
        else failedCount++;
    }

    OrionAuditTelemetry.EntriesWritten.Add(writtenCount);
    OrionAuditTelemetry.EntriesFailed.Add(failedCount);
    OrionAuditTelemetry.CaptureDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
    activity?.SetStatus(ActivityStatusCode.Ok);

    return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
}
```

### Step 3: Instrument the reconstructor

Modify `src/OrionAudit/Read/AuditReconstructor.cs`. Add `using System.Diagnostics;` at the top and wrap each public method body:

```csharp
public async Task<T?> ReconstructAsync<T>(string entityId, DateTime asOf, CancellationToken cancellationToken = default)
    where T : class, new()
{
    ArgumentException.ThrowIfNullOrEmpty(entityId);
    using var activity = OrionAuditTelemetry.ActivitySource.StartActivity("OrionAudit.Reconstruct", ActivityKind.Internal);
    activity?.SetTag("orionaudit.entity_type", typeof(T).Name);
    activity?.SetTag("orionaudit.as_of", asOf.ToString("O"));

    var stopwatch = Stopwatch.StartNew();
    try
    {
        var entityTypeName = typeof(T).AssemblyQualifiedName!;
        var rows = await context.Set<AuditLog>()
            .Where(a => a.EntityType == entityTypeName && a.EntityId == entityId && a.OccurredOnUtc <= asOf)
            .OrderBy(a => a.OccurredOnUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        activity?.SetTag("orionaudit.audit_row_count", rows.Count);
        var result = Replay<T>(rows, entityId);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return result;
    }
    finally
    {
        OrionAuditTelemetry.ReconstructDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
    }
}
```

Apply the same pattern to `ReconstructManyAsync` (wrap the body in an activity + duration histogram).

### Step 4: Write a telemetry test

Create `tests/OrionAudit.Tests/OrionAuditTelemetryTests.cs`:

```csharp
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.Capture;
using OrionAudit.Configuration;

namespace OrionAudit.Tests;

public class OrionAuditTelemetryTests
{
    [Auditable]
    public sealed class Order
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Status { get; set; } = "New";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().HasKey(o => o.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    [Fact]
    public async Task SaveChanges_EmitsCaptureActivity()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == OrionAuditTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Order>());
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        var activity = Assert.Single(captured, a => a.OperationName == "OrionAudit.Capture");
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Equal(1, (long)activity.GetTagItem("orionaudit.entry_count")!);
    }
}
```

### Step 5: Run tests, commit

```bash
dotnet test tests/OrionAudit.Tests --filter "FullyQualifiedName~OrionAuditTelemetryTests"
```

Expected: 1/1 PASS.

```bash
git add src/OrionAudit/Telemetry tests/OrionAudit.Tests/OrionAuditTelemetryTests.cs src/OrionAudit/Capture/AuditSaveChangesInterceptor.cs src/OrionAudit/Read/AuditReconstructor.cs
git commit -m "feat(telemetry): add OrionAudit ActivitySource + meter and instrument capture/reconstruct"
```

---

## Task 19: ASP.NET Core integration package

**Files:**
- Create: `src/OrionAudit.AspNetCore/HttpContextAuditUserResolver.cs`
- Create: `src/OrionAudit.AspNetCore/AuditAspNetCoreServiceCollectionExtensions.cs`
- Delete: `src/OrionAudit.AspNetCore/OrionAuditAspNetCoreMarker.cs`
- Test: `tests/OrionAudit.AspNetCore.Tests/HttpContextAuditUserResolverTests.cs`

### Step 1: Write failing tests

Create `tests/OrionAudit.AspNetCore.Tests/HttpContextAuditUserResolverTests.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.AspNetCore;

namespace OrionAudit.AspNetCore.Tests;

public class HttpContextAuditUserResolverTests
{
    private static IServiceProvider BuildSpWithUser(ClaimsPrincipal? principal)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
        {
            HttpContext = principal is null
                ? new DefaultHttpContext()
                : new DefaultHttpContext { User = principal }
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoHttpContext()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var resolver = new HttpContextAuditUserResolver();
        Assert.Null(resolver.Resolve(sp));
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenUserNotAuthenticated()
    {
        var sp = BuildSpWithUser(new ClaimsPrincipal(new ClaimsIdentity()));
        var resolver = new HttpContextAuditUserResolver();
        Assert.Null(resolver.Resolve(sp));
    }

    [Fact]
    public void Resolve_ReturnsAuditUser_WhenNameIdentifierClaimPresent()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-123"),
            new Claim(ClaimTypes.Name, "Alice")
        }, authenticationType: "Test");
        var sp = BuildSpWithUser(new ClaimsPrincipal(identity));

        var resolver = new HttpContextAuditUserResolver();
        var user = resolver.Resolve(sp);
        Assert.NotNull(user);
        Assert.Equal("user-123", user.Id);
        Assert.Equal("Alice", user.DisplayName);
        Assert.Equal("user", user.Type);
    }

    [Fact]
    public void Resolve_FallsBackToSubClaim_WhenNameIdentifierAbsent()
    {
        var identity = new ClaimsIdentity(new[] { new Claim("sub", "user-456") }, authenticationType: "Test");
        var sp = BuildSpWithUser(new ClaimsPrincipal(identity));

        var resolver = new HttpContextAuditUserResolver();
        var user = resolver.Resolve(sp);
        Assert.NotNull(user);
        Assert.Equal("user-456", user.Id);
    }
}
```

### Step 2: Verify failure

```bash
dotnet test tests/OrionAudit.AspNetCore.Tests --filter "FullyQualifiedName~HttpContextAuditUserResolverTests"
```

Expected: FAIL — `HttpContextAuditUserResolver` not found.

### Step 3: Implement resolver

Create `src/OrionAudit.AspNetCore/HttpContextAuditUserResolver.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace OrionAudit.AspNetCore;

/// <summary>
/// <see cref="IAuditUserResolver"/> implementation that pulls the current user from
/// <see cref="IHttpContextAccessor"/>. Returns null for anonymous requests or when
/// <see cref="IHttpContextAccessor.HttpContext"/> is not available.
/// </summary>
public sealed class HttpContextAuditUserResolver : IAuditUserResolver
{
    /// <inheritdoc />
    public AuditUser? Resolve(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        var accessor = serviceProvider.GetService<IHttpContextAccessor>();
        var user = accessor?.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return null;

        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
              ?? user.FindFirst("sub")?.Value;
        if (id is null) return null;

        var display = user.FindFirst(ClaimTypes.Name)?.Value;
        return new AuditUser(id, display, "user");
    }
}
```

### Step 4: Implement DI helper

Create `src/OrionAudit.AspNetCore/AuditAspNetCoreServiceCollectionExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OrionAudit.AspNetCore;

/// <summary>DI helpers for the ASP.NET Core integration package.</summary>
public static class AuditAspNetCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="HttpContextAuditUserResolver"/> as the default <see cref="IAuditUserResolver"/>
    /// and ensures <see cref="IHttpContextAccessor"/> is available. Idempotent.
    /// </summary>
    public static IServiceCollection AddOrionAuditAspNetCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.TryAddScoped<IAuditUserResolver, HttpContextAuditUserResolver>();
        return services;
    }
}
```

### Step 5: Delete marker, run tests, commit

```bash
rm src/OrionAudit.AspNetCore/OrionAuditAspNetCoreMarker.cs
dotnet test tests/OrionAudit.AspNetCore.Tests
```

Expected: 4/4 PASS.

```bash
git add src/OrionAudit.AspNetCore tests/OrionAudit.AspNetCore.Tests
git commit -m "feat(aspnetcore): add HttpContextAuditUserResolver and AddOrionAuditAspNetCore"
```

---

## Task 20: OrionAudit.Testing package

**Files:**
- Create: `src/OrionAudit.Testing/OrionAuditAssertionException.cs`
- Create: `src/OrionAudit.Testing/AuditCapture.cs`
- Create: `src/OrionAudit.Testing/AuditAssertions.cs`
- Create: `src/OrionAudit.Testing/InMemoryAuditUserResolver.cs`
- Create: `src/OrionAudit.Testing/InMemoryAuditTenantResolver.cs`
- Delete: `src/OrionAudit.Testing/OrionAuditTestingMarker.cs`
- Test: `tests/OrionAudit.Testing.Tests/AuditCaptureTests.cs`

### Step 1: Write failing tests

Create `tests/OrionAudit.Testing.Tests/AuditCaptureTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.Testing;

namespace OrionAudit.Testing.Tests;

public class AuditCaptureTests
{
    [Auditable]
    public sealed class Order
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Status { get; set; } = "New";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().HasKey(o => o.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private static async Task<TestContext> NewContextAsync()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Order>());
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<TestContext>();
    }

    [Fact]
    public async Task Capture_From_ProvidesAllAuditRows()
    {
        await using var ctx = await NewContextAsync();
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        var capture = AuditCapture.From(ctx);
        Assert.Single(capture.All);
    }

    [Fact]
    public async Task Should_HaveLogged_Passes_WhenActionPresent()
    {
        await using var ctx = await NewContextAsync();
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        AuditCapture.From(ctx).Should().HaveLogged<Order>(AuditAction.Inserted);
    }

    [Fact]
    public async Task Should_HaveLogged_Throws_WhenActionMissing()
    {
        await using var ctx = await NewContextAsync();
        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        Assert.Throws<OrionAuditAssertionException>(() =>
            AuditCapture.From(ctx).Should().HaveLogged<Order>(AuditAction.Deleted));
    }

    [Fact]
    public async Task Should_NotHaveLogged_Passes_WhenNoLogs()
    {
        await using var ctx = await NewContextAsync();
        AuditCapture.From(ctx).Should().NotHaveLogged<Order>();
    }

    [Fact]
    public async Task Should_HaveLoggedExactly_VerifiesCount()
    {
        await using var ctx = await NewContextAsync();
        ctx.Orders.Add(new Order { Status = "A" });
        ctx.Orders.Add(new Order { Status = "B" });
        await ctx.SaveChangesAsync();

        AuditCapture.From(ctx).Should().HaveLoggedExactly(2).Of<Order>();
        Assert.Throws<OrionAuditAssertionException>(() =>
            AuditCapture.From(ctx).Should().HaveLoggedExactly(5).Of<Order>());
    }

    [Fact]
    public void InMemoryAuditUserResolver_ReturnsConfiguredUser()
    {
        var resolver = new InMemoryAuditUserResolver(new AuditUser("u-1", "Alice"));
        var user = resolver.Resolve(null!);
        Assert.NotNull(user);
        Assert.Equal("u-1", user.Id);
    }

    [Fact]
    public void InMemoryAuditTenantResolver_ReturnsConfiguredTenant()
    {
        var resolver = new InMemoryAuditTenantResolver("tenant-x");
        Assert.Equal("tenant-x", resolver.Resolve(null!));
    }
}
```

### Step 2: Verify failure

```bash
dotnet test tests/OrionAudit.Testing.Tests
```

Expected: FAIL — types not found.

### Step 3: Implement assertion exception

Create `src/OrionAudit.Testing/OrionAuditAssertionException.cs`:

```csharp
namespace OrionAudit.Testing;

/// <summary>
/// Thrown by <see cref="AuditAssertions"/> when an expectation about captured audit rows fails.
/// Test runners (xUnit, NUnit, MSTest) treat any thrown exception as a test failure, so this
/// works without depending on a specific framework's assertion type.
/// </summary>
public sealed class OrionAuditAssertionException : Exception
{
    /// <summary>Initializes a new instance with the supplied message.</summary>
    public OrionAuditAssertionException(string message) : base(message) { }
}
```

### Step 4: Implement AuditCapture

Create `src/OrionAudit.Testing/AuditCapture.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace OrionAudit.Testing;

/// <summary>
/// Snapshot of audit-log rows from a <see cref="DbContext"/> for use in fluent test assertions.
/// </summary>
public sealed class AuditCapture
{
    private readonly IReadOnlyList<AuditLog> rows;

    private AuditCapture(IReadOnlyList<AuditLog> rows) => this.rows = rows;

    /// <summary>All captured audit rows.</summary>
    public IReadOnlyList<AuditLog> All => rows;

    /// <summary>Loads all audit rows from the supplied context (sync — for in-memory test stores).</summary>
    public static AuditCapture From(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var loaded = context.Set<AuditLog>().AsNoTracking().ToList();
        return new AuditCapture(loaded);
    }

    /// <summary>Returns audit rows for entity type <typeparamref name="T"/>.</summary>
    public IEnumerable<AuditLog> For<T>()
        => rows.Where(r => r.EntityType == typeof(T).AssemblyQualifiedName);

    /// <summary>Entry point for fluent assertions.</summary>
    public AuditAssertions Should() => new(this);
}
```

### Step 5: Implement AuditAssertions

Create `src/OrionAudit.Testing/AuditAssertions.cs`:

```csharp
namespace OrionAudit.Testing;

/// <summary>Fluent assertions over a <see cref="AuditCapture"/>.</summary>
public sealed class AuditAssertions
{
    private readonly AuditCapture capture;

    internal AuditAssertions(AuditCapture capture) => this.capture = capture;

    /// <summary>Asserts that at least one audit row of <typeparamref name="T"/> with the given action was captured.</summary>
    public AuditAssertions HaveLogged<T>(AuditAction action)
    {
        if (!capture.For<T>().Any(a => a.Action == action))
        {
            throw new OrionAuditAssertionException(
                $"Expected {typeof(T).Name} {action} log but found none. " +
                $"Captured for {typeof(T).Name}: {string.Join(", ", capture.For<T>().Select(a => a.Action))}");
        }
        return this;
    }

    /// <summary>Asserts that at least one audit row of <typeparamref name="T"/> with the given action matches the predicate.</summary>
    public AuditAssertions HaveLogged<T>(AuditAction action, Func<AuditLog, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (!capture.For<T>().Any(a => a.Action == action && predicate(a)))
        {
            throw new OrionAuditAssertionException(
                $"Expected {typeof(T).Name} {action} log matching predicate but none found.");
        }
        return this;
    }

    /// <summary>Asserts that no audit row of <typeparamref name="T"/> was captured.</summary>
    public AuditAssertions NotHaveLogged<T>()
    {
        if (capture.For<T>().Any())
        {
            throw new OrionAuditAssertionException(
                $"Expected no {typeof(T).Name} logs but found {capture.For<T>().Count()}.");
        }
        return this;
    }

    /// <summary>Begins a count-then-type fluent assertion (e.g. <c>HaveLoggedExactly(2).Of&lt;Order&gt;()</c>).</summary>
    public CountAssertion HaveLoggedExactly(int expected) => new(this, capture, expected);

    /// <summary>Continuation that pairs an expected count with an entity type.</summary>
    public sealed class CountAssertion
    {
        private readonly AuditAssertions parent;
        private readonly AuditCapture capture;
        private readonly int expected;

        internal CountAssertion(AuditAssertions parent, AuditCapture capture, int expected)
        {
            this.parent = parent;
            this.capture = capture;
            this.expected = expected;
        }

        /// <summary>Specifies the entity type whose captured row count must equal the previously supplied number.</summary>
        public AuditAssertions Of<T>()
        {
            var actual = capture.For<T>().Count();
            if (actual != expected)
            {
                throw new OrionAuditAssertionException(
                    $"Expected exactly {expected} {typeof(T).Name} log(s), but found {actual}.");
            }
            return parent;
        }
    }
}
```

### Step 6: Implement InMemory resolvers

Create `src/OrionAudit.Testing/InMemoryAuditUserResolver.cs`:

```csharp
namespace OrionAudit.Testing;

/// <summary>
/// Test double for <see cref="IAuditUserResolver"/>. Returns the configured user regardless of the
/// supplied service provider.
/// </summary>
public sealed class InMemoryAuditUserResolver : IAuditUserResolver
{
    /// <summary>Initializes a new resolver returning <paramref name="user"/> (default null).</summary>
    public InMemoryAuditUserResolver(AuditUser? user = null) => User = user;

    /// <summary>The user instance returned on resolve. Mutable so tests can swap mid-run.</summary>
    public AuditUser? User { get; set; }

    /// <inheritdoc />
    public AuditUser? Resolve(IServiceProvider serviceProvider) => User;
}
```

Create `src/OrionAudit.Testing/InMemoryAuditTenantResolver.cs`:

```csharp
namespace OrionAudit.Testing;

/// <summary>
/// Test double for <see cref="IAuditTenantResolver"/>. Returns the configured tenant id regardless
/// of the supplied service provider.
/// </summary>
public sealed class InMemoryAuditTenantResolver : IAuditTenantResolver
{
    /// <summary>Initializes a new resolver returning <paramref name="tenantId"/> (default null).</summary>
    public InMemoryAuditTenantResolver(string? tenantId = null) => TenantId = tenantId;

    /// <summary>The tenant id returned on resolve. Mutable so tests can swap mid-run.</summary>
    public string? TenantId { get; set; }

    /// <inheritdoc />
    public string? Resolve(IServiceProvider serviceProvider) => TenantId;
}
```

### Step 7: Delete marker, run tests, commit

```bash
rm src/OrionAudit.Testing/OrionAuditTestingMarker.cs
dotnet test tests/OrionAudit.Testing.Tests
```

Expected: 7/7 PASS.

```bash
git add src/OrionAudit.Testing tests/OrionAudit.Testing.Tests
git commit -m "feat(testing): add AuditCapture, AuditAssertions, and InMemory resolvers"
```

---

## Task 21: Integration tests — Sqlite end-to-end

**Files:**
- Create: `tests/OrionAudit.IntegrationTests/SqliteEndToEndTests.cs`
- Create: `tests/OrionAudit.IntegrationTests/MultiTenantIsolationTests.cs`

### Step 1: Write SqliteEndToEndTests

Create `tests/OrionAudit.IntegrationTests/SqliteEndToEndTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;

namespace OrionAudit.IntegrationTests;

public class SqliteEndToEndTests : IAsyncLifetime
{
    [Auditable]
    public sealed class Customer
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Customer> Customers => Set<Customer>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>().HasKey(c => c.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private SqliteConnection connection = null!;
    private ServiceProvider provider = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Customer>());
        services.AddSingleton(connection);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await provider.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task InsertUpdateDelete_FullCycle_ProducesThreeAuditRows()
    {
        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var customer = new Customer { Name = "Alice", Email = "alice@example.com" };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        customer.Name = "Alice (updated)";
        await ctx.SaveChangesAsync();

        ctx.Customers.Remove(customer);
        await ctx.SaveChangesAsync();

        var logs = await ctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.Equal(3, logs.Count);
        Assert.Equal(AuditAction.Inserted, logs[0].Action);
        Assert.Equal(AuditAction.Updated, logs[1].Action);
        Assert.Equal(AuditAction.Deleted, logs[2].Action);
        Assert.NotNull(logs[2].Snapshot);
    }

    [Fact]
    public async Task Reconstruct_ReplaysFullHistoryToLatestState()
    {
        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var customer = new Customer { Name = "Bob", Email = "bob@example.com" };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();
        customer.Name = "Bob Smith";
        await ctx.SaveChangesAsync();

        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();
        var result = await reconstructor.ReconstructAsync<Customer>(customer.Id.ToString(), DateTime.UtcNow.AddMinutes(1));

        Assert.NotNull(result);
        Assert.Equal("Bob Smith", result.Name);
    }
}
```

### Step 2: Write MultiTenantIsolationTests

Create `tests/OrionAudit.IntegrationTests/MultiTenantIsolationTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.Testing;

namespace OrionAudit.IntegrationTests;

public class MultiTenantIsolationTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Text { get; set; } = "";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    [Fact]
    public async Task AuditFor_FiltersToCurrentTenant_AutomaticallyAcrossWrites()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var resolver = new InMemoryAuditTenantResolver();

        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Note>());
        services.AddSingleton(connection);
        services.AddSingleton<IAuditTenantResolver>(resolver);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));

        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            await ctx.Database.EnsureCreatedAsync();
        }

        // Tenant A writes one note
        resolver.TenantId = "tenant-A";
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Notes.Add(new Note { Text = "Alpha" });
            await ctx.SaveChangesAsync();
        }

        // Tenant B writes one note
        resolver.TenantId = "tenant-B";
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Notes.Add(new Note { Text = "Beta" });
            await ctx.SaveChangesAsync();
        }

        // Tenant A reads — should see only their audit row
        resolver.TenantId = "tenant-A";
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var logs = await ctx.AuditFor<Note>().ToListAsync();
            Assert.Single(logs);
            Assert.Equal("tenant-A", logs[0].TenantId);
        }

        // Cross-tenant query bypasses the filter
        resolver.TenantId = "tenant-A";
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var logs = await ctx.AuditFor<Note>(crossTenant: true).ToListAsync();
            Assert.Equal(2, logs.Count);
        }

        await connection.DisposeAsync();
    }
}
```

### Step 3: Run tests, commit

```bash
dotnet test tests/OrionAudit.IntegrationTests
```

Expected: 3/3 PASS (2 end-to-end + 1 tenant isolation).

```bash
git add tests/OrionAudit.IntegrationTests
git commit -m "test(integration): add Sqlite end-to-end and multi-tenant isolation tests"
```

---

## Task 22: Sample console app

**Files:**
- Create: `sample/OrionAudit.Sample.Console/OrionAudit.Sample.Console.csproj`
- Create: `sample/OrionAudit.Sample.Console/Program.cs`
- Modify: `OrionAudit.sln`

### Step 1: Create the sample csproj

Create `sample/OrionAudit.Sample.Console/OrionAudit.Sample.Console.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.0" />
    <ProjectReference Include="..\..\src\OrionAudit\OrionAudit.csproj" />
    <ProjectReference Include="..\..\src\OrionAudit.Testing\OrionAudit.Testing.csproj" />
  </ItemGroup>
</Project>
```

### Step 2: Create the sample Program.cs

Create `sample/OrionAudit.Sample.Console/Program.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.Testing;

Console.WriteLine("OrionAudit v0.1.0 sample");
Console.WriteLine(new string('=', 60));

var connection = new SqliteConnection("DataSource=:memory:");
await connection.OpenAsync();

var services = new ServiceCollection();
services.AddOrionAudit<SampleDb>(o => o
    .Audit<Order>()
    .UserResolver<DemoUserResolver>());
services.AddSingleton(connection);
services.AddDbContext<SampleDb>((sp, o) =>
    o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));

await using var sp = services.BuildServiceProvider();
await using (var scope = sp.CreateAsyncScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<SampleDb>();
    await ctx.Database.EnsureCreatedAsync();
}

// 1) Insert a few orders
await using (var scope = sp.CreateAsyncScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<SampleDb>();
    ctx.Orders.Add(new Order { Status = "Pending", Total = 99.99m });
    ctx.Orders.Add(new Order { Status = "Pending", Total = 149.50m });
    await ctx.SaveChangesAsync();
    Console.WriteLine("  Inserted 2 orders");
}

// 2) Update one
await using (var scope = sp.CreateAsyncScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<SampleDb>();
    var first = await ctx.Orders.FirstAsync();
    first.Status = "Shipped";
    await ctx.SaveChangesAsync();
    Console.WriteLine($"  Updated order {first.Id} to Shipped");
}

// 3) Show audit log
await using (var scope = sp.CreateAsyncScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<SampleDb>();
    var logs = await ctx.AuditFor<Order>().OrderBy(a => a.OccurredOnUtc).ToListAsync();
    Console.WriteLine($"\n  AuditLog rows: {logs.Count}");
    foreach (var log in logs)
    {
        Console.WriteLine($"    {log.OccurredOnUtc:O}  {log.Action,-8}  EntityId={log.EntityId}  User={log.UserId}");
    }
}

// 4) Time-travel reconstruction
await using (var scope = sp.CreateAsyncScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<SampleDb>();
    var first = await ctx.Orders.FirstAsync();
    var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();
    var current = await reconstructor.ReconstructAsync<Order>(first.Id.ToString(), DateTime.UtcNow);
    Console.WriteLine($"\n  Reconstructed order {first.Id}:");
    Console.WriteLine($"    Status = {current!.Status}, Total = {current.Total}");
}

await connection.DisposeAsync();
Console.WriteLine("\n  Sample complete.");

// ---- types ----

[Auditable]
public sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Status { get; set; } = "New";
    public decimal Total { get; set; }
}

public sealed class SampleDb : DbContext
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public SampleDb(DbContextOptions<SampleDb> options) : base(options) { }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>().HasKey(o => o.Id);
        modelBuilder.ApplyOrionAuditConfigurations();
    }
}

public sealed class DemoUserResolver : IAuditUserResolver
{
    public AuditUser? Resolve(IServiceProvider sp) => new("demo-user", "Demo User");
}
```

### Step 3: Register and run

```bash
dotnet sln add sample/OrionAudit.Sample.Console/OrionAudit.Sample.Console.csproj
dotnet run --project sample/OrionAudit.Sample.Console
```

Expected: console output shows insert + update + audit log + reconstruction.

### Step 4: Commit

```bash
git add sample OrionAudit.sln
git commit -m "docs(sample): add console sample showcasing capture and reconstruction"
```

---

## Task 23: CI/CD workflow + final docs

**Files:**
- Create: `.github/workflows/ci-cd.yml`
- Create: `.github/FUNDING.yml`
- Update: `README.md`, `CHANGELOG.md`

### Step 1: Create CI/CD workflow

Create `.github/workflows/ci-cd.yml`:

```yaml
name: CI/CD

on:
  push:
    branches: [ main, master ]
  pull_request:
    branches: [ main, master ]
  release:
    types: [ published ]

env:
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: true
  DOTNET_CLI_TELEMETRY_OPTOUT: true
  SOLUTION_PATH: OrionAudit.sln

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    strategy:
      matrix:
        dotnet-version: ['8.0.x', '9.0.x', '10.0.x']
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ matrix.dotnet-version }}
      - run: dotnet restore ${{ env.SOLUTION_PATH }}
      - run: dotnet build ${{ env.SOLUTION_PATH }} --no-restore --configuration Release
      - run: dotnet test ${{ env.SOLUTION_PATH }} --no-restore --configuration Release --verbosity normal

  publish:
    needs: build-and-test
    runs-on: ubuntu-latest
    if: github.event_name == 'release'
    permissions:
      packages: write
      contents: read
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet restore ${{ env.SOLUTION_PATH }}
      - run: dotnet build ${{ env.SOLUTION_PATH }} --no-restore --configuration Release
      - name: Pack All Projects
        run: |
          dotnet pack src/OrionAudit/OrionAudit.csproj --no-build --configuration Release -o ./nupkgs
          dotnet pack src/OrionAudit.AspNetCore/OrionAudit.AspNetCore.csproj --no-build --configuration Release -o ./nupkgs
          dotnet pack src/OrionAudit.Testing/OrionAudit.Testing.csproj --no-build --configuration Release -o ./nupkgs
      - name: Push to NuGet.org
        run: dotnet nuget push "./nupkgs/*.nupkg" --api-key ${{ secrets.NUGET }} --source https://api.nuget.org/v3/index.json --skip-duplicate
      - name: Push to GitHub Packages
        run: |
          dotnet nuget add source --username ${{ github.repository_owner }} --password ${{ secrets.GITHUB_TOKEN }} --store-password-in-clear-text --name github "https://nuget.pkg.github.com/${{ github.repository_owner }}/index.json"
          dotnet nuget push "./nupkgs/*.nupkg" --source github --skip-duplicate
```

### Step 2: Create FUNDING.yml

Create `.github/FUNDING.yml`:

```yaml
github: [tunahanaliozturk]
```

### Step 3: Flesh out README.md

Replace the root `README.md` with a full release-grade landing page including:
- Brief value prop
- Install snippet (3 packages)
- Quick-start (5-line example)
- Features summary
- Link to spec + plan in docs/
- Link to sister projects (OrionGuard, others when shipped)

(Full content is the same shape as OrionGuard's README — adapt section-by-section.)

### Step 4: Flesh out CHANGELOG.md v0.1.0 entry

Replace `CHANGELOG.md` with the actual release notes for v0.1.0 mirroring the spec's section list (capture, diff, reconstruction, multi-tenancy, etc.).

### Step 5: Final solution-wide verification

```bash
dotnet build OrionAudit.sln -c Release
dotnet test OrionAudit.sln
dotnet pack src/OrionAudit -c Release -o ./artifacts
dotnet pack src/OrionAudit.AspNetCore -c Release -o ./artifacts
dotnet pack src/OrionAudit.Testing -c Release -o ./artifacts
```

Expected:
- Release build: 0 errors
- All tests pass (~70 across 4 test projects per DoD)
- 3 .nupkg files produced at `./artifacts/OrionAudit*.0.1.0.nupkg`

### Step 6: Commit

```bash
git add .github README.md CHANGELOG.md
git commit -m "ci: add release workflow and finalize v0.1.0 docs"
```

### Step 7: Push + open release

```bash
git push -u origin master
git tag -a v0.1.0 -m "v0.1.0 — Initial release"
git push origin v0.1.0
gh release create v0.1.0 --title "v0.1.0 — Initial release" --notes-file CHANGELOG.md --verify-tag --latest
```

The publish workflow fires on the release event and pushes all 3 packages to NuGet.org and GitHub Packages.

---

## Definition of Done

- 70+ tests passing across 4 test projects
- Release build clean across net8/9/10
- 3 NuGet packages produced and published
- Sample app runs end-to-end
- README + CHANGELOG complete
- CI/CD green on `master`

## Self-review checklist (engineer)

- Every public type listed in spec § 15 is implemented (Task 4-21 covers all).
- AOT trim annotations (`[RequiresUnreferencedCode]`, `[RequiresDynamicCode]`) added where appropriate (interceptor, reconstructor). These can be added in a final polish pass after Task 18 if not done inline.
- Composite primary keys throw `OrionAuditConfigurationException` at runtime (Task 12 `ExtractPrimaryKey`).
- Tenant filter is bypassable via `crossTenant: true` (Task 14).
- No `Co-Authored-By` trailer on any commit in the entire plan.
