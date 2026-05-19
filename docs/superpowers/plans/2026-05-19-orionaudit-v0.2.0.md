# OrionAudit v0.2.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (or
> subagent-driven-development) to implement this plan task-by-task. Steps use checkbox
> (`- [ ]`) syntax for tracking.

**Goal:** Ship v0.2.0 (Reliability & Scale). Six features from the [v0.2.0 design spec][spec]:
composite primary keys, periodic snapshotting, retention policy, provider-aware column types,
soft-delete capture, and the `AuditScope` correlation override.

**Spec:** [docs/superpowers/specs/2026-05-19-orionaudit-v0.2.0-design.md][spec]

**Predecessor:** v0.1.0 — released `2026-05-19` from commit `90a89d9`.

**Branching:** All work lands on `master` via small focused commits. Each task's tests pass
before the commit. No long-lived branches.

**NuGet IDs unchanged:** `OrionAudit`, `OrionAudit.AspNetCore`, `OrionAudit.Testing`. Bumping
`<Version>` to `0.2.0` is the final task.

[spec]: ../specs/2026-05-19-orionaudit-v0.2.0-design.md

---

## Task ordering rationale

Tasks build on each other:

1. **Composite keys (Task 1)** has no dependencies — small, isolated, clears the v0.1
   limitation that blocks adoption for many EF Core models.
2. **`AuditScope` correlation override (Task 2)** also independent — clean
   `AsyncLocal<string?>` + interceptor read; needed for snapshot tests that span scopes.
3. **Soft-delete capture (Task 3)** introduces `AuditAction.SoftDeleted` and the per-entity
   `[SoftDelete]` mechanism — touches the same interceptor as snapshotting will, easier to
   land first.
4. **Periodic snapshotting (Task 4)** is the largest behavioural change — needs the
   `OrionAudit_Snapshot_Cursors` table, policy types, and reconstructor rewrite to find &
   replay-from-snapshot.
5. **Retention policy (Task 5)** layers on a hosted service; doesn't change capture or read
   paths.
6. **Provider-aware column types (Task 6)** is the final polish — pure schema
   metadata, no behavioural change.
7. **Release (Task 7)** — version bump, CHANGELOG, sample/bench update, tag, publish.

---

## Task 1: Composite primary key support

**Files:**
- Modify: `src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs` (`ExtractPrimaryKey`)
- Create: `src/Moongazing.OrionAudit/AuditKey.cs` (public helper)
- Modify: `src/Moongazing.OrionAudit/Core/OrionAuditConfigurationException.cs` (drop composite-key throw text — keep the type, change the message catalog references)
- Test: `tests/Moongazing.OrionAudit.Tests/CompositeKeyTests.cs`

### Step 1: Failing tests

Create `tests/Moongazing.OrionAudit.Tests/CompositeKeyTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class CompositeKeyTests
{
    [Auditable]
    public sealed class Translation
    {
        public string TenantId { get; set; } = "";
        public Guid DocumentId { get; set; } = Guid.NewGuid();
        public string Locale { get; set; } = "";
        public string Body { get; set; } = "";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Translation> Translations => Set<Translation>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Translation>().HasKey(t => new { t.TenantId, t.DocumentId, t.Locale });
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private static async Task<TestContext> NewAsync()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Translation>());
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));
        return await Task.FromResult(services.BuildServiceProvider().GetRequiredService<TestContext>());
    }

    [Fact]
    public async Task Insert_OnCompositeKeyEntity_WritesAuditRowWithJoinedKey()
    {
        await using var ctx = await NewAsync();
        var t = new Translation { TenantId = "acme", DocumentId = Guid.NewGuid(), Locale = "en", Body = "hello" };
        ctx.Translations.Add(t);
        await ctx.SaveChangesAsync();

        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.Equal($"acme|{t.DocumentId}|en", entry.EntityId);
    }

    [Fact]
    public async Task AuditKey_From_ProducesSameShapeAsInterceptor()
    {
        await using var ctx = await NewAsync();
        var docId = Guid.NewGuid();
        ctx.Translations.Add(new Translation { TenantId = "acme", DocumentId = docId, Locale = "tr", Body = "merhaba" });
        await ctx.SaveChangesAsync();

        var rendered = AuditKey.From("acme", docId, "tr");
        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.Equal(rendered, entry.EntityId);
    }

    [Fact]
    public async Task Reconstruct_AcceptsCompositeKey_AndReplaysHistory()
    {
        await using var ctx = await NewAsync();
        var docId = Guid.NewGuid();
        ctx.Translations.Add(new Translation { TenantId = "acme", DocumentId = docId, Locale = "en", Body = "v1" });
        await ctx.SaveChangesAsync();

        var t = await ctx.Translations.FirstAsync();
        t.Body = "v2";
        await ctx.SaveChangesAsync();

        var key = AuditKey.From(t.TenantId, t.DocumentId, t.Locale);
        var reconstructor = new Read.AuditReconstructor(ctx);
        var rebuilt = await reconstructor.ReconstructAsync<Translation>(key, DateTime.UtcNow.AddMinutes(1));
        Assert.NotNull(rebuilt);
        Assert.Equal("v2", rebuilt!.Body);
    }
}
```

### Step 2: Implement `AuditKey`

Create `src/Moongazing.OrionAudit/AuditKey.cs`:

```csharp
namespace Moongazing.OrionAudit;

/// <summary>Helpers for serialising composite primary keys into the canonical AuditLog.EntityId form.</summary>
public static class AuditKey
{
    /// <summary>Separator between key components. Reserved in component values — see <see cref="From"/>.</summary>
    public const char Separator = '|';

    /// <summary>Renders the supplied key components into a stable string.</summary>
    /// <remarks>
    /// Each component is converted with <c>ToString()</c> (invariant for primitives). Literal
    /// <c>|</c> characters in source values are URL-percent-escaped to <c>%7C</c> so the join is
    /// unambiguous.
    /// </remarks>
    public static string From(params object?[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Length == 0)
        {
            throw new ArgumentException("At least one component is required.", nameof(components));
        }
        if (components.Length == 1)
        {
            return components[0]?.ToString() ?? throw new ArgumentException("Component cannot be null.", nameof(components));
        }
        return string.Join(Separator, components.Select(c =>
            (c?.ToString() ?? throw new ArgumentException("Component cannot be null.", nameof(components)))
                .Replace("|", "%7C", StringComparison.Ordinal)));
    }
}
```

### Step 3: Rewrite `ExtractPrimaryKey`

Replace the composite-key throw in `AuditSaveChangesInterceptor`:

```csharp
private static string ExtractPrimaryKey(EntityEntry entry)
{
    var pk = entry.Metadata.FindPrimaryKey()
        ?? throw new OrionAuditConfigurationException(
            $"Entity '{entry.Metadata.Name}' has no primary key configured.");

    if (pk.Properties.Count == 1)
    {
        var single = pk.Properties[0];
        return entry.Property(single.Name).CurrentValue?.ToString()
            ?? throw new InvalidOperationException($"Primary key value for entity '{entry.Metadata.Name}' is null.");
    }

    var parts = new object?[pk.Properties.Count];
    for (var i = 0; i < pk.Properties.Count; i++)
    {
        parts[i] = entry.Property(pk.Properties[i].Name).CurrentValue
            ?? throw new InvalidOperationException(
                $"Composite primary key component '{pk.Properties[i].Name}' on '{entry.Metadata.Name}' is null.");
    }
    return AuditKey.From(parts);
}
```

### Step 4: Build + run tests + commit

```bash
dotnet build OrionAudit.sln -c Debug
tests/Moongazing.OrionAudit.Tests/bin/Debug/net10.0/Moongazing.OrionAudit.Tests.exe
```

Expected: existing tests + 3 new tests pass.

```bash
git add src/Moongazing.OrionAudit/AuditKey.cs \
        src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs \
        tests/Moongazing.OrionAudit.Tests/CompositeKeyTests.cs
git commit -m "feat(capture): support composite primary keys via AuditKey serialisation"
```

---

## Task 2: `AuditScope` correlation override

**Files:**
- Create: `src/Moongazing.OrionAudit/AuditScope.cs`
- Modify: `src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs` (replace `Activity.Current?.Id` with `AuditScope.Current ?? Activity.Current?.Id`)
- Test: `tests/Moongazing.OrionAudit.Tests/AuditScopeTests.cs`

### Step 1: Failing tests

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Tests;

public class AuditScopeTests
{
    [Auditable]
    public sealed class Job
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Status { get; set; } = "";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Job> Jobs => Set<Job>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Job>().HasKey(j => j.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private static async Task<TestContext> NewAsync()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Job>());
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));
        return await Task.FromResult(services.BuildServiceProvider().GetRequiredService<TestContext>());
    }

    [Fact]
    public async Task Push_SetsCorrelationOnAuditRow()
    {
        await using var ctx = await NewAsync();
        const string jobId = "nightly-2026-05-20";
        using (AuditScope.Push(jobId))
        {
            ctx.Jobs.Add(new Job { Status = "running" });
            await ctx.SaveChangesAsync();
        }
        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.Equal(jobId, entry.CorrelationId);
    }

    [Fact]
    public async Task NestedScopes_RestoreOuterValueOnDispose()
    {
        await using var ctx = await NewAsync();
        using (AuditScope.Push("outer"))
        {
            using (AuditScope.Push("inner"))
            {
                Assert.Equal("inner", AuditScope.Current);
            }
            Assert.Equal("outer", AuditScope.Current);
        }
        Assert.Null(AuditScope.Current);
    }

    [Fact]
    public async Task Push_FlowsAcrossAwaits()
    {
        await using var ctx = await NewAsync();
        using (AuditScope.Push("flow-test"))
        {
            await Task.Yield();
            await Task.Run(() => Assert.Equal("flow-test", AuditScope.Current));
        }
    }
}
```

### Step 2: Implement `AuditScope`

```csharp
namespace Moongazing.OrionAudit;

/// <summary>
/// Ambient correlation-id scope flowed via <see cref="AsyncLocal{T}"/>. Pushed values are
/// preferred over <c>Activity.Current?.Id</c> by the interceptor when stamping
/// <see cref="AuditLog.CorrelationId"/>.
/// </summary>
public static class AuditScope
{
    private static readonly AsyncLocal<string?> currentId = new();

    /// <summary>The correlation id active on the current async-flow, or null.</summary>
    public static string? Current => currentId.Value;

    /// <summary>Pushes a new ambient correlation id; disposing the returned scope restores the previous value.</summary>
    public static IDisposable Push(string correlationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(correlationId);
        var previous = currentId.Value;
        currentId.Value = correlationId;
        return new PopOnDispose(previous);
    }

    private sealed class PopOnDispose : IDisposable
    {
        private readonly string? previous;
        public PopOnDispose(string? previous) => this.previous = previous;
        public void Dispose() => currentId.Value = previous;
    }
}
```

### Step 3: Wire it into the interceptor

```csharp
var correlationId = AuditScope.Current ?? Activity.Current?.Id;
```

### Step 4: Build + run + commit

```bash
git add src/Moongazing.OrionAudit/AuditScope.cs \
        src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs \
        tests/Moongazing.OrionAudit.Tests/AuditScopeTests.cs
git commit -m "feat(capture): add AuditScope.Push for AsyncLocal correlation override"
```

---

## Task 3: Soft-delete capture

**Files:**
- Modify: `src/Moongazing.OrionAudit/Core/AuditAction.cs` (`SoftDeleted = 3`)
- Create: `src/Moongazing.OrionAudit/Attributes/SoftDeleteAttribute.cs`
- Modify: `src/Moongazing.OrionAudit/Configuration/AuditTypeBuilder.cs` (add `SoftDelete(selector)`)
- Modify: `src/Moongazing.OrionAudit/Configuration/AuditableTypeConfig.cs` (carry `SoftDeleteProperty`)
- Modify: `src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs` (detect flip → emit SoftDeleted)
- Modify: `src/Moongazing.OrionAudit/Read/AuditReconstructor.cs` (treat SoftDeleted like Deleted)
- Test: `tests/Moongazing.OrionAudit.Tests/SoftDeleteTests.cs`

### Step 1: Failing tests

```csharp
namespace Moongazing.OrionAudit.Tests;

public class SoftDeleteTests
{
    [Auditable]
    [SoftDelete(nameof(IsDeleted))]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
        public bool IsDeleted { get; set; }
    }

    // ... DbContext, AddOrionAudit, etc.

    [Fact]
    public async Task UpdateThatFlipsIsDeletedTrue_EmitsSoftDeletedAction()
    {
        await using var ctx = await NewAsync();
        var note = new Note { Body = "hi" };
        ctx.Notes.Add(note);
        await ctx.SaveChangesAsync();

        note.IsDeleted = true;
        await ctx.SaveChangesAsync();

        var rows = await ctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.Equal(AuditAction.Inserted, rows[0].Action);
        Assert.Equal(AuditAction.SoftDeleted, rows[1].Action);
        Assert.NotNull(rows[1].Snapshot);   // snapshot also captured for soft-delete
    }

    [Fact]
    public async Task UpdateThatDoesNotFlipIsDeleted_StaysUpdatedAction()
    {
        // body change while IsDeleted remains false → Updated, not SoftDeleted
    }

    [Fact]
    public async Task Reconstruct_AfterSoftDelete_ReturnsNull()
    {
        // history: Insert + SoftDelete → ReconstructAsync(...) → null
    }

    [Fact]
    public async Task FluentSoftDelete_WorksLikeAttribute()
    {
        // Configure via .Audit<T>(b => b.SoftDelete(x => x.IsDeleted))
    }
}
```

### Step 2: Enum + attribute + config

```csharp
// AuditAction.cs
SoftDeleted = 3,

// SoftDeleteAttribute.cs
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class SoftDeleteAttribute : Attribute
{
    public string PropertyName { get; }
    public SoftDeleteAttribute(string propertyName) { ... }
}
```

`AuditableTypeConfig` gains `string? SoftDeleteProperty { get; }`. `AuditTypeBuilder<T>` gains
`SoftDelete<TProp>(Expression<Func<T, TProp>> selector)`. `AuditConfigurationBuilder` reads the
attribute in `ApplyAttributeRules` and stores the property name.

### Step 3: Interceptor flip detection

In `BuildAuditLog`, after the existing `action` switch:

```csharp
if (action == AuditAction.Updated
    && typeConfig?.SoftDeleteProperty is { } softDeleteProp
    && entry.Properties.FirstOrDefault(p => p.Metadata.Name == softDeleteProp) is { } property
    && property.OriginalValue is false
    && property.CurrentValue is true)
{
    action = AuditAction.SoftDeleted;
}
```

When `action == AuditAction.SoftDeleted`, populate `auditLog.Snapshot` as well (same path as
`Deleted`).

### Step 4: Reconstructor parity

```csharp
if (rows[^1].Action is AuditAction.Deleted or AuditAction.SoftDeleted)
{
    return null;
}
```

### Step 5: Run + commit

```bash
git add ...
git commit -m "feat(capture): add AuditAction.SoftDeleted and [SoftDelete] attribute / fluent rule"
```

---

## Task 4: Periodic snapshotting policy

This is the largest task. Split into three commits.

**Files:**
- Create: `src/Moongazing.OrionAudit/Core/SnapshotCursor.cs`
- Create: `src/Moongazing.OrionAudit/Core/SnapshotCursorEntityTypeConfiguration.cs`
- Create: `src/Moongazing.OrionAudit/Configuration/SnapshotPolicy.cs`
- Modify: `src/Moongazing.OrionAudit/DependencyInjection/OrionAuditOptions.cs` (`SnapshotEvery(int)` / `SnapshotEvery(TimeSpan)`)
- Modify: `src/Moongazing.OrionAudit/DependencyInjection/AuditModelBuilderExtensions.cs` (also apply cursor configuration)
- Modify: `src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs` (evaluate policy, write snapshot, advance cursor)
- Modify: `src/Moongazing.OrionAudit/Read/AuditReconstructor.cs` (find latest snapshot ≤ asOf, hydrate, replay forward)
- Test: `tests/Moongazing.OrionAudit.Tests/SnapshotPolicyTests.cs` + integration test in `IntegrationTests`

### Step 1 (commit A): Schema + policy types

Add `SnapshotCursor` entity, EntityTypeConfiguration, `SnapshotPolicy` record:

```csharp
public sealed class SnapshotCursor
{
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string? TenantId { get; set; }
    public int UpdatesSinceLast { get; set; }
    public DateTime? LastSnapshotUtc { get; set; }
}

public abstract record SnapshotPolicy
{
    public static SnapshotPolicy Never { get; } = new NeverPolicy();
    public static SnapshotPolicy Every(int updates) => new EveryNthPolicy(updates);
    public static SnapshotPolicy EveryDuration(TimeSpan elapsed) => new EveryDurationPolicy(elapsed);

    internal sealed record NeverPolicy : SnapshotPolicy;
    internal sealed record EveryNthPolicy(int Updates) : SnapshotPolicy;
    internal sealed record EveryDurationPolicy(TimeSpan Elapsed) : SnapshotPolicy;
}
```

EntityTypeConfiguration applies the composite PK `(EntityType, EntityId, TenantId)` and the table
name `OrionAudit_Snapshot_Cursors`. `AuditModelBuilderExtensions.ApplyOrionAuditConfigurations`
gets a parameter to opt in to the cursor table.

Tests: cursor table mappable, policy types behave as discriminated union.

```bash
git commit -m "feat(snapshot): add SnapshotCursor table and SnapshotPolicy types"
```

### Step 2 (commit B): Interceptor writes snapshots & advances cursor

In `BuildAuditLog`, after computing `Diff`, when `action == AuditAction.Updated`:

```csharp
if (policy is SnapshotPolicy.EveryNthPolicy nth || policy is SnapshotPolicy.EveryDurationPolicy)
{
    var cursor = await GetOrCreateCursorAsync(ctx, entityTypeName, entityId, tenantId, ct);
    var shouldSnapshot = policy switch
    {
        SnapshotPolicy.EveryNthPolicy n => ++cursor.UpdatesSinceLast >= n.Updates,
        SnapshotPolicy.EveryDurationPolicy d =>
            cursor.LastSnapshotUtc is null || (occurredOn - cursor.LastSnapshotUtc) >= d.Elapsed,
        _ => false
    };
    if (shouldSnapshot)
    {
        auditLog.Snapshot = afterNode.ToJsonString();
        cursor.UpdatesSinceLast = 0;
        cursor.LastSnapshotUtc = occurredOn;
        OrionAuditTelemetry.SnapshotsWritten.Add(1);
    }
}
```

Cursor read/write happens inside the same DbContext transaction. Telemetry counter
`orionaudit.snapshots.written` added to `OrionAuditTelemetry`.

Tests: `SnapshotEvery(3)` writes a snapshot on the 3rd update; `EveryDuration` writes when
elapsed; `SnapshotPolicy.Never` (default) writes nothing.

```bash
git commit -m "feat(snapshot): interceptor writes periodic snapshots and advances cursor"
```

### Step 3 (commit C): Reconstructor uses latest snapshot ≤ asOf

`AuditReconstructor.Replay<T>` rewritten:

```csharp
var snapshotRow = rows.LastOrDefault(r =>
    r.Snapshot is not null
    && r.Action is AuditAction.Updated or AuditAction.Inserted);

JsonObject state;
int startIndex;
if (snapshotRow is not null)
{
    state = JsonNode.Parse(snapshotRow.Snapshot!)!.AsObject();
    startIndex = rows.IndexOf(snapshotRow) + 1;
}
else
{
    state = new JsonObject();
    startIndex = 0;
}

for (var i = startIndex; i < rows.Count; i++)
{
    var row = rows[i];
    if (string.IsNullOrEmpty(row.Diff) || row.Diff == "[]") continue;
    state = DiffEngine.Apply(state, row.Diff);
}

return JsonSerializer.Deserialize<T>(state);
```

Tests:
- Reconstruction with `SnapshotEvery(50)` over 200-update history → 1 snapshot read + ≤ 50
  applies.
- Reconstruction at exactly the timestamp of a snapshot → returns that snapshot's state.
- Mixed: history with snapshots at indices 50, 100, 150 → `asOf` after index 175 picks snapshot
  150 + replays 25 diffs.

```bash
git commit -m "feat(snapshot): reconstructor replays from latest snapshot for O(K) cost"
```

---

## Task 5: Retention policy + hosted service

**Files:**
- Create: `src/Moongazing.OrionAudit/Configuration/RetentionPolicy.cs`
- Modify: `src/Moongazing.OrionAudit/DependencyInjection/OrionAuditOptions.cs` (`RetainFor`, `RetainCount`, `RetentionSweepInterval`)
- Create: `src/Moongazing.OrionAudit/Retention/AuditRetentionHostedService.cs`
- Modify: `src/Moongazing.OrionAudit/DependencyInjection/AuditServiceCollectionExtensions.cs` (register hosted service when policy != none)
- Modify: `src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs` (add retention signals)
- Test: `tests/Moongazing.OrionAudit.IntegrationTests/RetentionTests.cs`

### Step 1: Failing tests

```csharp
[Fact]
public async Task RetainFor_DeletesRowsOlderThanCutoff()
{
    // Seed 20 audit rows with OccurredOnUtc spread over 30 days.
    // Configure RetainFor(TimeSpan.FromDays(7)).
    // Trigger sweep manually via service-scope.
    // Assert rows older than now-7d are gone.
}

[Fact]
public async Task RetainCount_KeepsLatestNPerEntity()
{
    // Update one entity 50 times → 50 audit rows.
    // RetainCount(10) sweep → only latest 10 remain.
}

[Fact]
public async Task Sweep_RespectsMaxRowsPerSweep()
{
    // RetainFor + 5000 stale rows, MaxRowsPerSweep=1000.
    // One sweep deletes 1000; second sweep another 1000; etc.
}
```

### Step 2: Implement policy + hosted service

`RetentionPolicy` discriminated union (Never / RetainFor(TimeSpan) / RetainCount(int)).
`AuditRetentionHostedService : BackgroundService` reads policy from options, sweeps on a
`PeriodicTimer(options.SweepInterval)` cadence, deletes in batches of
`options.MaxRowsPerSweep` per cycle.

Sweep query:

```csharp
// RetainFor
context.AuditLogs
    .Where(a => a.OccurredOnUtc < DateTime.UtcNow - retention.Span)
    .OrderBy(a => a.OccurredOnUtc)
    .Take(options.MaxRowsPerSweep)
    .ExecuteDelete();

// RetainCount: window function partition by (EntityType, EntityId, TenantId) — provider-specific SQL
```

Telemetry: `OrionAudit.Retention.Sweep` activity, `orionaudit.retention.rows_deleted` counter,
`orionaudit.retention.sweep.duration` histogram.

### Step 3: Run + commit

```bash
git commit -m "feat(retention): add RetainFor/RetainCount policies and AuditRetentionHostedService"
```

---

## Task 6: Provider-aware column types

**Files:**
- Modify: `src/Moongazing.OrionAudit/Core/AuditLogEntityTypeConfiguration.cs`
- Modify: `src/Moongazing.OrionAudit/Core/SnapshotCursorEntityTypeConfiguration.cs`
- Test: `tests/Moongazing.OrionAudit.IntegrationTests/ProviderColumnTypeTests.cs`

### Step 1: Failing tests

```csharp
[Fact]
public async Task Sqlite_DiffColumnIsText()
{
    // Build EnsureCreated against Sqlite, query sqlite_master for the column type.
}

[Fact]
public async Task PostgresProviderHint_MapsDiffToJsonb()
{
    // Use Npgsql in-memory if available (or test via metadata inspection without actually opening).
    // Asserts the EF Core model exposes jsonb type for Diff column when provider = Postgres.
}

[Fact]
public async Task SqlServer_DiffColumnIsNvarcharMax()
{
    // EF Core in-memory provider doesn't reflect provider-specific types — use Sqlite scaffold
    // + assert EF Core's column metadata declares the right SQL type via .HasColumnType.
}
```

### Step 2: Implementation

`AuditLogEntityTypeConfiguration.Configure` learns about provider name (via
`ModelBuilder.Model.GetAnnotations()` or `IDatabaseProviderInfoService`) and emits provider-
specific `HasColumnType` for `Diff` / `Snapshot`. Default mapping table:

| Provider                | Diff / Snapshot column type |
| ----------------------- | --------------------------- |
| Microsoft.EntityFrameworkCore.SqlServer | `nvarchar(max)` |
| Npgsql.EntityFrameworkCore.PostgreSQL  | `jsonb`         |
| Microsoft.EntityFrameworkCore.Sqlite   | `TEXT`          |
| (anything else)         | provider default (`nvarchar(max)` fallback) |

### Step 3: Run + commit

```bash
git commit -m "feat(schema): provider-aware column types for Diff/Snapshot"
```

---

## Task 7: Release v0.2.0

**Files:**
- Modify: `Directory.Build.props` (`<Version>0.2.0</Version>`)
- Modify: `CHANGELOG.md` (new `[0.2.0]` section under `## [Unreleased]`-style header)
- Modify: `ROADMAP.md` (mark v0.2 items shipped, move to a "Shipped" section)
- Modify: `README.md` (refresh v-aware hero callout)
- Modify: `sample/.../Program.cs` (add a "v0.2 features tour" section)
- Modify: `bench/.../ReconstructorBench.cs` (add a SnapshotPolicy-enabled variant to show the perf win)
- Tag + GitHub Release: handled by the existing `.github/workflows/ci-cd.yml`

### Steps

1. Bump version, write CHANGELOG entries, update ROADMAP labels.
2. Sample: prepend a section showing `SnapshotEvery(50)` + composite key Translation + `RetainFor(30d)` + `AuditScope.Push("seed-data")`.
3. Bench: add `ReconstructorWithSnapshotBench` showing the depth-1000 case under `SnapshotEvery(50)` for a side-by-side comparison.
4. `dotnet build OrionAudit.sln -c Release && dotnet pack` to verify packages produce.
5. Final smoke: invoke each test exe in Release.
6. Commit, tag, push, gh release create. Workflow publishes to nuget.org and GitHub Packages.

```bash
git commit -am "release: v0.2.0 — composite keys, snapshotting, retention, soft-delete, provider hints, AuditScope"
git tag -a v0.2.0 -m "v0.2.0 — Reliability & Scale"
git push origin master v0.2.0
gh release create v0.2.0 \
  --title "v0.2.0 — Reliability & Scale" \
  --notes-file <(awk '/^## \[0\.2\.0\]/{f=1; next} /^## \[/{f=0} f' CHANGELOG.md) \
  --verify-tag --latest
```

---

## Definition of Done (mirrors spec § 9)

- All v0.1.0 tests still pass unchanged.
- ≥ 25 new unit tests + 4 integration tests covering the six features.
- Release build clean across net8/9/10.
- 3 NuGet packages updated to v0.2.0 (IDs unchanged).
- Sample extended with a "v0.2 features tour" section that prints all six in one run.
- `ReconstructorWithSnapshotBench` lands and demonstrates the O(K) win in the README perf table.
- `CHANGELOG.md` `[0.2.0]` entry shipped.
- `ROADMAP.md` v0.2 items moved into the shipped column.
- CI green on `master`.

## Self-review checklist (engineer)

- Composite key serialisation round-trips through both interceptor and `AuditKey.From`.
- `AuditScope.Push` is `AsyncLocal`-safe (test covers `await Task.Yield()` + `await Task.Run`).
- `SnapshotPolicy.EveryNthPolicy` counter never goes negative under concurrent saves on the same
  entity (cursor read uses `UPDATE`-and-return idiom or `WITH FOR UPDATE`).
- Retention sweep is idempotent: running twice in a row deletes the same rowset (none) the
  second time.
- Provider hint detection falls back gracefully on unknown providers (no exception thrown).
- No `Co-Authored-By` trailer on any commit in the entire plan.
