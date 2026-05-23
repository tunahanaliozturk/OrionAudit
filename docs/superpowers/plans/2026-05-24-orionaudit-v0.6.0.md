# OrionAudit v0.6.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship OrionAudit v0.6.0 — extensible `AuditLog` row (`AddColumn<T>` adds real, tipped, indexable EF shadow-property columns), plus `AuditImportBuilder` for bulk legacy-history import with idempotent, byte-for-byte-compatible diffs.

**Architecture:** Both features land in core `OrionAudit` (no new packages). Custom columns are registered via `OrionAuditOptions.AddColumn<T>`, flow through `AuditConfiguration`, get mapped as shadow properties on `AuditLog`, and — in async mode — round-trip through a new `OrionAudit_Capture_Queue.CustomColumnsJson` column. `AuditImportBuilder` writes `AuditLog` rows directly (bypassing the capture queue) in `BatchSize`-sized transactions, reusing `SnapshotBuilder` + `Json6902` for byte-equal diffs, and uses an `ImportBatch`-tag stamped into `CorrelationId` for idempotency without a schema change.

**Tech Stack:** C# / .NET 8-9-10, EF Core 9, `System.Text.Json.Nodes`, xUnit v3, ASP.NET Core minimal APIs (for Viewer surface).

**Reference spec:** `docs/superpowers/specs/2026-05-24-orionaudit-v0.6.0-design.md`

**Environment notes (from v0.5.0 lessons — do not rediscover):**

- `dotnet test` is broken in this environment. To run tests: `dotnet build OrionAudit.sln -c Debug`, then run the xUnit v3 test executable directly — `./tests/<Project>/bin/Debug/net10.0/<Project>.exe` with `-class <FullyQualifiedClassName>` to filter.
- Commit messages: **no `Co-Authored-By` trailer** (project convention ECOSYSTEM §7).
- `AD0001` (Microsoft.AspNetCore.Analyzers.RouteHandlers NRE) is suppressed in `Moongazing.OrionAudit.Viewer.csproj`'s `NoWarn` — leave that suppression in place; no Viewer changes in this release re-trigger the analyzer.
- Minimal-API service parameters in the Viewer use `[FromServices]` explicitly.

---

## Phase 1 — `AddColumn` configuration and sync-path mapping

### Task 1: `CustomColumn` and `AuditColumnContext`

**Files:**
- Create: `src/Moongazing.OrionAudit/Configuration/CustomColumn.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/CustomColumnTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Moongazing.OrionAudit.Tests/CustomColumnTests.cs`:

```csharp
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class CustomColumnTests
{
    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(double))]
    [InlineData(typeof(float))]
    [InlineData(typeof(short))]
    [InlineData(typeof(byte))]
    [InlineData(typeof(int?))]
    [InlineData(typeof(Guid?))]
    [InlineData(typeof(AuditAction))]   // enum
    [InlineData(typeof(AuditAction?))]  // nullable enum
    public void IsSupportedColumnType_Accepts_Scalars(Type t)
        => Assert.True(CustomColumn.IsSupportedColumnType(t));

    [Theory]
    [InlineData(typeof(object))]
    [InlineData(typeof(int[]))]
    [InlineData(typeof(List<string>))]
    [InlineData(typeof(CustomColumnTests))]
    public void IsSupportedColumnType_Rejects_NonScalars(Type t)
        => Assert.False(CustomColumn.IsSupportedColumnType(t));

    [Fact]
    public void Construct_HoldsName_ClrType_AndProvider()
    {
        var col = new CustomColumn("X", typeof(int), _ => 42);
        Assert.Equal("X", col.Name);
        Assert.Equal(typeof(int), col.ClrType);
        var ctx = new AuditColumnContext(new object(), null!, AuditAction.Inserted, null, null);
        Assert.Equal(42, col.Provider(ctx));
    }
}
```

- [ ] **Step 2: Build to verify the test fails to compile**

Run: `dotnet build tests/Moongazing.OrionAudit.Tests/Moongazing.OrionAudit.Tests.csproj -c Debug 2>&1 | tail -10`
Expected: FAIL — `CustomColumn` / `AuditColumnContext` not defined.

- [ ] **Step 3: Create `CustomColumn.cs`**

```csharp
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// Information the value provider for a custom audit column receives at capture time.
/// Mirrors the data already in scope inside <c>AuditSaveChangesInterceptor</c>.
/// </summary>
public sealed record AuditColumnContext(
    object Entity,
    EntityEntry Entry,
    AuditAction Action,
    AuditUser? User,
    string? TenantId);

/// <summary>
/// A consumer-registered custom column on <see cref="AuditLog"/>. Created by
/// <c>OrionAuditOptions.AddColumn&lt;T&gt;</c> and consumed by
/// <c>AuditLogEntityTypeConfiguration</c> (shadow-property mapping), the interceptor
/// (value capture), and the dispatcher (async-mode value application).
/// </summary>
public sealed record CustomColumn(
    string Name,
    Type ClrType,
    Func<AuditColumnContext, object?> Provider)
{
    /// <summary>
    /// EF-mappable scalar types supported by <c>AddColumn&lt;T&gt;</c>.
    /// </summary>
    public static bool IsSupportedColumnType(Type t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var underlying = Nullable.GetUnderlyingType(t) ?? t;
        if (underlying.IsEnum)
        {
            return true;
        }
        return underlying == typeof(string)
            || underlying == typeof(Guid)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(bool)
            || underlying == typeof(int)
            || underlying == typeof(long)
            || underlying == typeof(short)
            || underlying == typeof(byte)
            || underlying == typeof(decimal)
            || underlying == typeof(double)
            || underlying == typeof(float);
    }
}
```

- [ ] **Step 4: Build + run the test**

```
dotnet build OrionAudit.sln -c Debug 2>&1 | tail -6
./tests/Moongazing.OrionAudit.Tests/bin/Debug/net10.0/Moongazing.OrionAudit.Tests.exe -class Moongazing.OrionAudit.Tests.CustomColumnTests 2>&1 | tail -5
```
Expected: build clean; `Total: 22, Failed: 0` (16 + 4 + 1 + an extra inline expected — adjust count to actual; the principle is all pass).

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionAudit/Configuration/CustomColumn.cs tests/Moongazing.OrionAudit.Tests/CustomColumnTests.cs
git commit -m "feat(addcolumn): add CustomColumn and AuditColumnContext records"
```

---

### Task 2: `OrionAuditOptions.AddColumn<T>` registration

**Files:**
- Modify: `src/Moongazing.OrionAudit/DependencyInjection/OrionAuditOptions.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/AddColumnRegistrationTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Moongazing.OrionAudit.Tests/AddColumnRegistrationTests.cs`:

```csharp
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AddColumnRegistrationTests
{
    [Fact]
    public void AddColumn_RegistersWithNameClrTypeAndProvider()
    {
        var o = new OrionAuditOptions();
        o.AddColumn<int>("WorkflowStepId", _ => 7);
        var registered = Assert.Single(o.CustomColumns);
        Assert.Equal("WorkflowStepId", registered.Name);
        Assert.Equal(typeof(int), registered.ClrType);
    }

    [Fact]
    public void AddColumn_DuplicateName_Throws()
    {
        var o = new OrionAuditOptions();
        o.AddColumn<int>("X", _ => 1);
        Assert.Throws<OrionAuditConfigurationException>(() => o.AddColumn<string>("X", _ => "y"));
    }

    [Fact]
    public void AddColumn_UnsupportedType_Throws()
    {
        var o = new OrionAuditOptions();
        Assert.Throws<OrionAuditConfigurationException>(() => o.AddColumn<List<int>>("X", _ => null));
    }

    [Fact]
    public void AddColumn_NullOrEmptyName_Throws()
    {
        var o = new OrionAuditOptions();
        Assert.Throws<ArgumentException>(() => o.AddColumn<int>("", _ => 1));
        Assert.Throws<ArgumentException>(() => o.AddColumn<int>("   ", _ => 1));
        Assert.Throws<ArgumentNullException>(() => o.AddColumn<int>(null!, _ => 1));
    }

    [Fact]
    public void AddColumn_NullProvider_Throws()
    {
        var o = new OrionAuditOptions();
        Assert.Throws<ArgumentNullException>(() => o.AddColumn<int>("X", null!));
    }

    [Fact]
    public void AddColumn_ProviderBox_MatchesGenericReturn()
    {
        var o = new OrionAuditOptions();
        o.AddColumn<int>("X", _ => 42);
        var ctx = new AuditColumnContext(new object(), null!, AuditAction.Inserted, null, null);
        Assert.Equal(42, Assert.Single(o.CustomColumns).Provider(ctx));
    }
}
```

- [ ] **Step 2: Build to verify failure**

Run: `dotnet build tests/Moongazing.OrionAudit.Tests/Moongazing.OrionAudit.Tests.csproj -c Debug 2>&1 | tail -6`
Expected: FAIL — `AddColumn` / `CustomColumns` not defined on `OrionAuditOptions`.

- [ ] **Step 3: Extend `OrionAuditOptions.cs`**

In `src/Moongazing.OrionAudit/DependencyInjection/OrionAuditOptions.cs`, add a backing list and the public collection property near the other `internal` collections (after `ScanAssemblies`):

```csharp
    private readonly List<CustomColumn> customColumns = new();

    /// <summary>
    /// The custom columns registered on <see cref="AuditLog"/> via <see cref="AddColumn{T}"/>.
    /// Read by <c>AuditLogEntityTypeConfiguration</c> (shadow-property mapping), the
    /// interceptor (value capture), and the dispatcher (async-mode value application).
    /// </summary>
    public IReadOnlyList<CustomColumn> CustomColumns => customColumns;
```

Add the `AddColumn<T>` method after `UseAsyncCapture`:

```csharp
    /// <summary>
    /// Registers a custom column on <see cref="AuditLog"/>. The column is materialised as a
    /// real, nullable, tipped EF shadow property (queryable via <c>EF.Property&lt;T&gt;</c>).
    /// The provider runs inside the capture transaction with the audited entity in scope.
    /// In async-capture mode the value rides through <c>OrionAudit_Capture_Queue.CustomColumnsJson</c>
    /// and is applied by the dispatcher.
    /// </summary>
    public OrionAuditOptions AddColumn<T>(string name, Func<AuditColumnContext, object?> provider)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(provider);

        if (!CustomColumn.IsSupportedColumnType(typeof(T)))
        {
            throw new OrionAuditConfigurationException(
                $"AddColumn '{name}': type '{typeof(T)}' is not an EF-mappable scalar.");
        }
        if (customColumns.Any(c => string.Equals(c.Name, name, StringComparison.Ordinal)))
        {
            throw new OrionAuditConfigurationException(
                $"AddColumn '{name}': a column with this name is already registered.");
        }

        customColumns.Add(new CustomColumn(name, typeof(T), provider));
        return this;
    }
```

- [ ] **Step 4: Build + run**

```
dotnet build OrionAudit.sln -c Debug 2>&1 | tail -6
./tests/Moongazing.OrionAudit.Tests/bin/Debug/net10.0/Moongazing.OrionAudit.Tests.exe -class Moongazing.OrionAudit.Tests.AddColumnRegistrationTests 2>&1 | tail -5
```
Expected: 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionAudit/DependencyInjection/OrionAuditOptions.cs tests/Moongazing.OrionAudit.Tests/AddColumnRegistrationTests.cs
git commit -m "feat(addcolumn): add OrionAuditOptions.AddColumn<T> registration"
```

---

### Task 3: `IAuditConfiguration.CustomColumns`

**Files:**
- Modify: `src/Moongazing.OrionAudit/Configuration/IAuditConfiguration.cs`
- Modify: `src/Moongazing.OrionAudit/Configuration/AuditConfiguration.cs`
- Modify: `src/Moongazing.OrionAudit/Configuration/AuditConfigurationBuilder.cs`
- Modify: `src/Moongazing.OrionAudit/DependencyInjection/AuditServiceCollectionExtensions.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/AuditConfigurationCustomColumnsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AuditConfigurationCustomColumnsTests
{
    private sealed class TestDb : DbContext
    {
        public TestDb(DbContextOptions<TestDb> options) : base(options) { }
    }

    [Fact]
    public void Configuration_Exposes_RegisteredCustomColumns()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<TestDb>(o => o
            .AddColumn<int>("WorkflowStepId", _ => 1)
            .AddColumn<string>("Source", _ => "x"));
        using var sp = services.BuildServiceProvider();

        var config = sp.GetRequiredService<IAuditConfiguration>();
        Assert.Equal(2, config.CustomColumns.Count);
        Assert.Contains(config.CustomColumns, c => c.Name == "WorkflowStepId" && c.ClrType == typeof(int));
        Assert.Contains(config.CustomColumns, c => c.Name == "Source" && c.ClrType == typeof(string));
    }

    [Fact]
    public void Configuration_NoCustomColumns_IsEmpty()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<TestDb>(o => { });
        using var sp = services.BuildServiceProvider();

        Assert.Empty(sp.GetRequiredService<IAuditConfiguration>().CustomColumns);
    }
}
```

- [ ] **Step 2: Build to verify failure**

Expected: `CustomColumns` not defined on `IAuditConfiguration`.

- [ ] **Step 3: Extend `IAuditConfiguration`**

In `IAuditConfiguration.cs`, after `AuditedTypeNames`:

```csharp
    /// <summary>Custom columns registered via <c>OrionAuditOptions.AddColumn</c>.</summary>
    IReadOnlyList<CustomColumn> CustomColumns { get; }
```

- [ ] **Step 4: Extend `AuditConfiguration`**

In `AuditConfiguration.cs`, add the field, constructor arg, and property. Update the constructor signature:

```csharp
    private readonly IReadOnlyList<CustomColumn> customColumns;

    /// <summary>Initializes a new configuration. Intended to be called only by <see cref="AuditConfigurationBuilder"/>.</summary>
    public AuditConfiguration(
        IDictionary<Type, AuditableTypeConfig> byType,
        IReadOnlyList<CustomColumn>? customColumns = null)
    {
        ArgumentNullException.ThrowIfNull(byType);
        this.byType = byType.ToFrozenDictionary();
        auditedTypeNames = this.byType.Keys.Select(t => t.AssemblyQualifiedName!).ToArray();
        this.customColumns = customColumns ?? Array.Empty<CustomColumn>();
    }

    /// <inheritdoc />
    public IReadOnlyList<CustomColumn> CustomColumns => customColumns;
```

- [ ] **Step 5: Extend `AuditConfigurationBuilder.Build`**

Open `src/Moongazing.OrionAudit/Configuration/AuditConfigurationBuilder.cs`. Add a custom-columns list and a `RegisterCustomColumns` setter (called by `AddOrionAudit`'s wiring), and pass them to the `AuditConfiguration` constructor in `Build()`. The existing `Build()` will look like `new AuditConfiguration(byType)`; change it to `new AuditConfiguration(byType, customColumns)`.

Add to `AuditConfigurationBuilder`:

```csharp
    private IReadOnlyList<CustomColumn> customColumns = Array.Empty<CustomColumn>();

    /// <summary>Set by <c>AddOrionAudit</c> from <c>OrionAuditOptions.CustomColumns</c>.</summary>
    internal void RegisterCustomColumns(IReadOnlyList<CustomColumn> columns)
        => customColumns = columns ?? Array.Empty<CustomColumn>();
```

And modify `Build()` to pass `customColumns` through.

- [ ] **Step 6: Wire it in `AddOrionAudit`**

In `src/Moongazing.OrionAudit/DependencyInjection/AuditServiceCollectionExtensions.cs`, right before `var configuration = options.ConfigurationBuilder.Build();`, add:

```csharp
        options.ConfigurationBuilder.RegisterCustomColumns(options.CustomColumns);
```

- [ ] **Step 7: Build + run**

```
dotnet build OrionAudit.sln -c Debug 2>&1 | tail -6
./tests/Moongazing.OrionAudit.Tests/bin/Debug/net10.0/Moongazing.OrionAudit.Tests.exe -class Moongazing.OrionAudit.Tests.AuditConfigurationCustomColumnsTests 2>&1 | tail -5
```
Expected: 2 tests pass; full Tests suite stays green.

- [ ] **Step 8: Commit**

```bash
git add src/Moongazing.OrionAudit/Configuration/IAuditConfiguration.cs src/Moongazing.OrionAudit/Configuration/AuditConfiguration.cs src/Moongazing.OrionAudit/Configuration/AuditConfigurationBuilder.cs src/Moongazing.OrionAudit/DependencyInjection/AuditServiceCollectionExtensions.cs tests/Moongazing.OrionAudit.Tests/AuditConfigurationCustomColumnsTests.cs
git commit -m "feat(addcolumn): expose CustomColumns through IAuditConfiguration"
```

---

### Task 4: `AuditLog` shadow-property mapping

**Files:**
- Modify: `src/Moongazing.OrionAudit/Core/AuditLogEntityTypeConfiguration.cs`
- Modify: `src/Moongazing.OrionAudit/DependencyInjection/AuditModelBuilderExtensions.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/AuditLogCustomColumnMappingTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AuditLogCustomColumnMappingTests
{
    private sealed class MappingDb : DbContext
    {
        public MappingDb(DbContextOptions<MappingDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyOrionAuditConfigurations(customColumns: new[]
            {
                new CustomColumn("WorkflowStepId", typeof(int), _ => 0),
                new CustomColumn("Source", typeof(string), _ => null),
                new CustomColumn("RequestId", typeof(Guid?), _ => null),
            });
    }

    [Fact]
    public void CustomColumns_Are_NullableShadowProperties_With_RightClrType()
    {
        var opts = new DbContextOptionsBuilder<MappingDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new MappingDb(opts);
        var et = db.Model.FindEntityType(typeof(AuditLog))!;

        var step = et.FindProperty("WorkflowStepId")!;
        Assert.Equal(typeof(int?), step.ClrType);  // shadow nullable
        Assert.True(step.IsNullable);

        var source = et.FindProperty("Source")!;
        Assert.Equal(typeof(string), source.ClrType);
        Assert.True(source.IsNullable);
        Assert.Equal(512, source.GetMaxLength());

        var req = et.FindProperty("RequestId")!;
        Assert.Equal(typeof(Guid?), req.ClrType);
        Assert.True(req.IsNullable);
    }
}
```

- [ ] **Step 2: Build to verify failure**

Expected: `ApplyOrionAuditConfigurations` has no `customColumns` parameter.

- [ ] **Step 3: Add the `customColumns` parameter to `ApplyOrionAuditConfigurations`**

In `AuditModelBuilderExtensions.cs`, append `IReadOnlyList<CustomColumn>? customColumns = null` to the parameter list and pass it through to `AuditLogEntityTypeConfiguration`. Update the constructor calls so the configuration receives the column list. Full method:

```csharp
    public static ModelBuilder ApplyOrionAuditConfigurations(
        this ModelBuilder modelBuilder,
        string? auditLogTableName = null,
        string? snapshotCursorTableName = null,
        OrionAuditColumnHints columnHints = OrionAuditColumnHints.Auto,
        string? captureQueueTableName = null,
        IReadOnlyList<CustomColumn>? customColumns = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var auditLog = new AuditLogEntityTypeConfiguration(
            auditLogTableName ?? AuditLogEntityTypeConfiguration.DefaultTableName,
            columnHints,
            customColumns ?? Array.Empty<CustomColumn>());
        modelBuilder.ApplyConfiguration(auditLog);

        var cursor = snapshotCursorTableName is null
            ? new SnapshotCursorEntityTypeConfiguration()
            : new SnapshotCursorEntityTypeConfiguration(snapshotCursorTableName);
        modelBuilder.ApplyConfiguration(cursor);

        var queue = captureQueueTableName is null
            ? new AuditCaptureQueueEntityTypeConfiguration()
            : new AuditCaptureQueueEntityTypeConfiguration(captureQueueTableName);
        modelBuilder.ApplyConfiguration(queue);

        return modelBuilder;
    }
```

Add `using Moongazing.OrionAudit.Configuration;` at the top if not already present.

- [ ] **Step 4: Map custom columns in `AuditLogEntityTypeConfiguration`**

Replace the constructor and `Configure` method to accept and use the column list:

```csharp
    private readonly string tableName;
    private readonly OrionAuditColumnHints columnHints;
    private readonly IReadOnlyList<CustomColumn> customColumns;

    public AuditLogEntityTypeConfiguration()
        : this(DefaultTableName, OrionAuditColumnHints.Auto, Array.Empty<CustomColumn>()) { }

    public AuditLogEntityTypeConfiguration(string tableName)
        : this(tableName, OrionAuditColumnHints.Auto, Array.Empty<CustomColumn>()) { }

    public AuditLogEntityTypeConfiguration(string tableName, OrionAuditColumnHints columnHints)
        : this(tableName, columnHints, Array.Empty<CustomColumn>()) { }

    public AuditLogEntityTypeConfiguration(
        string tableName,
        OrionAuditColumnHints columnHints,
        IReadOnlyList<CustomColumn> customColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(customColumns);
        this.tableName = tableName;
        this.columnHints = columnHints;
        this.customColumns = customColumns;
    }
```

At the end of the existing `Configure` body (after the three `HasIndex` calls), add:

```csharp
        foreach (var column in customColumns)
        {
            var prop = builder.Property(column.ClrType, column.Name);
            prop.IsRequired(false);
            if (column.ClrType == typeof(string))
            {
                prop.HasMaxLength(512);
            }
        }
```

Add `using Moongazing.OrionAudit.Configuration;` to the top of the file.

- [ ] **Step 5: Wire `AddOrionAudit` → `ApplyOrionAuditConfigurations`**

The consumer's `OnModelCreating` calls `ApplyOrionAuditConfigurations()` (no DI). To carry the registered columns into the EF model, the documented contract for consumers becomes:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyOrionAuditConfigurations(
        customColumns: OrionAuditConfigurationAccessor.CustomColumns);
}
```

But that requires accessor plumbing. Cleaner: expose the configured columns through a DI-resolvable static-ish accessor. The simplest working design — add an internal singleton `IAuditConfiguration` resolution at model-creation time via the `DbContext.GetService<>()` extension. EF Core's `OnModelCreating` runs inside a `DbContext` instance, and `context.GetService<IAuditConfiguration>()` works after DI is built.

Update `ApplyOrionAuditConfigurations` to support a `DbContext`-overload that auto-pulls the registered columns. Add this overload to `AuditModelBuilderExtensions.cs`:

```csharp
    /// <summary>
    /// DbContext-aware overload that picks up <see cref="CustomColumn"/>s registered via
    /// <c>AddOrionAudit</c>. Prefer this over the parameter-list overload; it keeps
    /// <c>OnModelCreating</c> bodies short.
    /// </summary>
    public static ModelBuilder ApplyOrionAuditConfigurations(
        this ModelBuilder modelBuilder,
        DbContext context,
        string? auditLogTableName = null,
        string? snapshotCursorTableName = null,
        OrionAuditColumnHints columnHints = OrionAuditColumnHints.Auto,
        string? captureQueueTableName = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        var customs = context.GetService<IAuditConfiguration>()?.CustomColumns
            ?? Array.Empty<CustomColumn>();

        return ApplyOrionAuditConfigurations(
            modelBuilder, auditLogTableName, snapshotCursorTableName, columnHints,
            captureQueueTableName, customs);
    }
```

Add `using Microsoft.EntityFrameworkCore.Infrastructure;` for `GetService`.

Document in the README that consumers using `AddColumn` should switch to the `(this, this)` overload:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyOrionAuditConfigurations(this);
}
```

The original parameterless `ApplyOrionAuditConfigurations()` keeps working unchanged for consumers that don't use `AddColumn`.

- [ ] **Step 6: Build + run**

```
dotnet build OrionAudit.sln -c Debug 2>&1 | tail -6
./tests/Moongazing.OrionAudit.Tests/bin/Debug/net10.0/Moongazing.OrionAudit.Tests.exe -class Moongazing.OrionAudit.Tests.AuditLogCustomColumnMappingTests 2>&1 | tail -5
```
Expected: 1 test pass.

- [ ] **Step 7: Commit**

```bash
git add src/Moongazing.OrionAudit/Core/AuditLogEntityTypeConfiguration.cs src/Moongazing.OrionAudit/DependencyInjection/AuditModelBuilderExtensions.cs tests/Moongazing.OrionAudit.Tests/AuditLogCustomColumnMappingTests.cs
git commit -m "feat(addcolumn): map custom columns as AuditLog shadow properties"
```

---

### Task 5: Interceptor sync path writes custom-column values

**Files:**
- Modify: `src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs`
- Test: `tests/Moongazing.OrionAudit.IntegrationTests/AddColumnSyncTests.cs`

- [ ] **Step 1: Write the failing integration test**

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.IntegrationTests;

public class AddColumnSyncTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    private sealed class SyncDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public SyncDb(DbContextOptions<SyncDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    [Fact]
    public async Task AddColumn_Value_Lands_On_AuditLog_ShadowProperty()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _c = conn;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<SyncDb>(o => o
            .Audit<Note>()
            .AddColumn<string>("Source", ctx => ctx.Action == AuditAction.Inserted ? "import" : "app")
            .AddColumn<int>("Length", ctx => ((Note)ctx.Entity).Body.Length));
        services.AddSingleton(conn);
        services.AddDbContext<SyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SyncDb>().Database.EnsureCreatedAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<SyncDb>();
            ctx.Notes.Add(new Note { Body = "hello" });
            await ctx.SaveChangesAsync();
        }

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<SyncDb>();
        var log = await vctx.AuditLogs.SingleAsync();
        Assert.Equal("import", EF.Property<string?>(log, "Source"));
        Assert.Equal(5, EF.Property<int?>(log, "Length"));
    }

    [Fact]
    public async Task AddColumn_ProviderThrows_RowStillWritten_WithErrorAnnotation_AndNullColumn()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _c = conn;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<SyncDb>(o => o
            .Audit<Note>()
            .AddColumn<int>("Boom", _ => throw new InvalidOperationException("nope")));
        services.AddSingleton(conn);
        services.AddDbContext<SyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SyncDb>().Database.EnsureCreatedAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<SyncDb>();
            ctx.Notes.Add(new Note { Body = "x" });
            await ctx.SaveChangesAsync();
        }

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<SyncDb>();
        var log = await vctx.AuditLogs.SingleAsync();
        Assert.NotNull(log.Error);
        Assert.Contains("Boom", log.Error!, StringComparison.Ordinal);
        Assert.Null(EF.Property<int?>(log, "Boom"));
    }
}
```

- [ ] **Step 2: Build to verify failure**

Expected: tests build but `Source`/`Length`/`Boom` values come back NULL (interceptor doesn't apply them yet).

- [ ] **Step 3: Apply custom columns in the sync path**

In `AuditSaveChangesInterceptor.cs`, the sync path adds the audit row with `ctx.Add(auditLog)`. Right after that, before `if (auditLog.Error is null) { writtenCount++; } else { failedCount++; }`, iterate registered custom columns and set their shadow properties:

```csharp
            ApplyCustomColumns(ctx, auditLog, entry, configuration, action, user, tenantId);
```

Add the private static helper:

```csharp
    private static void ApplyCustomColumns(
        DbContext ctx,
        AuditLog auditLog,
        EntityEntry entry,
        IAuditConfiguration configuration,
        AuditAction action,
        AuditUser? user,
        string? tenantId)
    {
        if (configuration.CustomColumns.Count == 0)
        {
            return;
        }
        var auditCtx = new AuditColumnContext(entry.Entity, entry, action, user, tenantId);
        foreach (var column in configuration.CustomColumns)
        {
            try
            {
                var value = column.Provider(auditCtx);
                ctx.Entry(auditLog).Property(column.Name).CurrentValue = value;
            }
#pragma warning disable CA1031 // single bad provider must not abort the save
            catch (Exception ex)
#pragma warning restore CA1031
            {
                auditLog.Error = string.IsNullOrEmpty(auditLog.Error)
                    ? $"AddColumn '{column.Name}': {ex.Message}"
                    : auditLog.Error + $"; AddColumn '{column.Name}': {ex.Message}";
            }
        }
    }
```

Add `using Microsoft.EntityFrameworkCore;` and `using Moongazing.OrionAudit.Configuration;` if missing.

- [ ] **Step 4: Build + run**

```
dotnet build OrionAudit.sln -c Debug 2>&1 | tail -6
./tests/Moongazing.OrionAudit.IntegrationTests/bin/Debug/net10.0/Moongazing.OrionAudit.IntegrationTests.exe -class Moongazing.OrionAudit.IntegrationTests.AddColumnSyncTests 2>&1 | tail -5
```
Expected: 2 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs tests/Moongazing.OrionAudit.IntegrationTests/AddColumnSyncTests.cs
git commit -m "feat(addcolumn): interceptor writes custom-column values in sync mode"
```

---

## Phase 2 — `AddColumn` async-mode wiring

### Task 6: `OrionAudit_Capture_Queue.CustomColumnsJson` column

**Files:**
- Modify: `src/Moongazing.OrionAudit/Core/AuditCaptureQueueEntry.cs`
- Modify: `src/Moongazing.OrionAudit/Core/AuditCaptureQueueEntityTypeConfiguration.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/AuditCaptureQueueConfigurationTests.cs` (extend)

- [ ] **Step 1: Extend the existing test**

In `AuditCaptureQueueConfigurationTests.cs`, add a third `[Fact]`:

```csharp
    [Fact]
    public void CustomColumnsJson_Is_Nullable_Text()
    {
        var options = new DbContextOptionsBuilder<QueueDb>().UseInMemoryDatabase("queue-customs").Options;
        using var db = new QueueDb(options);
        var prop = db.Model.FindEntityType(typeof(AuditCaptureQueueEntry))!
            .FindProperty(nameof(AuditCaptureQueueEntry.CustomColumnsJson))!;
        Assert.Equal(typeof(string), prop.ClrType);
        Assert.True(prop.IsNullable);
    }
```

- [ ] **Step 2: Build to verify failure**

Expected: `AuditCaptureQueueEntry.CustomColumnsJson` undefined.

- [ ] **Step 3: Add the property to `AuditCaptureQueueEntry`**

In `AuditCaptureQueueEntry.cs`, add after `Error`:

```csharp
    /// <summary>
    /// JSON object mapping custom-column name → captured value. Populated by the interceptor's
    /// async branch when <c>OrionAuditOptions.AddColumn</c> registrations are present; the
    /// dispatcher deserialises and applies each value to the final <see cref="AuditLog"/>.
    /// Null when no custom columns are configured (or all providers returned null).
    /// </summary>
    public string? CustomColumnsJson { get; set; }
```

- [ ] **Step 4: Map the column**

In `AuditCaptureQueueEntityTypeConfiguration.cs`, in `Configure`, after the existing `Property(x => x.ClaimToken)` line:

```csharp
        builder.Property(x => x.CustomColumnsJson);
```

(No `HasMaxLength`/`IsRequired` — defaults to nullable text.)

- [ ] **Step 5: Build + run**

Expected: 3 tests pass in `AuditCaptureQueueConfigurationTests`.

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionAudit/Core/AuditCaptureQueueEntry.cs src/Moongazing.OrionAudit/Core/AuditCaptureQueueEntityTypeConfiguration.cs tests/Moongazing.OrionAudit.Tests/AuditCaptureQueueConfigurationTests.cs
git commit -m "feat(addcolumn): add CustomColumnsJson column to OrionAudit_Capture_Queue"
```

---

### Task 7: Interceptor async path serialises custom columns

**Files:**
- Modify: `src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs`
- Test: `tests/Moongazing.OrionAudit.IntegrationTests/AddColumnAsyncQueueTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.IntegrationTests;

public class AddColumnAsyncQueueTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    private sealed class AsyncDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditCaptureQueueEntry> Queue => Set<AuditCaptureQueueEntry>();
        public AsyncDb(DbContextOptions<AsyncDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    [Fact]
    public async Task AsyncMode_Interceptor_Serialises_CustomColumns_Into_QueueRow()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _c = conn;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<AsyncDb>(o => o
            .Audit<Note>()
            .UseAsyncCapture()
            .AddColumn<int>("Length", ctx => ((Note)ctx.Entity).Body.Length)
            .AddColumn<string>("Source", _ => "test"));
        services.AddSingleton(conn);
        services.AddDbContext<AsyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AsyncDb>().Database.EnsureCreatedAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            ctx.Notes.Add(new Note { Body = "hi!" });
            await ctx.SaveChangesAsync();
        }

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<AsyncDb>();
        var queued = await vctx.Queue.SingleAsync();
        Assert.NotNull(queued.CustomColumnsJson);
        Assert.Contains("\"Length\":3", queued.CustomColumnsJson!, StringComparison.Ordinal);
        Assert.Contains("\"Source\":\"test\"", queued.CustomColumnsJson!, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Build to verify failure**

Expected: `CustomColumnsJson` is null on the queue row (async branch doesn't populate it yet).

- [ ] **Step 3: Serialise custom columns in `BuildQueueEntry`**

In `AuditSaveChangesInterceptor.cs`, modify `BuildQueueEntry` to populate `CustomColumnsJson`. After the `return new AuditCaptureQueueEntry { ... }` block, the easiest pattern is to compute the JSON before building the entry. Replace the `return new AuditCaptureQueueEntry { ... }` with:

```csharp
        var customsJson = SerializeCustomColumns(entry, configuration, user, tenantId, action);

        return new AuditCaptureQueueEntry
        {
            EntityType = entityType.AssemblyQualifiedName!,
            EntityId = ExtractPrimaryKey(entry),
            Action = action,
            BeforeJson = beforeNode.ToJsonString(),
            AfterJson = afterNode.ToJsonString(),
            UserId = user?.Id,
            UserDisplay = user?.DisplayName,
            UserType = user?.Type,
            TenantId = tenantId,
            CorrelationId = correlationId,
            OccurredOnUtc = occurredOn,
            Attempts = 0,
            CustomColumnsJson = customsJson,
        };
```

Add the helper next to `BuildQueueEntry`:

```csharp
    private static string? SerializeCustomColumns(
        EntityEntry entry,
        IAuditConfiguration configuration,
        AuditUser? user,
        string? tenantId,
        AuditAction action)
    {
        if (configuration.CustomColumns.Count == 0)
        {
            return null;
        }
        var auditCtx = new AuditColumnContext(entry.Entity, entry, action, user, tenantId);
        var obj = new JsonObject();
        var hasAny = false;
        foreach (var column in configuration.CustomColumns)
        {
            try
            {
                var value = column.Provider(auditCtx);
                if (value is null)
                {
                    obj[column.Name] = null;
                }
                else
                {
                    obj[column.Name] = JsonValue.Create(value);
                }
                hasAny = true;
            }
#pragma warning disable CA1031 // single bad provider must not abort the save
            catch
#pragma warning restore CA1031
            {
                // Failure annotated on AuditLog in sync path; in async path the dispatcher's
                // BuildAuditLog will leave the column NULL — drop it here and continue.
            }
        }
        return hasAny ? obj.ToJsonString() : null;
    }
```

`using Moongazing.OrionAudit.Configuration;` and `using System.Text.Json.Nodes;` should already be present.

- [ ] **Step 4: Build + run**

Expected: 1 test pass + all prior async tests still green.

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs tests/Moongazing.OrionAudit.IntegrationTests/AddColumnAsyncQueueTests.cs
git commit -m "feat(addcolumn): interceptor serialises custom columns into queue row in async mode"
```

---

### Task 8: Dispatcher deserialises and applies custom columns

**Files:**
- Modify: `src/Moongazing.OrionAudit/Capture/AuditDispatcher.cs`
- Test: `tests/Moongazing.OrionAudit.IntegrationTests/AddColumnAsyncDispatchTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.IntegrationTests;

public class AddColumnAsyncDispatchTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    private sealed class AsyncDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditCaptureQueueEntry> Queue => Set<AuditCaptureQueueEntry>();
        public AsyncDb(DbContextOptions<AsyncDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    [Fact]
    public async Task Dispatcher_Applies_CustomColumns_From_QueueJson_To_AuditLog()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _c = conn;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<AsyncDb>(o => o
            .Audit<Note>()
            .UseAsyncCapture()
            .AddColumn<int>("Length", ctx => ((Note)ctx.Entity).Body.Length)
            .AddColumn<string>("Source", _ => "test"));
        services.AddSingleton(conn);
        services.AddDbContext<AsyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AsyncDb>().Database.EnsureCreatedAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            ctx.Notes.Add(new Note { Body = "hi!" });
            await ctx.SaveChangesAsync();
        }

        await sp.GetRequiredService<IAuditDispatcher>().FlushPendingAsync();

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<AsyncDb>();
        var log = await vctx.AuditLogs.SingleAsync();
        Assert.Equal(3, EF.Property<int?>(log, "Length"));
        Assert.Equal("test", EF.Property<string?>(log, "Source"));
        Assert.Equal(0, await vctx.Queue.CountAsync());
    }
}
```

- [ ] **Step 2: Build to verify failure**

Expected: `Length`/`Source` columns are NULL on AuditLog (dispatcher doesn't apply them yet).

- [ ] **Step 3: Apply custom columns in `AuditDispatcher.BuildAuditLog`**

In `AuditDispatcher.cs`, the constructor already takes `SnapshotPolicy`. Inject `IAuditConfiguration` too. Update constructor + fields:

```csharp
    private readonly IAuditConfiguration configuration;

    public AuditDispatcher(
        IServiceScopeFactory scopeFactory,
        AsyncCaptureOptions options,
        SnapshotPolicy snapshotPolicy,
        IAuditConfiguration configuration,
        TimeProvider clock,
        ILogger<AuditDispatcher<TDbContext>> logger)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.snapshotPolicy = snapshotPolicy ?? throw new ArgumentNullException(nameof(snapshotPolicy));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
```

(`IAuditConfiguration` is already singleton-registered by `AddOrionAudit` — DI auto-injects.)

In `BuildAuditLog`, after the `ctx.Add(auditLog)` call there is no such call — the row is built and returned, then `ctx.Add(auditLog)` is invoked by the caller (`DispatchOnceAsync`). So apply custom columns there. Change `DispatchOnceAsync`'s per-row block to:

Currently:
```csharp
            var auditLog = BuildAuditLog(ctx, row);
            ctx.Add(auditLog);
            ctx.Set<AuditCaptureQueueEntry>().Remove(row);
            processed++;
```

Change to:
```csharp
            var auditLog = BuildAuditLog(ctx, row);
            ctx.Add(auditLog);
            ApplyCustomColumnsFromQueue(ctx, auditLog, row);
            ctx.Set<AuditCaptureQueueEntry>().Remove(row);
            processed++;
```

And add the helper method:

```csharp
    private void ApplyCustomColumnsFromQueue(TDbContext ctx, AuditLog auditLog, AuditCaptureQueueEntry row)
    {
        if (configuration.CustomColumns.Count == 0 || string.IsNullOrEmpty(row.CustomColumnsJson))
        {
            return;
        }
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(row.CustomColumnsJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }
        if (node is not JsonObject customs)
        {
            return;
        }
        foreach (var column in configuration.CustomColumns)
        {
            if (customs[column.Name] is not JsonValue v)
            {
                continue;
            }
            try
            {
                var clr = v.Deserialize(column.ClrType, JsonSerializerOptions.Default);
                ctx.Entry(auditLog).Property(column.Name).CurrentValue = clr;
            }
#pragma warning disable CA1031 // a malformed value must not abort the batch
            catch
#pragma warning restore CA1031
            {
                auditLog.Error = string.IsNullOrEmpty(auditLog.Error)
                    ? $"AddColumn '{column.Name}': dispatch deserialize failed"
                    : auditLog.Error + $"; AddColumn '{column.Name}': dispatch deserialize failed";
            }
        }
    }
```

Add `using System.Text.Json;` if not present.

- [ ] **Step 4: Build + run**

Expected: 1 new test pass + prior async tests still green.

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionAudit/Capture/AuditDispatcher.cs tests/Moongazing.OrionAudit.IntegrationTests/AddColumnAsyncDispatchTests.cs
git commit -m "feat(addcolumn): dispatcher applies custom columns from queue JSON to AuditLog"
```

---

## Phase 3 — Read-side surface

### Task 9: `AuditEntryView.CustomColumns`

**Files:**
- Modify: `src/Moongazing.OrionAudit/Read/AuditView.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/AuditViewRendererTests.cs` (extend)

- [ ] **Step 1: Extend the renderer test**

Append to `AuditViewRendererTests.cs`:

```csharp
    [Fact]
    public void Render_CopiesCustomColumns_From_EntityEntry()
    {
        // The renderer reads custom columns from a DbContext-backed EntityEntry on the
        // single-row Render(...) overload. Verify the simpler dictionary-input overload.
        var row = Log("[]");
        var customs = new Dictionary<string, object?>
        {
            { "Source", "app" },
            { "Length", 7 },
            { "RequestId", null },
        };
        var view = AuditViewRenderer.Render(row, customs);
        Assert.Equal("app", view.CustomColumns["Source"]);
        Assert.Equal(7, view.CustomColumns["Length"]);
        Assert.Null(view.CustomColumns["RequestId"]);
    }

    [Fact]
    public void Render_WithoutCustomColumns_HasEmptyDictionary()
    {
        var view = AuditViewRenderer.Render(Log("[]"));
        Assert.NotNull(view.CustomColumns);
        Assert.Empty(view.CustomColumns);
    }
```

- [ ] **Step 2: Build to verify failure**

Expected: `CustomColumns` undefined on `AuditEntryView`; `Render(row, dict)` overload missing.

- [ ] **Step 3: Add `CustomColumns` to `AuditEntryView`**

In `AuditView.cs`, in the `AuditEntryView` class after `Changes`:

```csharp
    /// <summary>
    /// Consumer-registered custom columns (from <c>OrionAuditOptions.AddColumn</c>),
    /// projected from the underlying <see cref="AuditLog"/> row's shadow properties.
    /// Empty when no columns are configured.
    /// </summary>
    public IReadOnlyDictionary<string, object?> CustomColumns { get; init; }
        = new Dictionary<string, object?>(StringComparer.Ordinal);
```

- [ ] **Step 4: Extend `Render` overloads**

After the existing `Render(AuditLog row)`:

```csharp
    /// <summary>Renders one audit row, attaching the supplied custom-column values.</summary>
    public static AuditEntryView Render(AuditLog row, IReadOnlyDictionary<string, object?> customColumns)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(customColumns);
        return new AuditEntryView
        {
            Id = row.Id,
            Action = row.Action,
            OccurredOnUtc = row.OccurredOnUtc,
            UserDisplay = row.UserDisplay,
            CorrelationId = row.CorrelationId,
            Changes = ParseChanges(row.Diff),
            CustomColumns = customColumns,
        };
    }
```

The existing `Render(AuditLog row)` keeps producing an empty-dictionary view; the
`RenderMany(rows)` overload also stays empty for the dictionary unless a caller plumbs values.
Endpoint code (Task 10) does the EF.Property lookup and calls the two-arg overload.

- [ ] **Step 5: Build + run**

```
dotnet build OrionAudit.sln -c Debug 2>&1 | tail -6
./tests/Moongazing.OrionAudit.Tests/bin/Debug/net10.0/Moongazing.OrionAudit.Tests.exe -class Moongazing.OrionAudit.Tests.AuditViewRendererTests 2>&1 | tail -5
```
Expected: all existing + 2 new tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionAudit/Read/AuditView.cs tests/Moongazing.OrionAudit.Tests/AuditViewRendererTests.cs
git commit -m "feat(addcolumn): AuditEntryView.CustomColumns + two-arg Render overload"
```

---

### Task 10: Viewer API projects custom columns into responses

**Files:**
- Modify: `src/Moongazing.OrionAudit.Viewer/OrionAuditViewerApi.cs`
- Modify: `src/Moongazing.OrionAudit.Viewer/wwwroot/index.html`
- Test: `tests/Moongazing.OrionAudit.Viewer.Tests/ViewerCustomColumnsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Viewer;

namespace Moongazing.OrionAudit.Viewer.Tests;

public class ViewerCustomColumnsTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    public sealed class ApiDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public ApiDb(DbContextOptions<ApiDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    private sealed record EntryDto(string action, Dictionary<string, object?> customColumns);
    private sealed record LogPage(IReadOnlyList<EntryDto> entries);

    [Fact]
    public async Task LogEndpoint_Includes_CustomColumns()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _c = conn;

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddAuthentication();
                    s.AddAuthorization();
                    s.AddSingleton(conn);
                    s.AddOrionAudit<ApiDb>(o => o
                        .Audit<Note>()
                        .AddColumn<int>("Length", ctx => ((Note)ctx.Entity).Body.Length));
                    s.AddDbContext<ApiDb>((sp, o) =>
                        o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                        e.MapOrionAuditViewer<ApiDb>("/audit", o => o.AllowAnonymous()));
                }))
            .StartAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApiDb>();
            await ctx.Database.EnsureCreatedAsync();
            ctx.Notes.Add(new Note { Body = "hello" });
            await ctx.SaveChangesAsync();
        }

        var page = await host.GetTestServer().CreateClient()
            .GetFromJsonAsync<LogPage>("/audit/api/log?page=1&size=20");

        Assert.NotNull(page);
        var entry = Assert.Single(page!.entries);
        Assert.True(entry.customColumns.ContainsKey("Length"));
        // JSON number → JsonElement → boxed int when deserialised through Dictionary<string, object?>
        Assert.Equal(5, ((System.Text.Json.JsonElement)entry.customColumns["Length"]!).GetInt32());
    }
}
```

- [ ] **Step 2: Build to verify failure**

Expected: `customColumns` field missing from `/api/log` response.

- [ ] **Step 3: Project custom columns into the API responses**

In `OrionAuditViewerApi.cs`, change `/api/log` and `/api/{entityType}/{key}` to use the two-arg `AuditViewRenderer.Render` overload, looking up each registered column via `EF.Property`:

```csharp
    public static void Map<TDbContext>(RouteGroupBuilder group)
        where TDbContext : DbContext
    {
        group.MapGet("/api/log", async (
            [FromServices] TDbContext db,
            [FromServices] IAuditConfiguration config,
            int? page, int? size) =>
        {
            var take = Math.Clamp(size is null or <= 0 ? 50 : size.Value, 1, 500);
            var skip = Math.Max((page ?? 1) - 1, 0) * take;
            var rows = await db.AuditLog()
                .OrderByDescending(a => a.OccurredOnUtc)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
            var views = rows.Select(r => AuditViewRenderer.Render(r, ProjectCustoms(db, r, config))).ToList();
            return Results.Ok(new { entries = views });
        });

        group.MapGet("/api/{entityType}/{key}", async (
            [FromServices] TDbContext db,
            [FromServices] IAuditConfiguration config,
            string entityType, string key) =>
        {
            var rows = await db.AuditLog()
                .Where(a => a.EntityType == entityType && a.EntityId == key)
                .OrderBy(a => a.OccurredOnUtc)
                .ToListAsync();
            var views = rows.Select(r => AuditViewRenderer.Render(r, ProjectCustoms(db, r, config))).ToList();
            return Results.Ok(new { entries = views });
        });

        group.MapGet("/api/meta", async (
            [FromServices] IAuditConfiguration config,
            [FromServices] IAuditDispatcher dispatcher) =>
        {
            var queueDepth = await dispatcher.GetQueueDepthAsync();
            return Results.Ok(new
            {
                auditedTypes = config.AuditedTypeNames,
                queueDepth,
                customColumnNames = config.CustomColumns.Select(c => c.Name).ToArray(),
            });
        });
    }

    private static IReadOnlyDictionary<string, object?> ProjectCustoms(
        DbContext db, AuditLog row, IAuditConfiguration config)
    {
        if (config.CustomColumns.Count == 0)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        var entry = db.Entry(row);
        var result = new Dictionary<string, object?>(config.CustomColumns.Count, StringComparer.Ordinal);
        foreach (var column in config.CustomColumns)
        {
            result[column.Name] = entry.Property(column.Name).CurrentValue;
        }
        return result;
    }
```

(`EF.Property<T>` works in LINQ-to-SQL; for already-tracked entities `entry.Property(name).CurrentValue` is the in-memory equivalent and avoids a second round-trip.)

- [ ] **Step 4: Update the SPA to render custom-column badges**

In `wwwroot/index.html`, in the entry-head builder block, after the existing user span, append:

```javascript
    if (e.customColumns) {
      for (const [k, v] of Object.entries(e.customColumns)) {
        if (v === null || v === undefined) continue;
        const badge = document.createElement('span');
        badge.className = 'badge';
        badge.textContent = k + ': ' + v;
        head.appendChild(badge);
      }
    }
```

- [ ] **Step 5: Build + run**

```
dotnet build OrionAudit.sln -c Debug 2>&1 | tail -6
./tests/Moongazing.OrionAudit.Viewer.Tests/bin/Debug/net10.0/Moongazing.OrionAudit.Viewer.Tests.exe 2>&1 | tail -5
```
Expected: all Viewer tests (existing + 1 new) pass.

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionAudit.Viewer/OrionAuditViewerApi.cs src/Moongazing.OrionAudit.Viewer/wwwroot/index.html tests/Moongazing.OrionAudit.Viewer.Tests/ViewerCustomColumnsTests.cs
git commit -m "feat(addcolumn): viewer API + SPA project and render custom columns"
```

---

## Phase 4 — `AuditImportBuilder`

### Task 11: `AuditImportOptions` and `ImportResult`

**Files:**
- Create: `src/Moongazing.OrionAudit/Configuration/AuditImportOptions.cs`
- Create: `src/Moongazing.OrionAudit/Configuration/ImportResult.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/AuditImportOptionsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AuditImportOptionsTests
{
    [Fact]
    public void Defaults_AreSensible()
    {
        var o = new AuditImportOptions();
        Assert.Equal(1000, o.BatchSize);
        Assert.Null(o.ImportBatch);
    }

    [Fact]
    public void BatchSize_Rejects_NonPositive()
    {
        var o = new AuditImportOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => o.BatchSize = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => o.BatchSize = -1);
    }

    [Fact]
    public void ImportBatch_Rejects_NullOrWhitespace()
    {
        var o = new AuditImportOptions();
        Assert.Throws<ArgumentException>(() => o.ImportBatch = "");
        Assert.Throws<ArgumentException>(() => o.ImportBatch = "   ");
    }

    [Fact]
    public void ImportResult_DefaultsToZeros()
    {
        var r = new ImportResult(0, 0, 0);
        Assert.Equal(0, r.Written);
        Assert.Equal(0, r.Skipped);
        Assert.Equal(0, r.DeadLettered);
    }
}
```

- [ ] **Step 2: Build to verify failure**

Expected: types undefined.

- [ ] **Step 3: Create `AuditImportOptions.cs`**

```csharp
namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// Tunables passed to <c>DbContext.CreateAuditImport(o =&gt; ...)</c>. The
/// <see cref="ImportBatch"/> string is REQUIRED before <c>SaveAsync</c> — it drives
/// idempotency by stamping <c>AuditLog.CorrelationId</c>.
/// </summary>
public sealed class AuditImportOptions
{
    private int batchSize = 1000;
    private string? importBatch;

    /// <summary>How many rows the importer writes per transaction. Default: 1000.</summary>
    public int BatchSize
    {
        get => batchSize;
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(BatchSize), value, "Must be >= 1.");
            }
            batchSize = value;
        }
    }

    /// <summary>
    /// Stable, per-import label. Stamped into <c>AuditLog.CorrelationId</c> as
    /// <c>import:{ImportBatch}#{SourceId}</c> so re-runs are idempotent.
    /// Required; <c>SaveAsync</c> throws if null.
    /// </summary>
    public string? ImportBatch
    {
        get => importBatch;
        set
        {
            if (value is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
            }
            importBatch = value;
        }
    }
}
```

- [ ] **Step 4: Create `ImportResult.cs`**

```csharp
namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// Result of <c>AuditImportBuilder.SaveAsync</c>. <see cref="Written"/> rows landed in
/// <see cref="AuditLog"/>; <see cref="Skipped"/> rows were already present (matched the
/// idempotency tag); <see cref="DeadLettered"/> rows failed and were written with
/// <see cref="AuditLog.Error"/> populated.
/// </summary>
public sealed record ImportResult(int Written, int Skipped, int DeadLettered);
```

- [ ] **Step 5: Build + run**

Expected: 4 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionAudit/Configuration/AuditImportOptions.cs src/Moongazing.OrionAudit/Configuration/ImportResult.cs tests/Moongazing.OrionAudit.Tests/AuditImportOptionsTests.cs
git commit -m "feat(import): add AuditImportOptions and ImportResult"
```

---

### Task 12: `AuditImportBuilder` core + per-record builder

**Files:**
- Create: `src/Moongazing.OrionAudit/Configuration/AuditImportBuilder.cs`
- Create: `src/Moongazing.OrionAudit/DependencyInjection/AuditImportExtensions.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/AuditImportBuilderApiTests.cs`

This task adds the public API surface (record-builder + `Add` + early validation) without
the `SaveAsync` body. Save is in Task 13.

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AuditImportBuilderApiTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    private sealed class ImportDb : DbContext
    {
        public ImportDb(DbContextOptions<ImportDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    private static ImportDb BuildDb()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<ImportDb>(o => o.Audit<Note>());
        services.AddDbContext<ImportDb>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<ImportDb>();
    }

    [Fact]
    public void CreateAuditImport_Returns_NonNullBuilder_With_DefaultOptions()
    {
        var importer = BuildDb().CreateAuditImport();
        Assert.NotNull(importer);
    }

    [Fact]
    public void Add_WithoutKey_Throws()
    {
        var importer = BuildDb().CreateAuditImport(o => o.ImportBatch = "b");
        Assert.Throws<InvalidOperationException>(() => importer.Add<Note>(e => e
            .Action(AuditAction.Inserted)
            .After(new Note())));
    }

    [Fact]
    public void Add_WithoutAction_Throws()
    {
        var importer = BuildDb().CreateAuditImport(o => o.ImportBatch = "b");
        Assert.Throws<InvalidOperationException>(() => importer.Add<Note>(e => e
            .Key(Guid.NewGuid())
            .After(new Note())));
    }

    [Fact]
    public void Add_WithColumn_UnregisteredColumn_Throws()
    {
        var importer = BuildDb().CreateAuditImport(o => o.ImportBatch = "b");
        Assert.Throws<OrionAuditConfigurationException>(() => importer.Add<Note>(e => e
            .Key(Guid.NewGuid())
            .Action(AuditAction.Inserted)
            .After(new Note())
            .WithColumn("UnregisteredCol", 1)));
    }

    [Fact]
    public async Task SaveAsync_WithoutImportBatch_Throws()
    {
        var importer = BuildDb().CreateAuditImport();   // no ImportBatch
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.SaveAsync());
    }
}
```

- [ ] **Step 2: Build to verify failure**

Expected: `AuditImportBuilder` and `CreateAuditImport` undefined.

- [ ] **Step 3: Create `AuditImportExtensions.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit;

/// <summary><see cref="DbContext"/> extensions for bulk legacy-history import.</summary>
public static class AuditImportExtensions
{
    /// <summary>
    /// Creates a fresh <see cref="AuditImportBuilder"/> for this DbContext. Set
    /// <see cref="AuditImportOptions.ImportBatch"/> before calling <c>SaveAsync</c>.
    /// </summary>
    public static AuditImportBuilder CreateAuditImport(
        this DbContext context,
        Action<AuditImportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var opts = new AuditImportOptions();
        configure?.Invoke(opts);

        var configuration = context.GetService<IAuditConfiguration>()
            ?? throw new OrionAuditConfigurationException(
                "AuditImport requires AddOrionAudit<TContext>(...) to be configured on the container.");

        return new AuditImportBuilder(
            context,
            opts,
            configuration,
            context.GetService<System.Text.Json.Serialization.JsonSerializerContext>());
    }
}
```

- [ ] **Step 4: Create `AuditImportBuilder.cs` (API skeleton — `SaveAsync` lands in Task 13)**

```csharp
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// Fluent bulk-import builder. Construct via <c>DbContext.CreateAuditImport(...)</c>;
/// call <see cref="Add{T}"/> per record then <see cref="SaveAsync"/>. <see cref="SaveAsync"/>
/// can be called multiple times to resume after a partial failure — idempotency stamps
/// matched-already rows as <c>Skipped</c>.
/// </summary>
public sealed class AuditImportBuilder
{
    private readonly DbContext context;
    private readonly AuditImportOptions options;
    private readonly IAuditConfiguration configuration;
    private readonly JsonSerializerContext? jsonContext;
    private readonly List<PendingRecord> pending = new();

    internal AuditImportBuilder(
        DbContext context,
        AuditImportOptions options,
        IAuditConfiguration configuration,
        JsonSerializerContext? jsonContext)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.jsonContext = jsonContext;
    }

    /// <summary>Adds one record to the buffer. Throws if mandatory fields are missing.</summary>
    public AuditImportBuilder Add<T>(Action<AuditImportRecord<T>> configure) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var record = new AuditImportRecord<T>(configuration);
        configure(record);
        record.Validate();
        pending.Add(record.ToPending());
        return this;
    }

    /// <summary>Drains the buffer to <see cref="AuditLog"/>. Implemented in Task 13.</summary>
    public Task<ImportResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ImportBatch))
        {
            throw new InvalidOperationException("AuditImportOptions.ImportBatch is required before SaveAsync.");
        }
        throw new NotImplementedException("SaveAsync — Task 13.");
    }

    internal sealed class PendingRecord
    {
        public Type EntityType { get; init; } = default!;
        public string KeyString { get; init; } = default!;
        public AuditAction Action { get; init; }
        public object? Before { get; init; }
        public object? After { get; init; }
        public string? UserId { get; init; }
        public string? UserDisplay { get; init; }
        public string? UserType { get; init; }
        public string? TenantId { get; init; }
        public string? SourceId { get; init; }
        public DateTime OccurredOnUtc { get; init; }
        public Dictionary<string, object?>? CustomColumns { get; init; }
    }
}

/// <summary>Builder for a single import record passed to <c>Add&lt;T&gt;</c>.</summary>
public sealed class AuditImportRecord<T> where T : class
{
    private readonly IAuditConfiguration configuration;
    private string? keyString;
    private AuditAction? action;
    private object? before;
    private object? after;
    private string? userId;
    private string? userDisplay;
    private string? userType;
    private string? tenantId;
    private string? sourceId;
    private DateTime occurredOnUtc = DateTime.UtcNow;
    private Dictionary<string, object?>? customColumns;

    internal AuditImportRecord(IAuditConfiguration configuration)
        => this.configuration = configuration;

    /// <summary>Primary key — converted to string via <c>ToString()</c>.</summary>
    public AuditImportRecord<T> Key(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        keyString = key.ToString() ?? throw new ArgumentException("Key.ToString() returned null.", nameof(key));
        return this;
    }

    /// <summary>Composite key — passes through <see cref="AuditKey.From"/>.</summary>
    public AuditImportRecord<T> Key(params object?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        keyString = AuditKey.From(parts);
        return this;
    }

    public AuditImportRecord<T> Action(AuditAction value) { action = value; return this; }
    public AuditImportRecord<T> Before(T? state) { before = state; return this; }
    public AuditImportRecord<T> After(T? state) { after = state; return this; }
    public AuditImportRecord<T> By(string? id, string? display = null, string? type = null)
        { userId = id; userDisplay = display; userType = type; return this; }
    public AuditImportRecord<T> Tenant(string? value) { tenantId = value; return this; }
    public AuditImportRecord<T> SourceId(object? value) { sourceId = value?.ToString(); return this; }
    public AuditImportRecord<T> At(DateTime utc) { occurredOnUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc); return this; }

    /// <summary>Sets a previously-registered custom column's value for this record.</summary>
    public AuditImportRecord<T> WithColumn(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!configuration.CustomColumns.Any(c => string.Equals(c.Name, name, StringComparison.Ordinal)))
        {
            throw new OrionAuditConfigurationException(
                $"AuditImport.WithColumn '{name}': column is not registered via AddColumn.");
        }
        customColumns ??= new Dictionary<string, object?>(StringComparer.Ordinal);
        customColumns[name] = value;
        return this;
    }

    internal void Validate()
    {
        if (keyString is null)
        {
            throw new InvalidOperationException($"AuditImport record for '{typeof(T).Name}': Key(...) is required.");
        }
        if (action is null)
        {
            throw new InvalidOperationException($"AuditImport record for '{typeof(T).Name}': Action(...) is required.");
        }
    }

    internal AuditImportBuilder.PendingRecord ToPending() => new()
    {
        EntityType = typeof(T),
        KeyString = keyString!,
        Action = action!.Value,
        Before = before,
        After = after,
        UserId = userId,
        UserDisplay = userDisplay,
        UserType = userType,
        TenantId = tenantId,
        SourceId = sourceId,
        OccurredOnUtc = occurredOnUtc,
        CustomColumns = customColumns,
    };
}
```

- [ ] **Step 5: Build + run**

Expected: 5 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionAudit/Configuration/AuditImportBuilder.cs src/Moongazing.OrionAudit/DependencyInjection/AuditImportExtensions.cs tests/Moongazing.OrionAudit.Tests/AuditImportBuilderApiTests.cs
git commit -m "feat(import): add AuditImportBuilder fluent API + CreateAuditImport extension"
```

---

### Task 13: `AuditImportBuilder.SaveAsync` — diff + batched write + idempotency

**Files:**
- Modify: `src/Moongazing.OrionAudit/Configuration/AuditImportBuilder.cs`
- Test: `tests/Moongazing.OrionAudit.IntegrationTests/AuditImportSaveTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.IntegrationTests;

public class AuditImportSaveTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    private sealed class ImportDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditCaptureQueueEntry> Queue => Set<AuditCaptureQueueEntry>();
        public ImportDb(DbContextOptions<ImportDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    private static async Task<(ServiceProvider sp, SqliteConnection conn)> BuildAsync(Action<OrionAuditOptions> configure)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<ImportDb>(configure);
        services.AddSingleton(conn);
        services.AddDbContext<ImportDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ImportDb>().Database.EnsureCreatedAsync();
        }
        return (sp, conn);
    }

    [Fact]
    public async Task SaveAsync_Writes_RowPerRecord_With_NonEmptyDiff()
    {
        var (sp, conn) = await BuildAsync(o => o.Audit<Note>());
        await using var _c = conn;
        await using var _s = sp;

        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ImportDb>();

        var id = Guid.NewGuid();
        var importer = ctx.CreateAuditImport(o => o.ImportBatch = "legacy-1");
        importer.Add<Note>(e => e.Key(id).Action(AuditAction.Inserted).After(new Note { Id = id, Body = "v1" }).At(DateTime.UtcNow));
        importer.Add<Note>(e => e.Key(id).Action(AuditAction.Updated).Before(new Note { Id = id, Body = "v1" }).After(new Note { Id = id, Body = "v2" }).At(DateTime.UtcNow));
        var result = await importer.SaveAsync();

        Assert.Equal(2, result.Written);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.DeadLettered);

        var logs = await ctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.StartsWith("import:legacy-1", logs[0].CorrelationId);
        Assert.NotEqual("[]", logs[1].Diff);
    }

    [Fact]
    public async Task SaveAsync_Twice_With_SameBatch_IsIdempotent()
    {
        var (sp, conn) = await BuildAsync(o => o.Audit<Note>());
        await using var _c = conn;
        await using var _s = sp;

        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ImportDb>();

        var id = Guid.NewGuid();
        var importer1 = ctx.CreateAuditImport(o => o.ImportBatch = "legacy-2");
        importer1.Add<Note>(e => e.Key(id).Action(AuditAction.Inserted).After(new Note { Id = id, Body = "x" }).SourceId(1));
        Assert.Equal(1, (await importer1.SaveAsync()).Written);

        var importer2 = ctx.CreateAuditImport(o => o.ImportBatch = "legacy-2");
        importer2.Add<Note>(e => e.Key(id).Action(AuditAction.Inserted).After(new Note { Id = id, Body = "x" }).SourceId(1));
        var r2 = await importer2.SaveAsync();
        Assert.Equal(0, r2.Written);
        Assert.Equal(1, r2.Skipped);

        Assert.Equal(1, await ctx.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_Bypasses_CaptureQueue_When_AsyncMode_On()
    {
        var (sp, conn) = await BuildAsync(o => o.Audit<Note>().UseAsyncCapture());
        await using var _c = conn;
        await using var _s = sp;

        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ImportDb>();

        var importer = ctx.CreateAuditImport(o => o.ImportBatch = "legacy-3");
        importer.Add<Note>(e => e.Key(Guid.NewGuid()).Action(AuditAction.Inserted).After(new Note { Body = "x" }));
        var result = await importer.SaveAsync();

        Assert.Equal(1, result.Written);
        Assert.Equal(1, await ctx.AuditLogs.CountAsync());
        Assert.Equal(0, await ctx.Queue.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_Diff_IsByteEqual_With_SyncCapture()
    {
        // Sync: real interceptor capture.
        var (syncSp, syncConn) = await BuildAsync(o => o.Audit<Note>());
        await using var _c1 = syncConn;
        await using var _s1 = syncSp;
        var id = Guid.NewGuid();
        await using (var scope = syncSp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ImportDb>();
            ctx.Notes.Add(new Note { Id = id, Body = "v1" });
            await ctx.SaveChangesAsync();
        }
        await using (var scope = syncSp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ImportDb>();
            var n = await ctx.Notes.SingleAsync();
            n.Body = "v2";
            await ctx.SaveChangesAsync();
        }
        string syncUpdateDiff;
        await using (var scope = syncSp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ImportDb>();
            syncUpdateDiff = (await ctx.AuditLogs.SingleAsync(a => a.Action == AuditAction.Updated)).Diff;
        }

        // Import: same Update via the importer.
        var (impSp, impConn) = await BuildAsync(o => o.Audit<Note>());
        await using var _c2 = impConn;
        await using var _s2 = impSp;
        await using var impScope = impSp.CreateAsyncScope();
        var impCtx = impScope.ServiceProvider.GetRequiredService<ImportDb>();
        var importer = impCtx.CreateAuditImport(o => o.ImportBatch = "parity");
        importer.Add<Note>(e => e
            .Key(id)
            .Action(AuditAction.Updated)
            .Before(new Note { Id = id, Body = "v1" })
            .After(new Note { Id = id, Body = "v2" }));
        await importer.SaveAsync();
        var importUpdateDiff = (await impCtx.AuditLogs.SingleAsync(a => a.Action == AuditAction.Updated)).Diff;

        Assert.Equal(syncUpdateDiff, importUpdateDiff);
    }
}
```

- [ ] **Step 2: Build to verify failure**

Expected: `SaveAsync` throws `NotImplementedException`.

- [ ] **Step 3: Implement `SaveAsync`**

In `AuditImportBuilder.cs`, replace the `SaveAsync` stub:

```csharp
    /// <summary>
    /// Drains the buffer to <see cref="AuditLog"/>. Always writes directly (bypasses the
    /// async-capture queue). Each call processes the records added since the last call.
    /// </summary>
    public async Task<ImportResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ImportBatch))
        {
            throw new InvalidOperationException("AuditImportOptions.ImportBatch is required before SaveAsync.");
        }
        if (pending.Count == 0)
        {
            return new ImportResult(0, 0, 0);
        }

        var tag = options.ImportBatch!;
        var prefix = $"import:{tag}";

        var existingCorrelations = await context.Set<AuditLog>()
            .Where(a => a.CorrelationId != null && a.CorrelationId.StartsWith(prefix))
            .Select(a => a.CorrelationId!)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existing = new HashSet<string>(existingCorrelations, StringComparer.Ordinal);

        var written = 0;
        var skipped = 0;
        var deadLettered = 0;

        var batch = new List<AuditLog>(Math.Min(pending.Count, options.BatchSize));

        foreach (var record in pending)
        {
            var correlation = record.SourceId is null
                ? prefix
                : $"{prefix}#{record.SourceId}";

            if (existing.Contains(correlation))
            {
                skipped++;
                continue;
            }

            AuditLog row;
            try
            {
                row = BuildAuditLog(record, correlation);
            }
#pragma warning disable CA1031 // a malformed record must not abort the batch
            catch (Exception ex)
#pragma warning restore CA1031
            {
                row = new AuditLog
                {
                    EntityType = record.EntityType.AssemblyQualifiedName!,
                    EntityId = record.KeyString,
                    Action = record.Action,
                    OccurredOnUtc = record.OccurredOnUtc,
                    UserId = record.UserId,
                    UserDisplay = record.UserDisplay,
                    UserType = record.UserType,
                    TenantId = record.TenantId,
                    CorrelationId = correlation,
                    Diff = "[]",
                    Error = ex.ToString(),
                };
                deadLettered++;
            }

            if (row.Error is null)
            {
                written++;
            }
            batch.Add(row);
            ApplyRecordCustomColumns(row, record);

            if (batch.Count >= options.BatchSize)
            {
                await FlushAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }

        if (batch.Count > 0)
        {
            await FlushAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        pending.Clear();
        return new ImportResult(written, skipped, deadLettered);
    }

    private async Task FlushAsync(List<AuditLog> batch, CancellationToken cancellationToken)
    {
        await context.Set<AuditLog>().AddRangeAsync(batch, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        batch.Clear();
    }

    private AuditLog BuildAuditLog(PendingRecord record, string correlation)
    {
        var beforeValues = record.Before is null
            ? new Dictionary<string, object?>()
            : ToValueDictionary(record.EntityType, record.Before);
        var afterValues = record.After is null
            ? new Dictionary<string, object?>()
            : ToValueDictionary(record.EntityType, record.After);

        var beforeNode = jsonContext is not null
            ? Capture.SnapshotBuilder.Build(record.EntityType, beforeValues, configuration, jsonContext)
            : Capture.SnapshotBuilder.Build(record.EntityType, beforeValues, configuration);
        var afterNode = jsonContext is not null
            ? Capture.SnapshotBuilder.Build(record.EntityType, afterValues, configuration, jsonContext)
            : Capture.SnapshotBuilder.Build(record.EntityType, afterValues, configuration);

        var diff = Capture.DiffEngine.Compute(beforeNode, afterNode);

        var log = new AuditLog
        {
            EntityType = record.EntityType.AssemblyQualifiedName!,
            EntityId = record.KeyString,
            Action = record.Action,
            OccurredOnUtc = record.OccurredOnUtc,
            UserId = record.UserId,
            UserDisplay = record.UserDisplay,
            UserType = record.UserType,
            TenantId = record.TenantId,
            CorrelationId = correlation,
            Diff = diff,
        };

        if (record.Action == AuditAction.Deleted)
        {
            log.Snapshot = beforeNode.ToJsonString();
        }
        else if (record.Action == AuditAction.SoftDeleted)
        {
            log.Snapshot = afterNode.ToJsonString();
        }
        return log;
    }

    private void ApplyRecordCustomColumns(AuditLog row, PendingRecord record)
    {
        if (record.CustomColumns is null || record.CustomColumns.Count == 0)
        {
            return;
        }
        foreach (var (name, value) in record.CustomColumns)
        {
            context.Entry(row).Property(name).CurrentValue = value;
        }
    }

    private static Dictionary<string, object?> ToValueDictionary(Type entityType, object entity)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in entityType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!p.CanRead)
            {
                continue;
            }
            dict[p.Name] = p.GetValue(entity);
        }
        return dict;
    }
```

Add usings: `using System.Linq;`, `using Moongazing.OrionAudit.Capture;` (or fully qualify as above).

Note: `ToValueDictionary` uses reflection over public instance properties — matches what the
sync interceptor effectively does via `EntityEntry.Properties` (which enumerates EF's mapped
properties). For importer scenarios where the consumer constructs the entity directly, this
reflective scan is correct. The `SnapshotBuilder` then applies sensitive-field rules and
serializes via the registered context, so the parity test (§Step 1) is the actual contract.

Excluding the primary-key property keeps the diff strictly about state, mirroring the sync
path's `SnapshotValues` (which skips PKs). Add a PK-exclusion step:

```csharp
    private Dictionary<string, object?> ToValueDictionary(Type entityType, object entity)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        var pkNames = GetPrimaryKeyNames(entityType);
        foreach (var p in entityType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!p.CanRead || pkNames.Contains(p.Name))
            {
                continue;
            }
            dict[p.Name] = p.GetValue(entity);
        }
        return dict;
    }

    private HashSet<string> GetPrimaryKeyNames(Type entityType)
    {
        var et = context.Model.FindEntityType(entityType);
        var pk = et?.FindPrimaryKey();
        if (pk is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        return pk.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }
```

(`ToValueDictionary` and `GetPrimaryKeyNames` become instance methods because they need `context`.)

- [ ] **Step 4: Build + run**

Expected: 4 tests pass, including the byte-equality parity test.

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionAudit/Configuration/AuditImportBuilder.cs tests/Moongazing.OrionAudit.IntegrationTests/AuditImportSaveTests.cs
git commit -m "feat(import): AuditImportBuilder.SaveAsync — batched write, idempotent, byte-equal diff"
```

---

### Task 14: Import telemetry

**Files:**
- Modify: `src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs`
- Modify: `src/Moongazing.OrionAudit/Configuration/AuditImportBuilder.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/OrionAuditTelemetryTests.cs` (extend)

- [ ] **Step 1: Extend the telemetry test**

In `OrionAuditTelemetryTests.cs`, add the import-instrument assertions to the existing
`Meter_Exposes_DispatchInstruments` test (rename or duplicate):

```csharp
    [Fact]
    public void Meter_Exposes_ImportInstruments()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(OrionAuditTelemetry).TypeHandle);

        var names = new List<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == OrionAuditTelemetry.MeterName)
            {
                names.Add(instrument.Name);
            }
        };
        listener.Start();

        Assert.Contains("orionaudit.import.rows_written", names);
        Assert.Contains("orionaudit.import.rows_skipped", names);
        Assert.Contains("orionaudit.import.rows_deadlettered", names);
        Assert.Contains("orionaudit.import.batch.duration", names);
    }
```

- [ ] **Step 2: Add instruments**

In `OrionAuditTelemetry.cs`, after `DispatchQueueDepth`:

```csharp
    internal static readonly Counter<long> ImportRowsWritten = Meter.CreateCounter<long>(
        "orionaudit.import.rows_written", unit: "rows", description: "Audit rows written by the bulk importer.");

    internal static readonly Counter<long> ImportRowsSkipped = Meter.CreateCounter<long>(
        "orionaudit.import.rows_skipped", unit: "rows", description: "Bulk-import rows skipped via idempotency tag.");

    internal static readonly Counter<long> ImportRowsDeadLettered = Meter.CreateCounter<long>(
        "orionaudit.import.rows_deadlettered", unit: "rows", description: "Bulk-import rows written with Error populated.");

    internal static readonly Histogram<double> ImportBatchDuration = Meter.CreateHistogram<double>(
        "orionaudit.import.batch.duration", unit: "ms", description: "AuditImportBuilder SaveAsync duration.");
```

- [ ] **Step 3: Wire into `SaveAsync`**

In `AuditImportBuilder.SaveAsync`, wrap the body in a stopwatch + activity and publish counters at the end. At the top of the method (after the `ImportBatch` guard):

```csharp
        using var activity = OrionAuditTelemetry.ActivitySource.StartActivity(
            "OrionAudit.Import", System.Diagnostics.ActivityKind.Internal);
        var sw = System.Diagnostics.Stopwatch.StartNew();
```

At the end, before `return new ImportResult(...)`:

```csharp
        OrionAuditTelemetry.ImportRowsWritten.Add(written);
        OrionAuditTelemetry.ImportRowsSkipped.Add(skipped);
        OrionAuditTelemetry.ImportRowsDeadLettered.Add(deadLettered);
        OrionAuditTelemetry.ImportBatchDuration.Record(sw.Elapsed.TotalMilliseconds);
        activity?.SetTag("orionaudit.import.rows_written", written);
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
```

- [ ] **Step 4: Build + run**

Expected: telemetry test passes; SaveAsync tests still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs src/Moongazing.OrionAudit/Configuration/AuditImportBuilder.cs tests/Moongazing.OrionAudit.Tests/OrionAuditTelemetryTests.cs
git commit -m "feat(import): add import telemetry counters + activity"
```

---

## Phase 5 — Release

### Task 15: Version bump

**Files:**
- Modify: `Directory.Build.props`
- Modify: `src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs`

- [ ] **Step 1: Bump `Directory.Build.props`**

Change `<Version>0.5.1</Version>` (or whatever current is) to `<Version>0.6.0</Version>`.

- [ ] **Step 2: Bump telemetry version**

In `OrionAuditTelemetry.cs`, change both `new(ActivitySourceName, "0.5.0")` (or current) and `new Meter(MeterName, "0.5.0")` to `"0.6.0"`. Use Edit `replace_all` for the `"0.5.0"` → `"0.6.0"` swap if those are the only matches in this file.

- [ ] **Step 3: Build to confirm clean**

`dotnet build OrionAudit.sln -c Debug 2>&1 | tail -6` — expect 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs
git commit -m "release: bump version and telemetry to 0.6.0"
```

---

### Task 16: Documentation

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `ROADMAP.md`
- Modify: `ECOSYSTEM.md`
- Modify: `README.md`

- [ ] **Step 1: Add the CHANGELOG entry**

Insert under the `## [0.5.x] - ...` heading:

```markdown
## [0.6.0] - 2026-05-24

Developer Experience release. Adds two opt-in features that unblock common adoption
scenarios: extensible `AuditLog` rows for custom indexable dimensions, and bulk legacy-history
import with byte-equal diffs.

### Added

- **`o.AddColumn<T>(name, ctx => value)`.** Registers tipped, indexable EF shadow-property
  columns on `AuditLog`. Value provider receives an `AuditColumnContext` with the audited
  entity, EF entry, action, user, and tenant. Provider failures degrade to NULL plus an
  `AuditLog.Error` annotation — never abort the save.
- **Async-mode integration for custom columns.** `OrionAudit_Capture_Queue` gains a nullable
  `CustomColumnsJson` column; the interceptor's async branch serialises provider values, the
  dispatcher deserialises and applies them to the final `AuditLog` row.
- **`AuditImportBuilder`.** Fluent bulk-import of hand-rolled change history as synthetic
  `AuditLog` rows via `db.CreateAuditImport(o => o.ImportBatch = "tag")`. Diff produced by
  the same `Json6902` engine the capture path uses (byte-equal parity verified by test).
  Mandatory `ImportBatch` tag stamped into `CorrelationId` gives per-record idempotency via
  `SourceId`; re-running `SaveAsync` is safe and reports duplicate rows as `Skipped`.
  Always writes `AuditLog` directly — bypasses the capture queue in both sync and async modes.
- **Read-side `AuditEntryView.CustomColumns`** (`IReadOnlyDictionary<string, object?>`)
  projected by the Viewer API into `/api/log` and `/api/{entityType}/{key}` responses; the
  embedded SPA renders each non-null custom column as a header badge. `/api/meta` adds a
  `customColumnNames` list.
- **Import telemetry.** `OrionAudit.Import` activity, counters
  `orionaudit.import.rows_written` / `orionaudit.import.rows_skipped` /
  `orionaudit.import.rows_deadlettered`, histogram `orionaudit.import.batch.duration`.

### Changed

- `ApplyOrionAuditConfigurations` gained a `(this, this)` DbContext-aware overload that
  picks up registered `CustomColumn`s automatically. The parameter-list overload also gained
  a `customColumns` parameter for advanced scenarios.
- `IAuditConfiguration` gained a `CustomColumns` collection.
- `AuditDispatcher` now resolves `IAuditConfiguration` from DI to apply custom columns
  during dispatch.

### Migration from v0.5.0

- **Sync consumers not using `AddColumn` or import:** no code change.
- **Schema:** one EF migration adds `OrionAudit_Capture_Queue.CustomColumnsJson` (nullable
  text). The column is always mapped; it stays NULL when empty. Same precedent as v0.2.0's
  `SnapshotCursor` and v0.5.0's queue table.
- **Adopting `AddColumn`:** one EF migration per column on `OrionAudit_Log`. Pair with
  `migrationBuilder.CreateIndex(...)` if you'll filter on it. Switch
  `OnModelCreating` to `modelBuilder.ApplyOrionAuditConfigurations(this);` so registered
  columns are picked up automatically.
- **Adopting `AuditImportBuilder`:** opt-in API; no schema impact beyond the queue-column
  migration above. `ImportBatch` is mandatory — pick a stable per-import string.
```

- [ ] **Step 2: Update ROADMAP**

In `ROADMAP.md`, find `## v0.6.0 — Developer Experience *(planned, Q3 2026)*` and change the
heading to `## v0.6.0 — Developer Experience *(shipped)*`. Update the release-cadence table:

```
| v0.6.0    | extensible columns + import helper  | developer experience  — shipped |
```

(Keep the v0.7.0 row planned.)

- [ ] **Step 3: Update ECOSYSTEM**

In `ECOSYSTEM.md`, change OrionAudit row version to `v0.6.0` and headline mentions "extensible
columns + import".

- [ ] **Step 4: Update README**

Add a "What's new in v0.6.0" section near the v0.5.0 one:

```markdown
## What's new in v0.6.0

### `AddColumn` — tipped, indexable custom columns

```csharp
services.AddOrionAudit<AppDbContext>(o => o
    .Audit<Order>()
    .AddColumn<int>("WorkflowStepId", ctx => (ctx.Entity as IHasWorkflow)?.StepId)
    .AddColumn<string>("Source", ctx => ctx.Action == AuditAction.Inserted ? "import" : "app"));

// OnModelCreating: pick up custom columns automatically.
modelBuilder.ApplyOrionAuditConfigurations(this);

// LINQ filter on a real, indexable column:
var fromStep3 = await db.AuditLog()
    .Where(a => EF.Property<int?>(a, "WorkflowStepId") == 3)
    .ToListAsync();
```

Add a `CreateIndex` in your EF migration for any column you'll filter on. The provider runs
inside the capture transaction with the audited entity in scope; failure annotates
`AuditLog.Error` and leaves the column NULL.

### `AuditImportBuilder` — bulk historical import, idempotent

```csharp
var import = db.CreateAuditImport(o => o
    .BatchSize(1000)
    .ImportBatch("legacy-orders-2026"));

import.Add<Order>(e => e
    .Key(legacy.OrderId)
    .Action(AuditAction.Updated)
    .Before(oldState).After(newState)
    .By("u-123", "Legacy User")
    .At(legacy.ChangedAtUtc)
    .SourceId(legacy.RowId));

var result = await import.SaveAsync();
// result.Written / Skipped / DeadLettered
```

`ImportBatch` is mandatory — it stamps `AuditLog.CorrelationId` so re-running `SaveAsync` is
safe. Imported diffs are byte-equal to the diffs the live capture path produces. Import
always writes `AuditLog` directly, bypassing the async-capture queue.
```

(Also add a brief migration note callout if the existing README has a v0.5.x callout pattern.)

- [ ] **Step 5: Commit**

```bash
git add CHANGELOG.md ROADMAP.md ECOSYSTEM.md README.md
git commit -m "docs(release): document v0.6.0 — extensible columns + AuditImportBuilder"
```

---

### Task 17: Final full-solution verification

**Files:** none modified.

- [ ] **Step 1: Full Release build**

```
dotnet build OrionAudit.sln -c Release 2>&1 | tail -6
```
Expected: 0 warnings, 0 errors across all TFMs.

- [ ] **Step 2: Run every test project in Release**

```
for proj in Moongazing.OrionAudit.Tests Moongazing.OrionAudit.IntegrationTests Moongazing.OrionAudit.AspNetCore.Tests Moongazing.OrionAudit.Testing.Tests Moongazing.OrionAudit.Viewer.Tests; do
  echo "===$proj===" && ./tests/$proj/bin/Release/net10.0/$proj.exe 2>&1 | grep -E "TEST EXECUTION SUMMARY|Total:"
done
```
Expected: every project `Total: N, Failed: 0` (skips OK). A flake in
`AuditScopeTests.NoScope_FallsBackToActivityOrNull` is pre-existing and not blocking — re-run
once to confirm.

- [ ] **Step 3: AOT probe IL check**

```
dotnet publish aot/Moongazing.OrionAudit.AotProbe -c Release -r win-x64 2>&1 | tail -10
```
Expected: compilation step emits no `IL2*` / `IL3*` warnings. The native linker step may fail
on a Windows machine without the Visual Studio C++ workload — that's an environment limitation,
not a code defect; CI's Linux + clang setup handles it. The IL-trim analyzer pass is the
v0.6.0 contract.

- [ ] **Step 4: Merge worktree branch into master and tag**

(Assumes worktree branch `worktree-orionaudit-v0.6.0`; substitute your branch name.)

```
git switch master
git merge worktree-orionaudit-v0.6.0 --no-ff -m "Merge v0.6.0 — extensible AuditLog row + AuditImportBuilder

See CHANGELOG.md [0.6.0] for the full release notes; spec at
docs/superpowers/specs/2026-05-24-orionaudit-v0.6.0-design.md, plan at
docs/superpowers/plans/2026-05-24-orionaudit-v0.6.0.md."
git tag -a v0.6.0 -m "v0.6.0 — Developer Experience"
git push origin master --tags
```

CI runs `build-and-test` + `aot-publish-check` on the push. The `publish` job (NuGet) fires
only when a GitHub Release is created from the `v0.6.0` tag.

---

## Self-Review

**Spec coverage:**

- §2 in-scope item 1 (`AddColumn<T>` + `AuditColumnContext`) — Tasks 1, 2.
- §2 in-scope item 2 (`AuditLog` shadow-property mapping) — Task 4.
- §2 in-scope item 3 (`OrionAudit_Capture_Queue.CustomColumnsJson` + serialise/deserialise) — Tasks 6, 7, 8.
- §2 in-scope item 4 (`AuditImportBuilder` + `ImportBatch` idempotency + custom-column support + queue-bypass) — Tasks 11, 12, 13.
- §2 in-scope item 5 (`AuditEntryView.CustomColumns` + viewer SPA badges) — Tasks 9, 10.
- §2 in-scope item 6 (telemetry, version, docs) — Tasks 14, 15, 16.
- §3 invariants (atomic+lossless capture; byte-equal import diff) — Task 5 covers
  provider-failure-annotates-Error contract; Task 13 has a byte-equality parity test.
- §4 surface (sub-sections 4.1–4.9) — distributed across Tasks 1, 2, 4, 5, 7, 8.
- §5 importer surface (5.1–5.8) — Tasks 11, 12, 13.
- §6 telemetry — Task 14.
- §7 read-side surface — Tasks 9, 10.
- §8 testing matrix — every task has its own test slot; the byte-equality contract is a
  dedicated test in Task 13. Provider-throws + provider-null cases are in Task 5. Idempotency
  is in Task 13. Queue-bypass is in Task 13.
- §9 versioning — Task 15.
- §10 docs — Task 16.
- §11 release — Task 17.
- §12 migration — Task 16's CHANGELOG covers it.

**Placeholder scan:** no "TBD", no "implement later", every code-bearing step contains the
full code. Two intentional forward references — Task 12 says `SaveAsync` is implemented in
Task 13 (the stub throws `NotImplementedException`), explicitly called out.

**Type consistency:** `AuditColumnContext` defined in Task 1 used identically in Tasks 2, 5,
7. `CustomColumn` defined in Task 1 referenced everywhere with the same `Name` / `ClrType` /
`Provider` shape. `AuditImportBuilder.SaveAsync` signature returns `Task<ImportResult>` —
matches `ImportResult(int Written, int Skipped, int DeadLettered)` from Task 11. The
`ApplyOrionAuditConfigurations(this)` `DbContext`-aware overload (Task 4) is the one consumer
tests use throughout Tasks 5, 7, 8, 10, 13.

**Known soft spot:** Task 4 Step 5 introduces a `DbContext`-aware
`ApplyOrionAuditConfigurations` overload to plumb custom columns from DI into the EF model. If
the consumer's `OnModelCreating` already calls the parameterless overload and they add an
`AddColumn` without switching, the column won't be mapped and the interceptor's
`ctx.Entry(auditLog).Property(name).CurrentValue = ...` will throw at runtime (no such
property). The Task 16 CHANGELOG migration note is the safeguard.
