# OrionAudit v0.5.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship OrionAudit v0.5.0 — an opt-in async staging-capture mode that moves diff/snapshot work off the `SaveChanges` hot path without losing atomic, lossless capture, plus a self-contained `OrionAudit.Viewer` companion package.

**Architecture:** Async mode writes a cheap `OrionAudit_Capture_Queue` row inside the consumer's transaction; a background `AuditDispatcherHostedService` later computes diffs and writes the final `AuditLog` rows, deleting the queue row in the same transaction (exactly-once). The Viewer is an ASP.NET Core endpoint group (`MapOrionAuditViewer`) serving a JSON API plus an embedded static SPA, built on a new pure render core (`AuditViewRenderer`) in core OrionAudit.

**Tech Stack:** C# / .NET 8-9-10, EF Core 9, `System.Text.Json.Nodes`, xUnit, BenchmarkDotNet, ASP.NET Core minimal APIs.

**Reference spec:** `docs/superpowers/specs/2026-05-22-orionaudit-v0.5.0-design.md`

**Conventions for every task:** all builds/tests run from the repo root. Commit messages use Conventional Commits with no `Co-Authored-By` trailer (ECOSYSTEM §7). After each task's final test step, the working tree must build clean (`dotnet build OrionAudit.sln -c Debug`).

---

## Phase 1 — Async staging-capture

### Task 1: `AsyncCaptureOptions` and `UseAsyncCapture`

**Files:**
- Create: `src/Moongazing.OrionAudit/Configuration/AsyncCaptureOptions.cs`
- Modify: `src/Moongazing.OrionAudit/DependencyInjection/OrionAuditOptions.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/AsyncCaptureOptionsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Moongazing.OrionAudit.Tests/AsyncCaptureOptionsTests.cs`:

```csharp
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AsyncCaptureOptionsTests
{
    [Fact]
    public void Defaults_AreSensible()
    {
        var o = new AsyncCaptureOptions();
        Assert.Equal(TimeSpan.FromSeconds(2), o.PollInterval);
        Assert.Equal(500, o.BatchSize);
        Assert.Equal(5, o.MaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(5), o.ClaimLease);
    }

    [Fact]
    public void UseAsyncCapture_NotCalled_LeavesAsyncDisabled()
    {
        var options = new OrionAuditOptions();
        Assert.False(options.AsyncCaptureEnabled);
    }

    [Fact]
    public void UseAsyncCapture_EnablesAndAppliesBuilderOverrides()
    {
        var options = new OrionAuditOptions();
        options.UseAsyncCapture(q => q
            .PollInterval(TimeSpan.FromSeconds(10))
            .BatchSize(50)
            .MaxAttempts(3)
            .ClaimLease(TimeSpan.FromMinutes(1)));

        Assert.True(options.AsyncCaptureEnabled);
        Assert.Equal(TimeSpan.FromSeconds(10), options.AsyncCaptureOptions.PollInterval);
        Assert.Equal(50, options.AsyncCaptureOptions.BatchSize);
        Assert.Equal(3, options.AsyncCaptureOptions.MaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(1), options.AsyncCaptureOptions.ClaimLease);
    }

    [Fact]
    public void BatchSize_Rejects_NonPositive()
    {
        var b = new AsyncCaptureBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => b.BatchSize(0));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter AsyncCaptureOptionsTests`
Expected: FAIL — `AsyncCaptureOptions` / `AsyncCaptureBuilder` / `OrionAuditOptions.UseAsyncCapture` not defined.

- [ ] **Step 3: Create `AsyncCaptureOptions.cs`**

```csharp
namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// Tunables for opt-in async staging-capture. Defaults: poll every 2s, 500 rows per batch,
/// 5 dispatch attempts before dead-lettering, 5-minute claim lease before an abandoned
/// claim is reclaimable.
/// </summary>
public sealed class AsyncCaptureOptions
{
    /// <summary>How often the dispatcher polls the capture queue. Default: 2 seconds.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum queue rows claimed and processed per dispatch cycle. Default: 500.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Dispatch attempts for a single row before it is dead-lettered. Default: 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long a claim is honoured before another dispatcher may reclaim it. Default: 5 minutes.</summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Fluent builder for <see cref="AsyncCaptureOptions"/>, passed to <c>UseAsyncCapture</c>.</summary>
public sealed class AsyncCaptureBuilder
{
    internal AsyncCaptureOptions Options { get; } = new();

    /// <summary>Overrides the dispatcher poll interval. Must be positive.</summary>
    public AsyncCaptureBuilder PollInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Must be positive.");
        }
        Options.PollInterval = interval;
        return this;
    }

    /// <summary>Overrides the per-cycle batch size. Must be >= 1.</summary>
    public AsyncCaptureBuilder BatchSize(int size)
    {
        if (size < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Must be >= 1.");
        }
        Options.BatchSize = size;
        return this;
    }

    /// <summary>Overrides the dead-letter attempt cap. Must be >= 1.</summary>
    public AsyncCaptureBuilder MaxAttempts(int attempts)
    {
        if (attempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempts), attempts, "Must be >= 1.");
        }
        Options.MaxAttempts = attempts;
        return this;
    }

    /// <summary>Overrides the claim lease. Must be positive.</summary>
    public AsyncCaptureBuilder ClaimLease(TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease), lease, "Must be positive.");
        }
        Options.ClaimLease = lease;
        return this;
    }
}
```

- [ ] **Step 4: Add `UseAsyncCapture` to `OrionAuditOptions`**

In `src/Moongazing.OrionAudit/DependencyInjection/OrionAuditOptions.cs`, add these members after the `JsonContext` property (keep `using Moongazing.OrionAudit.Configuration;` — already present):

```csharp
    /// <summary>True when <see cref="UseAsyncCapture"/> has been called.</summary>
    public bool AsyncCaptureEnabled { get; private set; }

    /// <summary>The async-capture tunables. Meaningful only when <see cref="AsyncCaptureEnabled"/> is true.</summary>
    public AsyncCaptureOptions AsyncCaptureOptions { get; private set; } = new();
```

And add this method after `UseJsonContext`:

```csharp
    /// <summary>
    /// Opts into async staging-capture. The interceptor writes a lightweight
    /// <c>OrionAudit_Capture_Queue</c> row in the consumer's transaction; a background
    /// dispatcher computes the diff and writes the final <see cref="AuditLog"/> row shortly
    /// after. Capture stays atomic and lossless; audit becomes eventually consistent. When
    /// this is not called the synchronous v0.4.0 capture path is used unchanged.
    /// </summary>
    public OrionAuditOptions UseAsyncCapture(Action<AsyncCaptureBuilder>? configure = null)
    {
        var builder = new AsyncCaptureBuilder();
        configure?.Invoke(builder);
        AsyncCaptureOptions = builder.Options;
        AsyncCaptureEnabled = true;
        return this;
    }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter AsyncCaptureOptionsTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionAudit/Configuration/AsyncCaptureOptions.cs src/Moongazing.OrionAudit/DependencyInjection/OrionAuditOptions.cs tests/Moongazing.OrionAudit.Tests/AsyncCaptureOptionsTests.cs
git commit -m "feat(async): add AsyncCaptureOptions and OrionAuditOptions.UseAsyncCapture"
```

---

### Task 2: `AuditCaptureQueueEntry` entity and EF configuration

**Files:**
- Create: `src/Moongazing.OrionAudit/Core/AuditCaptureQueueEntry.cs`
- Create: `src/Moongazing.OrionAudit/Core/AuditCaptureQueueEntityTypeConfiguration.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/AuditCaptureQueueConfigurationTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Moongazing.OrionAudit.Tests/AuditCaptureQueueConfigurationTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.Tests;

public class AuditCaptureQueueConfigurationTests
{
    private sealed class QueueDb : DbContext
    {
        public QueueDb(DbContextOptions<QueueDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new AuditCaptureQueueEntityTypeConfiguration());
    }

    [Fact]
    public void Maps_To_DefaultTableName_With_LongIdentityKey()
    {
        var options = new DbContextOptionsBuilder<QueueDb>()
            .UseInMemoryDatabase("queue-cfg").Options;
        using var db = new QueueDb(options);
        var et = db.Model.FindEntityType(typeof(AuditCaptureQueueEntry))!;

        Assert.Equal("OrionAudit_Capture_Queue", et.GetTableName());
        var key = et.FindPrimaryKey()!;
        Assert.Single(key.Properties);
        Assert.Equal(nameof(AuditCaptureQueueEntry.Id), key.Properties[0].Name);
    }

    [Fact]
    public void DefaultTableName_Constant_IsStable()
        => Assert.Equal("OrionAudit_Capture_Queue", AuditCaptureQueueEntityTypeConfiguration.DefaultTableName);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter AuditCaptureQueueConfigurationTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Create `AuditCaptureQueueEntry.cs`**

```csharp
namespace Moongazing.OrionAudit;

/// <summary>
/// A pending audit capture awaiting background dispatch. Written by
/// <c>AuditSaveChangesInterceptor</c> in async mode, in the same transaction as the
/// originating entity change, then consumed by <c>AuditDispatcherHostedService</c> which
/// computes the diff, writes the final <see cref="AuditLog"/> row, and deletes this row.
/// </summary>
public sealed class AuditCaptureQueueEntry
{
    /// <summary>Auto-increment surrogate key; also the dispatch order key.</summary>
    public long Id { get; set; }

    /// <summary>Assembly-qualified name of the audited entity type.</summary>
    public string EntityType { get; set; } = default!;

    /// <summary>Serialized primary key of the audited entity (canonical <see cref="AuditKey"/> form).</summary>
    public string EntityId { get; set; } = default!;

    /// <summary>What kind of change this row records.</summary>
    public AuditAction Action { get; set; }

    /// <summary>Rule-applied before-state snapshot JSON (hash/redact/exclude already applied).</summary>
    public string BeforeJson { get; set; } = default!;

    /// <summary>Rule-applied after-state snapshot JSON (hash/redact/exclude already applied).</summary>
    public string AfterJson { get; set; } = default!;

    /// <summary>Optional user id captured at write time.</summary>
    public string? UserId { get; set; }

    /// <summary>Optional human-readable user display name.</summary>
    public string? UserDisplay { get; set; }

    /// <summary>Optional user classification.</summary>
    public string? UserType { get; set; }

    /// <summary>Optional tenant id captured at write time.</summary>
    public string? TenantId { get; set; }

    /// <summary>Optional correlation id captured at write time.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>UTC timestamp of the originating change; copied verbatim onto the final <see cref="AuditLog"/>.</summary>
    public DateTime OccurredOnUtc { get; set; }

    /// <summary>Dispatch attempts so far; drives dead-lettering.</summary>
    public int Attempts { get; set; }

    /// <summary>Null until dead-lettered, then the failure detail. A non-null value excludes the row from dispatch.</summary>
    public string? Error { get; set; }

    /// <summary>Per-dispatcher claim token; null when unclaimed.</summary>
    public string? ClaimToken { get; set; }

    /// <summary>UTC time the current claim was taken; used with the claim lease to reclaim abandoned rows.</summary>
    public DateTime? ClaimedUtc { get; set; }
}
```

- [ ] **Step 4: Create `AuditCaptureQueueEntityTypeConfiguration.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Moongazing.OrionAudit;

/// <summary>
/// EF Core fluent configuration for <see cref="AuditCaptureQueueEntry"/>. Applied automatically
/// by <c>modelBuilder.ApplyOrionAuditConfigurations()</c> — harmless when async capture is not
/// enabled, the table simply stays empty.
/// </summary>
public sealed class AuditCaptureQueueEntityTypeConfiguration : IEntityTypeConfiguration<AuditCaptureQueueEntry>
{
    /// <summary>Default table name when no override is supplied.</summary>
    public const string DefaultTableName = "OrionAudit_Capture_Queue";

    private readonly string tableName;

    /// <summary>Initializes a new configuration using <see cref="DefaultTableName"/>.</summary>
    public AuditCaptureQueueEntityTypeConfiguration() : this(DefaultTableName) { }

    /// <summary>Initializes a new configuration with a custom table name.</summary>
    public AuditCaptureQueueEntityTypeConfiguration(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        this.tableName = tableName;
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditCaptureQueueEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(tableName);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.EntityType).IsRequired().HasMaxLength(512);
        builder.Property(x => x.EntityId).IsRequired().HasMaxLength(128);
        builder.Property(x => x.Action).IsRequired();
        builder.Property(x => x.BeforeJson).IsRequired();
        builder.Property(x => x.AfterJson).IsRequired();
        builder.Property(x => x.UserId).HasMaxLength(256);
        builder.Property(x => x.UserDisplay).HasMaxLength(256);
        builder.Property(x => x.UserType).HasMaxLength(64);
        builder.Property(x => x.TenantId).HasMaxLength(128);
        builder.Property(x => x.CorrelationId).HasMaxLength(256);
        builder.Property(x => x.OccurredOnUtc).IsRequired();
        builder.Property(x => x.Attempts).IsRequired();
        builder.Property(x => x.ClaimToken).HasMaxLength(64);

        // The dispatcher's claim query filters on (Error, ClaimToken, ClaimedUtc) and orders by Id.
        builder.HasIndex(x => new { x.Error, x.ClaimToken });
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter AuditCaptureQueueConfigurationTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionAudit/Core/AuditCaptureQueueEntry.cs src/Moongazing.OrionAudit/Core/AuditCaptureQueueEntityTypeConfiguration.cs tests/Moongazing.OrionAudit.Tests/AuditCaptureQueueConfigurationTests.cs
git commit -m "feat(async): add AuditCaptureQueueEntry entity and EF configuration"
```

---

### Task 3: Map the queue table in `ApplyOrionAuditConfigurations`

**Files:**
- Modify: `src/Moongazing.OrionAudit/DependencyInjection/AuditModelBuilderExtensions.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/AuditLogConfigurationTests.cs` (add a test)

- [ ] **Step 1: Write the failing test**

Append to `tests/Moongazing.OrionAudit.Tests/AuditLogConfigurationTests.cs` a new `[Fact]` inside the existing test class (match the file's existing `using`s and DbContext pattern; if the file defines a local context that calls `ApplyOrionAuditConfigurations()`, reuse it):

```csharp
    [Fact]
    public void ApplyOrionAuditConfigurations_Maps_CaptureQueueTable()
    {
        using var db = NewContext();   // existing helper in this test class that builds a context
                                       // whose OnModelCreating calls ApplyOrionAuditConfigurations()
        var et = db.Model.FindEntityType(typeof(AuditCaptureQueueEntry));
        Assert.NotNull(et);
        Assert.Equal("OrionAudit_Capture_Queue", et!.GetTableName());
    }
```

If `AuditLogConfigurationTests` has no `NewContext()` helper, instead create a minimal context inline in the test the same way the other tests in that file do.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter AuditLogConfigurationTests`
Expected: FAIL — `FindEntityType(typeof(AuditCaptureQueueEntry))` returns null.

- [ ] **Step 3: Map the queue table**

In `AuditModelBuilderExtensions.cs`, add a `captureQueueTableName` parameter and apply the configuration. Replace the whole method body:

```csharp
    /// <summary>
    /// Applies the OrionAudit entity-type configurations to the model. Call from
    /// <c>DbContext.OnModelCreating</c>. Always maps <see cref="AuditLog"/>, the
    /// <see cref="SnapshotCursor"/> companion table, and the
    /// <see cref="AuditCaptureQueueEntry"/> companion table (the latter two are harmless when
    /// their feature is not configured — they simply stay empty).
    /// </summary>
    /// <param name="modelBuilder">The EF Core model builder.</param>
    /// <param name="auditLogTableName">Override the default <c>OrionAudit_Log</c> table name.</param>
    /// <param name="snapshotCursorTableName">Override the default <c>OrionAudit_Snapshot_Cursors</c> table name.</param>
    /// <param name="columnHints">Provider-specific column-type hints for <c>Diff</c> and <c>Snapshot</c> (default: <see cref="OrionAuditColumnHints.Auto"/>).</param>
    /// <param name="captureQueueTableName">Override the default <c>OrionAudit_Capture_Queue</c> table name.</param>
    public static ModelBuilder ApplyOrionAuditConfigurations(
        this ModelBuilder modelBuilder,
        string? auditLogTableName = null,
        string? snapshotCursorTableName = null,
        OrionAuditColumnHints columnHints = OrionAuditColumnHints.Auto,
        string? captureQueueTableName = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var auditLog = new AuditLogEntityTypeConfiguration(
            auditLogTableName ?? AuditLogEntityTypeConfiguration.DefaultTableName,
            columnHints);
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

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter AuditLogConfigurationTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionAudit/DependencyInjection/AuditModelBuilderExtensions.cs tests/Moongazing.OrionAudit.Tests/AuditLogConfigurationTests.cs
git commit -m "feat(async): map OrionAudit_Capture_Queue in ApplyOrionAuditConfigurations"
```

---

### Task 4: Extract `SnapshotPolicyEvaluator.ShouldSnapshot`

This refactor moves the `ShouldSnapshot` logic out of `AuditSaveChangesInterceptor` (currently a private static there) so the dispatcher can reuse it. No behaviour change; the interceptor stays green.

**Files:**
- Create: `src/Moongazing.OrionAudit/Capture/SnapshotPolicyEvaluator.cs`
- Modify: `src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs`

- [ ] **Step 1: Create `SnapshotPolicyEvaluator.cs`**

Move the existing `ShouldSnapshot` body verbatim into a new internal static class. The current method signature is `ShouldSnapshot(DbContext ctx, SnapshotPolicy policy, AuditLog row, DateTime occurredOn)`; keep it identical so the move is mechanical:

```csharp
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// Evaluates the periodic <see cref="SnapshotPolicy"/> against the <see cref="SnapshotCursor"/>
/// companion table. Shared by the synchronous interceptor and the async dispatcher so both
/// reach the same snapshot decision.
/// </summary>
internal static class SnapshotPolicyEvaluator
{
    /// <summary>
    /// Returns true when the supplied audit row should also carry a full snapshot, advancing
    /// (and lazily creating) the entity's <see cref="SnapshotCursor"/>. Must be called inside
    /// the same transaction that writes the resulting rows.
    /// </summary>
    public static bool ShouldSnapshot(DbContext ctx, SnapshotPolicy policy, AuditLog row, DateTime occurredOn)
    {
        var cursor = ctx.Set<SnapshotCursor>().Find(row.EntityType, row.EntityId, row.TenantId ?? string.Empty);
        if (cursor is null)
        {
            cursor = new SnapshotCursor
            {
                EntityType = row.EntityType,
                EntityId = row.EntityId,
                TenantId = row.TenantId ?? string.Empty,
                UpdatesSinceLast = 0,
                LastSnapshotUtc = null,
            };
            ctx.Add(cursor);
        }

        cursor.UpdatesSinceLast++;
        var shouldSnapshot = policy switch
        {
            SnapshotPolicy.EveryNthPolicy n => cursor.UpdatesSinceLast >= n.Updates,
            SnapshotPolicy.EveryDurationPolicy d =>
                cursor.LastSnapshotUtc is null
                || (occurredOn - cursor.LastSnapshotUtc.Value) >= d.Elapsed,
            _ => false,
        };

        if (shouldSnapshot)
        {
            cursor.UpdatesSinceLast = 0;
            cursor.LastSnapshotUtc = occurredOn;
        }
        return shouldSnapshot;
    }
}
```

- [ ] **Step 2: Update the interceptor to call the shared evaluator**

In `AuditSaveChangesInterceptor.cs`, delete the private `static bool ShouldSnapshot(...)` method (lines defining it) and change its one call site from `ShouldSnapshot(ctx, snapshotPolicy, auditLog, occurredOn)` to `SnapshotPolicyEvaluator.ShouldSnapshot(ctx, snapshotPolicy, auditLog, occurredOn)`. The `using Moongazing.OrionAudit.Capture;` is unnecessary — the interceptor is already in that namespace.

- [ ] **Step 3: Run the existing snapshot-policy tests to verify no regression**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter SnapshotPolicy`
Expected: PASS — all existing `SnapshotPolicyCaptureTests` / `SnapshotPolicyReplayTests` / `SnapshotPolicyTypeTests` still pass.

- [ ] **Step 4: Commit**

```bash
git add src/Moongazing.OrionAudit/Capture/SnapshotPolicyEvaluator.cs src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs
git commit -m "refactor(capture): extract SnapshotPolicyEvaluator for dispatcher reuse"
```

---

### Task 5: Interceptor async-mode branch — write queue entries

In async mode the interceptor builds the same rule-applied before/after snapshot nodes but writes an `AuditCaptureQueueEntry` (no diff, no `AuditLog`).

**Files:**
- Modify: `src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs`
- Test: `tests/Moongazing.OrionAudit.IntegrationTests/AsyncCaptureInterceptorTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Moongazing.OrionAudit.IntegrationTests/AsyncCaptureInterceptorTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.IntegrationTests;

public class AsyncCaptureInterceptorTests
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
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private static async Task<(ServiceProvider sp, SqliteConnection conn)> BuildAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<AsyncDb>(o => o.Audit<Note>().UseAsyncCapture());
        services.AddSingleton(connection);
        services.AddDbContext<AsyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AsyncDb>().Database.EnsureCreatedAsync();
        }
        return (sp, connection);
    }

    [Fact]
    public async Task AsyncMode_WritesQueueRow_NotAuditLog()
    {
        var (sp, conn) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            ctx.Notes.Add(new Note { Body = "hello" });
            await ctx.SaveChangesAsync();
        }

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<AsyncDb>();
        Assert.Equal(0, await vctx.AuditLogs.CountAsync());
        var queued = await vctx.Queue.SingleAsync();
        Assert.Equal(AuditAction.Inserted, queued.Action);
        Assert.Equal(typeof(Note).AssemblyQualifiedName, queued.EntityType);
        Assert.Contains("hello", queued.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsyncMode_QueueRow_RolledBackWithTheDataChange()
    {
        var (sp, conn) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            await using var tx = await ctx.Database.BeginTransactionAsync();
            ctx.Notes.Add(new Note { Body = "doomed" });
            await ctx.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<AsyncDb>();
        Assert.Equal(0, await vctx.Queue.CountAsync());
        Assert.Equal(0, await vctx.Notes.CountAsync());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Moongazing.OrionAudit.IntegrationTests -c Debug --filter AsyncCaptureInterceptorTests`
Expected: FAIL — `AsyncMode_WritesQueueRow_NotAuditLog` fails because the interceptor still writes an `AuditLog`.

- [ ] **Step 3: Add the async branch to the interceptor**

In `AuditSaveChangesInterceptor.cs`, inside `SavingChangesAsync`, after `auditedEntries` is computed and the empty check, detect async mode and branch. Add near the top of the method (after `var clock = ...`):

```csharp
        var asyncCapture = serviceProvider.GetService<Moongazing.OrionAudit.Configuration.AsyncCaptureOptions>();
```

Then, after the `auditedEntries.Count == 0` early return and after `correlationId` / `occurredOn` are computed, branch before the `foreach (var entry in auditedEntries)` loop:

```csharp
        if (asyncCapture is not null)
        {
            foreach (var entry in auditedEntries)
            {
                ctx.Add(BuildQueueEntry(entry, configuration, user, tenantId, correlationId, occurredOn, jsonContext));
            }
            OrionAuditTelemetry.EntriesWritten.Add(auditedEntries.Count);
            OrionAuditTelemetry.CaptureDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
        }
```

Add the `BuildQueueEntry` static method next to `BuildAuditLog`. It reuses the existing private statics `SnapshotValues`, `ExtractPrimaryKey`, and the same action/soft-delete logic as `BuildAuditLog`:

```csharp
    private static AuditCaptureQueueEntry BuildQueueEntry(
        EntityEntry entry,
        IAuditConfiguration configuration,
        AuditUser? user,
        string? tenantId,
        string? correlationId,
        DateTime occurredOn,
        JsonSerializerContext? jsonContext)
    {
        var entityType = entry.Entity.GetType();
        var typeConfig = configuration.GetConfig(entityType);

        var action = entry.State switch
        {
            EntityState.Added => AuditAction.Inserted,
            EntityState.Modified => AuditAction.Updated,
            EntityState.Deleted => AuditAction.Deleted,
            _ => throw new InvalidOperationException($"Unsupported entry state {entry.State}.")
        };
        if (action == AuditAction.Updated && typeConfig?.SoftDeleteProperty is { } softDeleteProp)
        {
            var property = entry.Properties.FirstOrDefault(p => p.Metadata.Name == softDeleteProp);
            if (property is not null && property.OriginalValue is false && property.CurrentValue is true)
            {
                action = AuditAction.SoftDeleted;
            }
        }

        var beforeValues = entry.State == EntityState.Added
            ? new Dictionary<string, object?>()
            : SnapshotValues(entry, useOriginal: true);
        var afterValues = entry.State == EntityState.Deleted
            ? new Dictionary<string, object?>()
            : SnapshotValues(entry, useOriginal: false);

        JsonObject beforeNode = jsonContext is not null
            ? SnapshotBuilder.Build(entityType, beforeValues, configuration, jsonContext)
            : SnapshotBuilder.Build(entityType, beforeValues, configuration);
        JsonObject afterNode = jsonContext is not null
            ? SnapshotBuilder.Build(entityType, afterValues, configuration, jsonContext)
            : SnapshotBuilder.Build(entityType, afterValues, configuration);

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
        };
    }
```

Note: `BuildQueueEntry` applies the same `SnapshotBuilder` rules (hash/redact/exclude) as the sync path, so no raw sensitive value reaches the queue table. The `SnapshotBuilder` may throw for an unregistered type under `UseJsonContext`; in async mode that exception propagates and rolls back the consumer's `SaveChanges` — consistent with the sync path's `OrionAuditException` contract.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Moongazing.OrionAudit.IntegrationTests -c Debug --filter AsyncCaptureInterceptorTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the full sync-path interceptor suite to confirm no regression**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter AuditSaveChangesInterceptorTests`
Expected: PASS — sync mode unaffected (`asyncCapture` is null when `UseAsyncCapture` is not called).

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionAudit/Capture/AuditSaveChangesInterceptor.cs tests/Moongazing.OrionAudit.IntegrationTests/AsyncCaptureInterceptorTests.cs
git commit -m "feat(async): interceptor writes capture-queue rows in async mode"
```

---

### Task 6: `IAuditDispatcher` and the `AuditDispatcher` worker — happy path

The worker claims queue rows, computes diffs, writes `AuditLog` rows, and deletes the claimed queue rows in one transaction (exactly-once).

**Files:**
- Create: `src/Moongazing.OrionAudit/Capture/IAuditDispatcher.cs`
- Create: `src/Moongazing.OrionAudit/Capture/AuditDispatcher.cs`
- Test: `tests/Moongazing.OrionAudit.IntegrationTests/AuditDispatcherTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Moongazing.OrionAudit.IntegrationTests/AuditDispatcherTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.IntegrationTests;

public class AuditDispatcherTests
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
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private static async Task<(ServiceProvider sp, SqliteConnection conn)> BuildAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<AsyncDb>(o => o.Audit<Note>().UseAsyncCapture());
        services.AddSingleton(connection);
        services.AddDbContext<AsyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AsyncDb>().Database.EnsureCreatedAsync();
        }
        return (sp, connection);
    }

    [Fact]
    public async Task DispatchOnce_TurnsQueueRowsIntoAuditLogRows()
    {
        var (sp, conn) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        Note? note = null;
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            note = new Note { Body = "v1" };
            ctx.Notes.Add(note);
            await ctx.SaveChangesAsync();
        }
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            var fresh = await ctx.Notes.FirstAsync();
            fresh.Body = "v2";
            await ctx.SaveChangesAsync();
        }

        var dispatcher = (IAuditDispatcher)sp.GetRequiredService<IAuditDispatcher>();
        var processed = await dispatcher.FlushPendingAsync();
        Assert.Equal(2, processed);

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<AsyncDb>();
        Assert.Equal(0, await vctx.Queue.CountAsync());
        var logs = await vctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.Equal(AuditAction.Inserted, logs[0].Action);
        Assert.Equal(AuditAction.Updated, logs[1].Action);
        Assert.NotEqual("[]", logs[1].Diff);   // the update produced a real diff
    }

    [Fact]
    public async Task GetQueueDepth_CountsUndispatchedRows()
    {
        var (sp, conn) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            ctx.Notes.Add(new Note { Body = "a" });
            ctx.Notes.Add(new Note { Body = "b" });
            await ctx.SaveChangesAsync();
        }

        var dispatcher = sp.GetRequiredService<IAuditDispatcher>();
        Assert.Equal(2, await dispatcher.GetQueueDepthAsync());
        await dispatcher.FlushPendingAsync();
        Assert.Equal(0, await dispatcher.GetQueueDepthAsync());
    }
}
```

Note: `FlushPendingAsync` in this test must return the processed-row count. Define it as `Task<int> FlushPendingAsync(...)`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Moongazing.OrionAudit.IntegrationTests -c Debug --filter AuditDispatcherTests`
Expected: FAIL — `IAuditDispatcher` / `AuditDispatcher` not defined; `GetRequiredService<IAuditDispatcher>()` throws.

- [ ] **Step 3: Create `IAuditDispatcher.cs`**

```csharp
namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// Drains the async capture queue into <see cref="AuditLog"/> rows. Registered only when
/// <c>UseAsyncCapture</c> is configured; in synchronous mode a no-op implementation is
/// registered so call sites can depend on it unconditionally.
/// </summary>
public interface IAuditDispatcher
{
    /// <summary>
    /// Synchronously drains the capture queue to completion (repeatedly dispatching batches
    /// until the queue holds no further dispatchable rows). Returns the number of rows turned
    /// into <see cref="AuditLog"/> rows. A no-op returning 0 in synchronous mode.
    /// </summary>
    Task<int> FlushPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts capture-queue rows still awaiting dispatch (excludes dead-lettered rows).</summary>
    Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Create `AuditDispatcher.cs`**

```csharp
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// The async-capture worker. Claims a batch of <see cref="AuditCaptureQueueEntry"/> rows,
/// computes each row's diff, writes the resulting <see cref="AuditLog"/> rows, and deletes the
/// claimed queue rows — the inserts and deletes commit in one transaction so dispatch is
/// exactly-once. Used by <see cref="AuditDispatcherHostedService{TDbContext}"/> and exposed as
/// <see cref="IAuditDispatcher"/>.
/// </summary>
public sealed partial class AuditDispatcher<TDbContext> : IAuditDispatcher
    where TDbContext : DbContext
{
    [LoggerMessage(EventId = 10, Level = LogLevel.Error,
        Message = "OrionAudit dispatch failed for queue row {QueueRowId} (attempt {Attempt}).")]
    private partial void LogRowFailed(long queueRowId, int attempt, Exception ex);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error,
        Message = "OrionAudit queue row {QueueRowId} dead-lettered after {Attempts} attempts.")]
    private partial void LogRowDeadLettered(long queueRowId, int attempts);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly AsyncCaptureOptions options;
    private readonly SnapshotPolicy snapshotPolicy;
    private readonly TimeProvider clock;
    private readonly ILogger<AuditDispatcher<TDbContext>> logger;

    /// <summary>Initializes a new dispatcher.</summary>
    public AuditDispatcher(
        IServiceScopeFactory scopeFactory,
        AsyncCaptureOptions options,
        SnapshotPolicy snapshotPolicy,
        TimeProvider clock,
        ILogger<AuditDispatcher<TDbContext>> logger)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.snapshotPolicy = snapshotPolicy ?? throw new ArgumentNullException(nameof(snapshotPolicy));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<int> FlushPendingAsync(CancellationToken cancellationToken = default)
    {
        var total = 0;
        int processed;
        do
        {
            processed = await DispatchOnceAsync(cancellationToken).ConfigureAwait(false);
            total += processed;
        }
        while (processed > 0);
        return total;
    }

    /// <inheritdoc />
    public async Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return await ctx.Set<AuditCaptureQueueEntry>()
            .CountAsync(q => q.Error == null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Claims and processes a single batch. Returns the number of queue rows successfully
    /// turned into <see cref="AuditLog"/> rows in this cycle (0 when the queue is empty).
    /// </summary>
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        using var activity = OrionAuditTelemetry.ActivitySource.StartActivity(
            "OrionAudit.Dispatch", ActivityKind.Internal);
        var sw = Stopwatch.StartNew();

        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TDbContext>();

        var claimToken = Guid.NewGuid().ToString("N");
        var staleBefore = clock.GetUtcNow().UtcDateTime - options.ClaimLease;

        // Atomic claim: a single UPDATE over the next BatchSize dispatchable rows.
        await ctx.Set<AuditCaptureQueueEntry>()
            .Where(q => q.Error == null && (q.ClaimToken == null || q.ClaimedUtc < staleBefore))
            .OrderBy(q => q.Id)
            .Take(options.BatchSize)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.ClaimToken, claimToken)
                .SetProperty(q => q.ClaimedUtc, clock.GetUtcNow().UtcDateTime), cancellationToken)
            .ConfigureAwait(false);

        var claimed = await ctx.Set<AuditCaptureQueueEntry>()
            .Where(q => q.ClaimToken == claimToken)
            .OrderBy(q => q.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (claimed.Count == 0)
        {
            return 0;
        }

        var processed = 0;
        var deadLettered = 0;
        foreach (var row in claimed)
        {
            try
            {
                var auditLog = BuildAuditLog(ctx, row);
                ctx.Add(auditLog);
                ctx.Set<AuditCaptureQueueEntry>().Remove(row);
                processed++;
            }
#pragma warning disable CA1031 // a single bad row must not abort the batch
            catch (Exception ex)
#pragma warning restore CA1031
            {
                row.Attempts++;
                row.ClaimToken = null;
                row.ClaimedUtc = null;
                LogRowFailed(row.Id, row.Attempts, ex);
                if (row.Attempts >= options.MaxAttempts)
                {
                    row.Error = ex.ToString();
                    deadLettered++;
                    LogRowDeadLettered(row.Id, row.Attempts);
                }
            }
        }

        // Inserts (AuditLog) + deletes (queue rows) + failure updates commit together.
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        OrionAuditTelemetry.DispatchRowsProcessed.Add(processed);
        OrionAuditTelemetry.DispatchRowsDeadLettered.Add(deadLettered);
        OrionAuditTelemetry.DispatchBatchDuration.Record(sw.Elapsed.TotalMilliseconds);
        activity?.SetTag("orionaudit.dispatch.rows_processed", processed);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return processed;
    }

    private AuditLog BuildAuditLog(TDbContext ctx, AuditCaptureQueueEntry row)
    {
        var before = JsonNode.Parse(row.BeforeJson)!.AsObject();
        var after = JsonNode.Parse(row.AfterJson)!.AsObject();

        var auditLog = new AuditLog
        {
            EntityType = row.EntityType,
            EntityId = row.EntityId,
            Action = row.Action,
            OccurredOnUtc = row.OccurredOnUtc,
            UserId = row.UserId,
            UserDisplay = row.UserDisplay,
            UserType = row.UserType,
            TenantId = row.TenantId,
            CorrelationId = row.CorrelationId,
            Diff = DiffEngine.Compute(before, after),
        };

        if (row.Action is AuditAction.Deleted)
        {
            auditLog.Snapshot = row.BeforeJson;
        }
        else if (row.Action is AuditAction.SoftDeleted)
        {
            auditLog.Snapshot = row.AfterJson;
        }
        else if (row.Action == AuditAction.Updated
                 && snapshotPolicy is not SnapshotPolicy.NeverPolicy
                 && SnapshotPolicyEvaluator.ShouldSnapshot(ctx, snapshotPolicy, auditLog, row.OccurredOnUtc))
        {
            auditLog.Snapshot = row.AfterJson;
        }

        return auditLog;
    }
}
```

Note on exactly-once: the claim is its own committed `ExecuteUpdateAsync`. The process step's `SaveChangesAsync` wraps the `AuditLog` inserts and queue-row deletes in one transaction. A crash before that `SaveChangesAsync` rolls it back; the claimed rows keep their (now stale) `ClaimToken` and are reclaimed after `ClaimLease`. No `AuditLog` row is ever written twice.

- [ ] **Step 5: Add telemetry instrument placeholders**

This task references `OrionAuditTelemetry.DispatchRowsProcessed`, `DispatchRowsDeadLettered`, and `DispatchBatchDuration`, which are created in Task 10. To keep this task self-contained and compilable now, add those three instruments to `OrionAuditTelemetry.cs` as part of this step (Task 10 then only adds the queue-depth gauge and the version bump):

In `src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs`, after `RetentionSweepDuration`:

```csharp
    internal static readonly Counter<long> DispatchRowsProcessed = Meter.CreateCounter<long>(
        "orionaudit.dispatch.rows_processed", unit: "rows", description: "Capture-queue rows turned into audit rows by the dispatcher.");

    internal static readonly Counter<long> DispatchRowsDeadLettered = Meter.CreateCounter<long>(
        "orionaudit.dispatch.rows_deadlettered", unit: "rows", description: "Capture-queue rows dead-lettered after exhausting dispatch attempts.");

    internal static readonly Histogram<double> DispatchBatchDuration = Meter.CreateHistogram<double>(
        "orionaudit.dispatch.batch.duration", unit: "ms", description: "Dispatcher batch duration per cycle.");
```

- [ ] **Step 6: Register the dispatcher in DI (minimal, to make the test resolve it)**

In `AuditServiceCollectionExtensions.AddOrionAudit`, after the retention `AddHostedService` block, add (full wiring including the no-op and the hosted service lands in Task 9 — for now register just enough for the test):

```csharp
        if (options.AsyncCaptureEnabled)
        {
            services.TryAddSingleton(options.AsyncCaptureOptions);
            services.TryAddSingleton<Capture.IAuditDispatcher, Capture.AuditDispatcher<TDbContext>>();
        }
```

Add `using Moongazing.OrionAudit.Capture;` is not needed if you fully-qualify as above; either is fine.

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test tests/Moongazing.OrionAudit.IntegrationTests -c Debug --filter AuditDispatcherTests`
Expected: PASS (2 tests).

- [ ] **Step 8: Commit**

```bash
git add src/Moongazing.OrionAudit/Capture/IAuditDispatcher.cs src/Moongazing.OrionAudit/Capture/AuditDispatcher.cs src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs src/Moongazing.OrionAudit/DependencyInjection/AuditServiceCollectionExtensions.cs tests/Moongazing.OrionAudit.IntegrationTests/AuditDispatcherTests.cs
git commit -m "feat(async): add AuditDispatcher worker with exactly-once batch dispatch"
```

---

### Task 7: Dispatcher dead-lettering and diff parity

Verify that a malformed queue row is dead-lettered after `MaxAttempts` and that dispatched `AuditLog` rows are byte-for-byte identical to what the synchronous path produces.

**Files:**
- Test: `tests/Moongazing.OrionAudit.IntegrationTests/AuditDispatcherDeadLetterTests.cs`
- (No production change expected — this task validates Task 6. If a test fails, fix `AuditDispatcher`.)

- [ ] **Step 1: Write the failing test**

Create `tests/Moongazing.OrionAudit.IntegrationTests/AuditDispatcherDeadLetterTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.IntegrationTests;

public class AuditDispatcherDeadLetterTests
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
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private static async Task<(ServiceProvider sp, SqliteConnection conn)> BuildAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<AsyncDb>(o => o.Audit<Note>().UseAsyncCapture(q => q.MaxAttempts(2)));
        services.AddSingleton(connection);
        services.AddDbContext<AsyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AsyncDb>().Database.EnsureCreatedAsync();
        }
        return (sp, connection);
    }

    [Fact]
    public async Task MalformedQueueRow_IsDeadLettered_AfterMaxAttempts()
    {
        var (sp, conn) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        // Insert a deliberately malformed queue row directly.
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            ctx.Queue.Add(new AuditCaptureQueueEntry
            {
                EntityType = typeof(Note).AssemblyQualifiedName!,
                EntityId = Guid.NewGuid().ToString(),
                Action = AuditAction.Inserted,
                BeforeJson = "{}",
                AfterJson = "this-is-not-json",   // forces JsonNode.Parse to throw
                OccurredOnUtc = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var dispatcher = (AuditDispatcher<AsyncDb>)sp.GetRequiredService<IAuditDispatcher>();

        // MaxAttempts = 2 → two failing cycles dead-letter the row.
        await dispatcher.DispatchOnceAsync();
        await dispatcher.DispatchOnceAsync();

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<AsyncDb>();
        var row = await vctx.Queue.SingleAsync();
        Assert.Equal(2, row.Attempts);
        Assert.NotNull(row.Error);
        Assert.Equal(0, await vctx.AuditLogs.CountAsync());
        Assert.Equal(0, await dispatcher.GetQueueDepthAsync());   // dead-lettered rows excluded
    }
}
```

- [ ] **Step 2: Run test to verify it fails or passes**

Run: `dotnet test tests/Moongazing.OrionAudit.IntegrationTests -c Debug --filter AuditDispatcherDeadLetterTests`
Expected: PASS if Task 6's dead-letter logic is correct. If it FAILS, fix `AuditDispatcher.DispatchOnceAsync` until it passes (the per-row `try/catch`, `Attempts++`, and `Error` assignment are the suspects).

- [ ] **Step 3: Write the diff-parity test**

Append to the same file a second `[Fact]` that proves async dispatch produces the same `Diff` as the sync path. Reuse the `AsyncDb` context; build a *second* service provider over a *separate* connection in synchronous mode (no `UseAsyncCapture`), apply the identical Insert+Update, and compare:

```csharp
    [Fact]
    public async Task AsyncDispatch_ProducesSameDiff_AsSyncCapture()
    {
        // --- async provider ---
        var (asyncSp, asyncConn) = await BuildAsync();
        await using var _ac = asyncConn;
        await using var _as = asyncSp;
        var fixedId = Guid.NewGuid();
        await ApplyInsertThenUpdate(asyncSp, fixedId);
        await ((IAuditDispatcher)asyncSp.GetRequiredService<IAuditDispatcher>()).FlushPendingAsync();

        // --- sync provider (no UseAsyncCapture) ---
        var syncConn = new SqliteConnection("DataSource=:memory:");
        await syncConn.OpenAsync();
        await using var _sc = syncConn;
        var syncServices = new ServiceCollection();
        syncServices.AddLogging();
        syncServices.AddOrionAudit<AsyncDb>(o => o.Audit<Note>());
        syncServices.AddSingleton(syncConn);
        syncServices.AddDbContext<AsyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var syncSp = syncServices.BuildServiceProvider();
        await using (var scope = syncSp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AsyncDb>().Database.EnsureCreatedAsync();
        }
        await ApplyInsertThenUpdate(syncSp, fixedId);

        // --- compare the Update row's diff ---
        var asyncDiff = await ReadUpdateDiff(asyncSp);
        var syncDiff = await ReadUpdateDiff(syncSp);
        Assert.Equal(syncDiff, asyncDiff);
    }

    private static async Task ApplyInsertThenUpdate(IServiceProvider sp, Guid id)
    {
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            ctx.Notes.Add(new Note { Id = id, Body = "v1" });
            await ctx.SaveChangesAsync();
        }
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            var fresh = await ctx.Notes.FirstAsync();
            fresh.Body = "v2";
            await ctx.SaveChangesAsync();
        }
    }

    private static async Task<string> ReadUpdateDiff(IServiceProvider sp)
    {
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
        var row = await ctx.AuditLogs.SingleAsync(a => a.Action == AuditAction.Updated);
        return row.Diff;
    }
```

- [ ] **Step 4: Run the parity test**

Run: `dotnet test tests/Moongazing.OrionAudit.IntegrationTests -c Debug --filter AuditDispatcherDeadLetterTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add tests/Moongazing.OrionAudit.IntegrationTests/AuditDispatcherDeadLetterTests.cs
git commit -m "test(async): cover dispatcher dead-lettering and sync/async diff parity"
```

---

### Task 8: `AuditDispatcherHostedService` and the no-op dispatcher

**Files:**
- Create: `src/Moongazing.OrionAudit/Capture/AuditDispatcherHostedService.cs`
- Create: `src/Moongazing.OrionAudit/Capture/NoOpAuditDispatcher.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/NoOpAuditDispatcherTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Moongazing.OrionAudit.Tests/NoOpAuditDispatcherTests.cs`:

```csharp
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Tests;

public class NoOpAuditDispatcherTests
{
    [Fact]
    public async Task NoOp_FlushPending_Returns0()
        => Assert.Equal(0, await new NoOpAuditDispatcher().FlushPendingAsync());

    [Fact]
    public async Task NoOp_GetQueueDepth_Returns0()
        => Assert.Equal(0, await new NoOpAuditDispatcher().GetQueueDepthAsync());
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter NoOpAuditDispatcherTests`
Expected: FAIL — `NoOpAuditDispatcher` not defined.

- [ ] **Step 3: Create `NoOpAuditDispatcher.cs`**

```csharp
namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// The <see cref="IAuditDispatcher"/> registered in synchronous mode. Both members are no-ops
/// so call sites — chiefly test code — can depend on <see cref="IAuditDispatcher"/> without
/// branching on whether async capture is enabled.
/// </summary>
public sealed class NoOpAuditDispatcher : IAuditDispatcher
{
    /// <inheritdoc />
    public Task<int> FlushPendingAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    /// <inheritdoc />
    public Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}
```

- [ ] **Step 4: Create `AuditDispatcherHostedService.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// Background service that periodically drains the async capture queue via
/// <see cref="AuditDispatcher{TDbContext}"/>. Registered automatically by <c>AddOrionAudit</c>
/// when <c>UseAsyncCapture</c> is configured.
/// </summary>
public sealed partial class AuditDispatcherHostedService<TDbContext> : BackgroundService
    where TDbContext : DbContext
{
    [LoggerMessage(EventId = 12, Level = LogLevel.Error,
        Message = "OrionAudit dispatch cycle failed; will retry on the next interval.")]
    private partial void LogCycleFailed(Exception ex);

    private readonly AuditDispatcher<TDbContext> dispatcher;
    private readonly AsyncCaptureOptions options;
    private readonly TimeProvider clock;
    private readonly ILogger<AuditDispatcherHostedService<TDbContext>> logger;

    /// <summary>Initializes a new dispatcher hosted service.</summary>
    public AuditDispatcherHostedService(
        AuditDispatcher<TDbContext> dispatcher,
        AsyncCaptureOptions options,
        TimeProvider clock,
        ILogger<AuditDispatcherHostedService<TDbContext>> logger)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval, clock);
        do
        {
            try
            {
                // Drain fully each tick so a burst does not accumulate across intervals.
                int processed;
                do
                {
                    processed = await dispatcher.DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                while (processed > 0 && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // background loop swallows unexpected failures to keep ticking
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogCycleFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter NoOpAuditDispatcherTests`
Expected: PASS (2 tests). Confirm the solution still builds: `dotnet build OrionAudit.sln -c Debug`.

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionAudit/Capture/NoOpAuditDispatcher.cs src/Moongazing.OrionAudit/Capture/AuditDispatcherHostedService.cs tests/Moongazing.OrionAudit.Tests/NoOpAuditDispatcherTests.cs
git commit -m "feat(async): add dispatcher hosted service and no-op sync dispatcher"
```

---

### Task 9: Full DI wiring for async capture

**Files:**
- Modify: `src/Moongazing.OrionAudit/DependencyInjection/AuditServiceCollectionExtensions.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/AsyncCaptureWiringTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Moongazing.OrionAudit.Tests/AsyncCaptureWiringTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AsyncCaptureWiringTests
{
    private sealed class WiringDb : DbContext
    {
        public WiringDb(DbContextOptions<WiringDb> options) : base(options) { }
    }

    [Fact]
    public void SyncMode_Registers_NoOpDispatcher_And_NoHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<WiringDb>(o => { });
        using var sp = services.BuildServiceProvider();

        Assert.IsType<NoOpAuditDispatcher>(sp.GetRequiredService<IAuditDispatcher>());
        Assert.Empty(sp.GetServices<IHostedService>()
            .Where(h => h is AuditDispatcherHostedService<WiringDb>));
        Assert.Null(sp.GetService<AsyncCaptureOptions>());
    }

    [Fact]
    public void AsyncMode_Registers_RealDispatcher_AndHostedService_AndOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<WiringDb>(o => o.UseAsyncCapture(q => q.BatchSize(7)));
        using var sp = services.BuildServiceProvider();

        Assert.IsType<AuditDispatcher<WiringDb>>(sp.GetRequiredService<IAuditDispatcher>());
        Assert.Contains(sp.GetServices<IHostedService>(),
            h => h is AuditDispatcherHostedService<WiringDb>);
        Assert.Equal(7, sp.GetRequiredService<AsyncCaptureOptions>().BatchSize);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter AsyncCaptureWiringTests`
Expected: FAIL — `SyncMode_*` fails (`IAuditDispatcher` not registered in sync mode); `AsyncMode_*` fails (hosted service not registered).

- [ ] **Step 3: Replace the partial async wiring from Task 6 with full wiring**

In `AuditServiceCollectionExtensions.cs`, ensure `using Moongazing.OrionAudit.Capture;` is present. Replace the partial block added in Task 6 Step 6 with:

```csharp
        if (options.AsyncCaptureEnabled)
        {
            services.TryAddSingleton(options.AsyncCaptureOptions);
            services.TryAddSingleton<AuditDispatcher<TDbContext>>();
            services.TryAddSingleton<IAuditDispatcher>(sp => sp.GetRequiredService<AuditDispatcher<TDbContext>>());
            services.AddHostedService<AuditDispatcherHostedService<TDbContext>>();
        }
        else
        {
            services.TryAddSingleton<IAuditDispatcher, NoOpAuditDispatcher>();
        }
```

`AuditDispatcher<TDbContext>` is registered as a concrete singleton so both the `IAuditDispatcher` resolution and the hosted service share one instance.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter AsyncCaptureWiringTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the full async integration suite**

Run: `dotnet test tests/Moongazing.OrionAudit.IntegrationTests -c Debug --filter "AsyncCaptureInterceptorTests|AuditDispatcherTests|AuditDispatcherDeadLetterTests"`
Expected: PASS — all async tests still green with the final wiring.

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionAudit/DependencyInjection/AuditServiceCollectionExtensions.cs tests/Moongazing.OrionAudit.Tests/AsyncCaptureWiringTests.cs
git commit -m "feat(async): wire dispatcher, hosted service, and no-op fallback into AddOrionAudit"
```

---

### Task 10: Queue-depth telemetry gauge

`DispatchRowsProcessed` / `DispatchRowsDeadLettered` / `DispatchBatchDuration` were added in Task 6. This task adds the observable queue-depth gauge.

**Files:**
- Modify: `src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs`
- Modify: `src/Moongazing.OrionAudit/Capture/AuditDispatcher.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/OrionAuditTelemetryTests.cs` (add a test)

- [ ] **Step 1: Write the failing test**

Append to `tests/Moongazing.OrionAudit.Tests/OrionAuditTelemetryTests.cs` (match its existing `using`s / class):

```csharp
    [Fact]
    public void Meter_Exposes_DispatchInstruments()
    {
        var names = new List<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OrionAuditTelemetry.MeterName)
            {
                names.Add(instrument.Name);
            }
        };
        listener.Start();

        Assert.Contains("orionaudit.dispatch.rows_processed", names);
        Assert.Contains("orionaudit.dispatch.rows_deadlettered", names);
        Assert.Contains("orionaudit.dispatch.batch.duration", names);
    }
```

- [ ] **Step 2: Run test to verify it fails or passes**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter OrionAuditTelemetryTests`
Expected: PASS for the three Task-6 instruments. If it FAILS, the instruments must be referenced somewhere so the static constructor runs — they are referenced by `AuditDispatcher`, which this test's assembly already loads; if still failing, add a `_ = OrionAuditTelemetry.DispatchRowsProcessed;` touch in the test setup.

- [ ] **Step 3: Add the queue-depth observable gauge**

In `OrionAuditTelemetry.cs`, after `DispatchBatchDuration`, add a settable backing field plus the gauge:

```csharp
    private static long dispatchQueueDepth;

    /// <summary>Last observed capture-queue depth; updated by the dispatcher each cycle.</summary>
    internal static void SetQueueDepth(long depth) => Interlocked.Exchange(ref dispatchQueueDepth, depth);

    internal static readonly ObservableGauge<long> DispatchQueueDepth = Meter.CreateObservableGauge<long>(
        "orionaudit.capture.queue_depth",
        () => Interlocked.Read(ref dispatchQueueDepth),
        unit: "rows", description: "Capture-queue rows awaiting dispatch, as last observed by the dispatcher.");
```

- [ ] **Step 4: Update the queue depth from the dispatcher**

In `AuditDispatcher.DispatchOnceAsync`, just before `return processed;`, add:

```csharp
        OrionAuditTelemetry.SetQueueDepth(await ctx.Set<AuditCaptureQueueEntry>()
            .CountAsync(q => q.Error == null, cancellationToken).ConfigureAwait(false));
```

- [ ] **Step 5: Run test to verify it still passes and the solution builds**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter OrionAuditTelemetryTests` then `dotnet build OrionAudit.sln -c Debug`
Expected: PASS; build clean.

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs src/Moongazing.OrionAudit/Capture/AuditDispatcher.cs tests/Moongazing.OrionAudit.Tests/OrionAuditTelemetryTests.cs
git commit -m "feat(async): add capture-queue depth observable gauge"
```

---

### Task 11: Benchmark — async-capture arm

**Files:**
- Modify: `bench/Moongazing.OrionAudit.Bench/InterceptorBench.cs`
- Create: `bench/Moongazing.OrionAudit.Bench/DispatcherBench.cs`
- Modify: `bench/Moongazing.OrionAudit.Bench/README.md`

- [ ] **Step 1: Add the async arm to `InterceptorBench`**

In `InterceptorBench.cs`, add a third configured provider and benchmark. After the `plainSp` / `plainConn` fields add:

```csharp
    private SqliteConnection asyncConn = null!;
    private ServiceProvider asyncSp = null!;
```

In `Setup()`, after the plain context block, add an async-capture context:

```csharp
        // Async-capture context
        asyncConn = new SqliteConnection("DataSource=:memory:");
        await asyncConn.OpenAsync();
        var asyncServices = new ServiceCollection();
        asyncServices.AddOrionAudit<AuditDb>(o => o.Audit<Row>().UseAsyncCapture());
        asyncServices.AddSingleton(asyncConn);
        asyncServices.AddDbContext<AuditDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        asyncSp = asyncServices.BuildServiceProvider();
        await using (var scope = asyncSp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuditDb>().Database.EnsureCreatedAsync();
        }
```

In `Cleanup()`, add `await asyncSp.DisposeAsync(); await asyncConn.DisposeAsync();`.

Add the benchmark method:

```csharp
    [Benchmark]
    public async Task<int> SaveChanges_WithAsyncAudit()
    {
        await using var scope = asyncSp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDb>();
        for (var i = 0; i < BatchSize; i++)
        {
            ctx.Rows.Add(new Row { Name = $"r{i}", Amount = i });
        }
        return await ctx.SaveChangesAsync();
    }
```

This measures the *hot-path* cost only (the queue-row write); the dispatcher runs separately and is not on this path.

- [ ] **Step 2: Create `DispatcherBench.cs`**

```csharp
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Bench;

/// <summary>
/// Dispatcher throughput: how fast a queued batch is turned into AuditLog rows. The queue is
/// re-seeded per iteration so each measured FlushPendingAsync drains a known row count.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class DispatcherBench
{
    [Auditable]
    public sealed class Row
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public decimal Amount { get; set; }
    }

    [Params(100, 1000)]
    public int QueuedRows { get; set; }

    private SqliteConnection conn = null!;
    private ServiceProvider sp = null!;

    public sealed class AuditDb : DbContext
    {
        public DbSet<Row> Rows => Set<Row>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditCaptureQueueEntry> Queue => Set<AuditCaptureQueueEntry>();
        public AuditDb(DbContextOptions<AuditDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Row>().HasKey(r => r.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    [GlobalSetup]
    public async Task Setup()
    {
        conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<AuditDb>(o => o.Audit<Row>().UseAsyncCapture(q => q.BatchSize(QueuedRows)));
        services.AddSingleton(conn);
        services.AddDbContext<AuditDb>((s, o) =>
            o.UseSqlite(s.GetRequiredService<SqliteConnection>()).UseOrionAudit(s));
        sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuditDb>().Database.EnsureCreatedAsync();
    }

    [IterationSetup]
    public void SeedQueue()
    {
        using var scope = sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDb>();
        for (var i = 0; i < QueuedRows; i++)
        {
            ctx.Rows.Add(new Row { Name = $"r{i}", Amount = i });
        }
        ctx.SaveChanges();   // async mode → writes QueuedRows queue rows
    }

    [Benchmark]
    public async Task<int> FlushPending()
        => await sp.GetRequiredService<IAuditDispatcher>().FlushPendingAsync();

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await sp.DisposeAsync();
        await conn.DisposeAsync();
    }
}
```

- [ ] **Step 3: Update the bench README**

In `bench/Moongazing.OrionAudit.Bench/README.md`, in the "What's measured" table, change the `InterceptorBench` row to mention the async arm and add a `DispatcherBench` row:

```markdown
| `InterceptorBench`    | EF SaveChanges with no audit / sync audit / async audit, batch 1/10/100 |
| `DispatcherBench`     | Async dispatcher throughput draining 100 / 1000 queued rows         |
```

- [ ] **Step 4: Verify the benchmark project builds**

Run: `dotnet build bench/Moongazing.OrionAudit.Bench/Moongazing.OrionAudit.Bench.csproj -c Release`
Expected: build succeeds. (Do not run the benchmarks here — they run in Task 19 to source the README chart.)

- [ ] **Step 5: Commit**

```bash
git add bench/Moongazing.OrionAudit.Bench/InterceptorBench.cs bench/Moongazing.OrionAudit.Bench/DispatcherBench.cs bench/Moongazing.OrionAudit.Bench/README.md
git commit -m "bench(async): add async-capture arm to InterceptorBench and a DispatcherBench"
```

---

## Phase 2 — `OrionAudit.Viewer`

### Task 12: Render core — `FieldChange`, `AuditEntryView`, `AuditViewRenderer`

**Files:**
- Create: `src/Moongazing.OrionAudit/Read/AuditView.cs`
- Test: `tests/Moongazing.OrionAudit.Tests/AuditViewRendererTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Moongazing.OrionAudit.Tests/AuditViewRendererTests.cs`:

```csharp
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Read;

namespace Moongazing.OrionAudit.Tests;

public class AuditViewRendererTests
{
    private static AuditLog Log(string diff, AuditAction action = AuditAction.Updated) => new()
    {
        EntityType = "Some.Type, Some.Asm",
        EntityId = "1",
        Action = action,
        OccurredOnUtc = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
        UserDisplay = "Alice",
        Diff = diff,
    };

    [Fact]
    public void Render_Replace_ProducesModifiedFieldChange()
    {
        var view = AuditViewRenderer.Render(Log("""[{"op":"replace","path":"/Body","value":"v2"}]"""));
        var change = Assert.Single(view.Changes);
        Assert.Equal("/Body", change.PropertyPath);
        Assert.Equal(ChangeKind.Modified, change.ChangeKind);
        Assert.Equal("v2", change.NewValue);
    }

    [Fact]
    public void Render_Add_ProducesAddedFieldChange()
    {
        var view = AuditViewRenderer.Render(Log("""[{"op":"add","path":"/Tag","value":"x"}]"""));
        Assert.Equal(ChangeKind.Added, Assert.Single(view.Changes).ChangeKind);
    }

    [Fact]
    public void Render_Remove_ProducesRemovedFieldChange()
    {
        var view = AuditViewRenderer.Render(Log("""[{"op":"remove","path":"/Tag"}]"""));
        Assert.Equal(ChangeKind.Removed, Assert.Single(view.Changes).ChangeKind);
    }

    [Fact]
    public void Render_EmptyDiff_ProducesNoChanges()
        => Assert.Empty(AuditViewRenderer.Render(Log("[]")).Changes);

    [Fact]
    public void Render_CopiesEntryMetadata()
    {
        var view = AuditViewRenderer.Render(Log("[]", AuditAction.Inserted));
        Assert.Equal(AuditAction.Inserted, view.Action);
        Assert.Equal("Alice", view.UserDisplay);
        Assert.Equal(new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc), view.OccurredOnUtc);
    }

    [Fact]
    public void RenderMany_PreservesChronologicalOrder()
    {
        var older = Log("[]"); older.OccurredOnUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = Log("[]"); newer.OccurredOnUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var views = AuditViewRenderer.RenderMany(new[] { newer, older });
        Assert.True(views[0].OccurredOnUtc < views[1].OccurredOnUtc);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter AuditViewRendererTests`
Expected: FAIL — `AuditViewRenderer` / `AuditEntryView` / `FieldChange` / `ChangeKind` not defined.

- [ ] **Step 3: Create `AuditView.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Moongazing.OrionAudit.Read;

/// <summary>How a single field changed within an audited entry.</summary>
public enum ChangeKind
{
    /// <summary>A property gained a value (RFC 6902 <c>add</c>).</summary>
    Added,

    /// <summary>A property lost a value (RFC 6902 <c>remove</c>).</summary>
    Removed,

    /// <summary>A property's value changed (RFC 6902 <c>replace</c>).</summary>
    Modified,
}

/// <summary>One field-level change extracted from an <see cref="AuditLog"/> row's RFC 6902 diff.</summary>
public sealed class FieldChange
{
    /// <summary>JSON Pointer path of the changed property (e.g. <c>/Body</c>).</summary>
    public string PropertyPath { get; init; } = default!;

    /// <summary>The pre-change value as a string, or null for an <see cref="ChangeKind.Added"/> change.</summary>
    public string? OldValue { get; init; }

    /// <summary>The post-change value as a string, or null for a <see cref="ChangeKind.Removed"/> change.</summary>
    public string? NewValue { get; init; }

    /// <summary>Whether this field was added, removed, or modified.</summary>
    public ChangeKind ChangeKind { get; init; }
}

/// <summary>Human-readable view of a single <see cref="AuditLog"/> row.</summary>
public sealed class AuditEntryView
{
    /// <summary>The audit row's id.</summary>
    public Guid Id { get; init; }

    /// <summary>What kind of change the row records.</summary>
    public AuditAction Action { get; init; }

    /// <summary>UTC timestamp of the change.</summary>
    public DateTime OccurredOnUtc { get; init; }

    /// <summary>Human-readable user display name, when attributed.</summary>
    public string? UserDisplay { get; init; }

    /// <summary>Correlation id captured with the change, when present.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Field-level changes extracted from the row's diff.</summary>
    public IReadOnlyList<FieldChange> Changes { get; init; } = Array.Empty<FieldChange>();
}

/// <summary>
/// Turns <see cref="AuditLog"/> rows into <see cref="AuditEntryView"/>s. Pure — depends only on
/// <c>System.Text.Json</c>; works type-agnostically against the RFC 6902 diff (JSON-path based).
/// </summary>
public static class AuditViewRenderer
{
    /// <summary>Renders one audit row into a readable view.</summary>
    public static AuditEntryView Render(AuditLog row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new AuditEntryView
        {
            Id = row.Id,
            Action = row.Action,
            OccurredOnUtc = row.OccurredOnUtc,
            UserDisplay = row.UserDisplay,
            CorrelationId = row.CorrelationId,
            Changes = ParseChanges(row.Diff),
        };
    }

    /// <summary>Renders many audit rows, ordered chronologically by <see cref="AuditLog.OccurredOnUtc"/>.</summary>
    public static IReadOnlyList<AuditEntryView> RenderMany(IEnumerable<AuditLog> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.OrderBy(r => r.OccurredOnUtc).Select(Render).ToList();
    }

    private static IReadOnlyList<FieldChange> ParseChanges(string diff)
    {
        if (string.IsNullOrEmpty(diff) || diff == "[]")
        {
            return Array.Empty<FieldChange>();
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(diff);
        }
        catch (JsonException)
        {
            return Array.Empty<FieldChange>();
        }

        if (node is not JsonArray ops)
        {
            return Array.Empty<FieldChange>();
        }

        var changes = new List<FieldChange>(ops.Count);
        foreach (var op in ops)
        {
            if (op is not JsonObject obj)
            {
                continue;
            }
            var opName = obj["op"]?.GetValue<string>();
            var path = obj["path"]?.GetValue<string>();
            if (path is null || opName is null)
            {
                continue;
            }
            var value = obj["value"]?.ToJsonString();
            changes.Add(opName switch
            {
                "add" => new FieldChange { PropertyPath = path, NewValue = value, ChangeKind = ChangeKind.Added },
                "remove" => new FieldChange { PropertyPath = path, OldValue = null, ChangeKind = ChangeKind.Removed },
                "replace" => new FieldChange { PropertyPath = path, NewValue = value, ChangeKind = ChangeKind.Modified },
                _ => new FieldChange { PropertyPath = path, NewValue = value, ChangeKind = ChangeKind.Modified },
            });
        }
        return changes;
    }
}
```

Note: `value` is rendered via `ToJsonString()` so a string value appears quoted (`"v2"`); the test expects `"v2"` exactly. If a future task wants unquoted scalars, unwrap `JsonValue` there — out of scope here.

Wait — the test asserts `change.NewValue == "v2"` (no quotes). Adjust `ParseChanges` to unwrap scalar string values: replace `var value = obj["value"]?.ToJsonString();` with:

```csharp
            var valueNode = obj["value"];
            var value = valueNode is JsonValue jv && jv.TryGetValue<string>(out var s)
                ? s
                : valueNode?.ToJsonString();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter AuditViewRendererTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionAudit/Read/AuditView.cs tests/Moongazing.OrionAudit.Tests/AuditViewRendererTests.cs
git commit -m "feat(viewer): add audit view render core (AuditViewRenderer, FieldChange, AuditEntryView)"
```

---

### Task 13: `Moongazing.OrionAudit.Viewer` project scaffold

**Files:**
- Create: `src/Moongazing.OrionAudit.Viewer/Moongazing.OrionAudit.Viewer.csproj`
- Create: `src/Moongazing.OrionAudit.Viewer/docs/README.md`
- Modify: `OrionAudit.sln`

- [ ] **Step 1: Create the csproj**

Mirror `src/Moongazing.OrionAudit.AspNetCore/Moongazing.OrionAudit.AspNetCore.csproj` (open it first to copy its `<PropertyGroup>` — multi-target TFMs, package metadata, `docs/README.md` packaging). Create `src/Moongazing.OrionAudit.Viewer/Moongazing.OrionAudit.Viewer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <PackageId>OrionAudit.Viewer</PackageId>
    <Description>Embeddable, framework-agnostic audit-trail viewer for OrionAudit. One endpoint registration serves a JSON API and a built-in static UI.</Description>
    <PackageTags>orionaudit;audit;viewer;efcore;aspnetcore</PackageTags>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Moongazing.OrionAudit\Moongazing.OrionAudit.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Include="docs\README.md" Pack="true" PackagePath="\" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="wwwroot\**\*" />
  </ItemGroup>

</Project>
```

If `Moongazing.OrionAudit.AspNetCore.csproj` inherits shared package metadata from `Directory.Build.props`, drop any duplicated properties above and keep only what AspNetCore's csproj keeps. Match the sibling exactly.

- [ ] **Step 2: Create the package README**

Create `src/Moongazing.OrionAudit.Viewer/docs/README.md`:

```markdown
# OrionAudit.Viewer

Embeddable audit-trail viewer for [OrionAudit](https://www.nuget.org/packages/OrionAudit).

One endpoint registration mounts a JSON API plus a built-in static UI — no Blazor, no
build step, drops into any ASP.NET Core host.

```csharp
app.MapOrionAuditViewer<AppDbContext>("/audit", o => o.RequireAuthorization("AuditViewers"));
```

The viewer is read-only and authorization-required by default. See the OrionAudit
repository README for the full guide.
```

- [ ] **Step 3: Add the project to the solution**

Run: `dotnet sln OrionAudit.sln add src/Moongazing.OrionAudit.Viewer/Moongazing.OrionAudit.Viewer.csproj`
Expected: "Project ... added to the solution."

- [ ] **Step 4: Create a placeholder `wwwroot` so the EmbeddedResource glob resolves**

Create `src/Moongazing.OrionAudit.Viewer/wwwroot/.gitkeep` (empty file). The real `index.html` lands in Task 16.

- [ ] **Step 5: Verify the project builds**

Run: `dotnet build src/Moongazing.OrionAudit.Viewer/Moongazing.OrionAudit.Viewer.csproj -c Debug`
Expected: build succeeds (an empty library at this point).

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionAudit.Viewer/ OrionAudit.sln
git commit -m "build(viewer): scaffold the Moongazing.OrionAudit.Viewer project"
```

---

### Task 14: `MapOrionAuditViewer` endpoint group and options

**Files:**
- Create: `src/Moongazing.OrionAudit.Viewer/OrionAuditViewerOptions.cs`
- Create: `src/Moongazing.OrionAudit.Viewer/OrionAuditViewerEndpointExtensions.cs`
- Create: `tests/Moongazing.OrionAudit.Viewer.Tests/Moongazing.OrionAudit.Viewer.Tests.csproj`
- Test: `tests/Moongazing.OrionAudit.Viewer.Tests/ViewerAuthorizationTests.cs`

- [ ] **Step 1: Create the test project**

Mirror `tests/Moongazing.OrionAudit.AspNetCore.Tests/Moongazing.OrionAudit.AspNetCore.Tests.csproj`. Create `tests/Moongazing.OrionAudit.Viewer.Tests/Moongazing.OrionAudit.Viewer.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Moongazing.OrionAudit.Viewer\Moongazing.OrionAudit.Viewer.csproj" />
  </ItemGroup>

</Project>
```

Match the test SDK / xUnit package versions used by the existing test csproj files (open one and copy the `<PackageReference>` lines for `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `coverlet.collector` if present — they are likely centralised in `Directory.Build.props` or `Directory.Packages.props`; if so, omit versions here).

Run: `dotnet sln OrionAudit.sln add tests/Moongazing.OrionAudit.Viewer.Tests/Moongazing.OrionAudit.Viewer.Tests.csproj`

- [ ] **Step 2: Write the failing test**

Create `tests/Moongazing.OrionAudit.Viewer.Tests/ViewerAuthorizationTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Viewer;

namespace Moongazing.OrionAudit.Viewer.Tests;

public class ViewerAuthorizationTests
{
    public sealed class ViewerDb : DbContext
    {
        public ViewerDb(DbContextOptions<ViewerDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyOrionAuditConfigurations();
    }

    private static IHost BuildHost(Action<OrionAuditViewerOptions>? configure)
        => new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddAuthentication();
                    s.AddAuthorization();
                    s.AddDbContext<ViewerDb>(o => o.UseSqlite("DataSource=:memory:"));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapOrionAuditViewer<ViewerDb>("/audit", configure));
                }))
            .Build();

    [Fact]
    public async Task ApiEndpoint_WithoutAuthenticatedUser_Returns401()
    {
        using var host = BuildHost(configure: null);   // default: authorization required
        await host.StartAsync();
        var client = host.GetTestServer().CreateClient();

        var response = await client.GetAsync("/audit/api/meta");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiEndpoint_WithAllowAnonymous_DoesNotReturn401()
    {
        using var host = BuildHost(o => o.AllowAnonymous());
        await host.StartAsync();
        var client = host.GetTestServer().CreateClient();

        var response = await client.GetAsync("/audit/api/meta");
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Moongazing.OrionAudit.Viewer.Tests -c Debug`
Expected: FAIL — `OrionAuditViewerOptions` / `MapOrionAuditViewer` not defined.

- [ ] **Step 4: Create `OrionAuditViewerOptions.cs`**

```csharp
namespace Moongazing.OrionAudit.Viewer;

/// <summary>
/// Configures a <c>MapOrionAuditViewer</c> registration. Authorization is required by default;
/// call <see cref="AllowAnonymous"/> to opt out (dev use only).
/// </summary>
public sealed class OrionAuditViewerOptions
{
    internal string? AuthorizationPolicy { get; private set; }
    internal bool AnonymousAllowed { get; private set; }

    /// <summary>Requires the named authorization policy for every viewer endpoint.</summary>
    public OrionAuditViewerOptions RequireAuthorization(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        AuthorizationPolicy = policyName;
        AnonymousAllowed = false;
        return this;
    }

    /// <summary>
    /// Opts out of authorization, exposing the viewer to anonymous callers. Intended for local
    /// development only — never for an internet-facing deployment.
    /// </summary>
    public OrionAuditViewerOptions AllowAnonymous()
    {
        AnonymousAllowed = true;
        AuthorizationPolicy = null;
        return this;
    }
}
```

- [ ] **Step 5: Create `OrionAuditViewerEndpointExtensions.cs` (group + authorization only)**

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionAudit.Viewer;

/// <summary><see cref="IEndpointRouteBuilder"/> extensions that mount the OrionAudit viewer.</summary>
public static class OrionAuditViewerEndpointExtensions
{
    /// <summary>
    /// Mounts the audit viewer — a JSON API and a built-in static UI — under
    /// <paramref name="pathPrefix"/>, reading audit data from <typeparamref name="TDbContext"/>.
    /// Authorization is required unless <see cref="OrionAuditViewerOptions.AllowAnonymous"/> is called.
    /// </summary>
    public static IEndpointConventionBuilder MapOrionAuditViewer<TDbContext>(
        this IEndpointRouteBuilder endpoints,
        string pathPrefix,
        Action<OrionAuditViewerOptions>? configure = null)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathPrefix);

        var options = new OrionAuditViewerOptions();
        configure?.Invoke(options);

        var prefix = pathPrefix.TrimEnd('/');
        var group = endpoints.MapGroup(prefix);

        OrionAuditViewerApi.Map<TDbContext>(group);
        OrionAuditViewerStaticFiles.Map(group);

        if (options.AnonymousAllowed)
        {
            group.AllowAnonymous();
        }
        else if (options.AuthorizationPolicy is { } policy)
        {
            group.RequireAuthorization(policy);
        }
        else
        {
            group.RequireAuthorization();   // default: any authenticated user
        }

        return group;
    }
}
```

This references `OrionAuditViewerApi.Map` and `OrionAuditViewerStaticFiles.Map`, created in Tasks 15 and 16. To make this task compile and its test pass now, create both as minimal stubs:

Create `src/Moongazing.OrionAudit.Viewer/OrionAuditViewerApi.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionAudit.Viewer;

internal static class OrionAuditViewerApi
{
    // Full endpoints land in Task 15. Stub keeps the meta route present for the auth test.
    public static void Map<TDbContext>(RouteGroupBuilder group)
        where TDbContext : DbContext
        => group.MapGet("/api/meta", () => Results.Ok(new { auditedTypes = Array.Empty<string>(), queueDepth = 0 }));
}
```

Create `src/Moongazing.OrionAudit.Viewer/OrionAuditViewerStaticFiles.cs`:

```csharp
using Microsoft.AspNetCore.Routing;

namespace Moongazing.OrionAudit.Viewer;

internal static class OrionAuditViewerStaticFiles
{
    // Full embedded-SPA serving lands in Task 16.
    public static void Map(RouteGroupBuilder group) { }
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/Moongazing.OrionAudit.Viewer.Tests -c Debug`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Moongazing.OrionAudit.Viewer/ tests/Moongazing.OrionAudit.Viewer.Tests/ OrionAudit.sln
git commit -m "feat(viewer): add MapOrionAuditViewer endpoint group with authorization-by-default"
```

---

### Task 15: Viewer JSON API endpoints

**Files:**
- Modify: `src/Moongazing.OrionAudit.Viewer/OrionAuditViewerApi.cs`
- Test: `tests/Moongazing.OrionAudit.Viewer.Tests/ViewerApiTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Moongazing.OrionAudit.Viewer.Tests/ViewerApiTests.cs`:

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

public class ViewerApiTests
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
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private sealed record LogPage(IReadOnlyList<EntryDto> entries);
    private sealed record EntryDto(string action, IReadOnlyList<ChangeDto> changes);
    private sealed record ChangeDto(string propertyPath, string changeKind);

    private static async Task<(IHost host, SqliteConnection conn)> BuildAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddAuthentication();
                    s.AddAuthorization();
                    s.AddSingleton(conn);
                    s.AddOrionAudit<ApiDb>(o => o.Audit<Note>());
                    s.AddDbContext<ApiDb>((sp, o) =>
                        o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                        e.MapOrionAuditViewer<ApiDb>("/audit", o => o.AllowAnonymous()));
                }))
            .Build();
        await host.StartAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApiDb>().Database.EnsureCreatedAsync();
        }
        return (host, conn);
    }

    [Fact]
    public async Task LogEndpoint_ReturnsRenderedEntries()
    {
        var (host, conn) = await BuildAsync();
        using var _h = host;
        await using var _c = conn;

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApiDb>();
            ctx.Notes.Add(new Note { Body = "first" });
            await ctx.SaveChangesAsync();
        }

        var page = await host.GetTestServer().CreateClient()
            .GetFromJsonAsync<LogPage>("/audit/api/log?page=1&size=20");
        Assert.NotNull(page);
        Assert.Single(page!.entries);
        Assert.Equal("Inserted", page.entries[0].action);
    }

    [Fact]
    public async Task MetaEndpoint_ReturnsAuditedTypeNames()
    {
        var (host, conn) = await BuildAsync();
        using var _h = host;
        await using var _c = conn;

        var meta = await host.GetTestServer().CreateClient()
            .GetFromJsonAsync<MetaDto>("/audit/api/meta");
        Assert.NotNull(meta);
        Assert.Contains(meta!.auditedTypes, t => t.Contains("Note", StringComparison.Ordinal));
    }

    private sealed record MetaDto(IReadOnlyList<string> auditedTypes, int queueDepth);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Moongazing.OrionAudit.Viewer.Tests -c Debug --filter ViewerApiTests`
Expected: FAIL — `/audit/api/log` 404s (stub only maps `/api/meta`), and `meta` returns an empty `auditedTypes`.

- [ ] **Step 3: Implement the full API**

Replace `src/Moongazing.OrionAudit.Viewer/OrionAuditViewerApi.cs` with:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Read;

namespace Moongazing.OrionAudit.Viewer;

/// <summary>Maps the viewer's read-only JSON API onto a route group.</summary>
internal static class OrionAuditViewerApi
{
    public static void Map<TDbContext>(RouteGroupBuilder group)
        where TDbContext : DbContext
    {
        // Paged recent audit rows.
        group.MapGet("/api/log", async (TDbContext db, IAuditTenantResolver? tenant, int page, int size) =>
        {
            var take = Math.Clamp(size <= 0 ? 50 : size, 1, 500);
            var skip = Math.Max(page - 1, 0) * take;
            var rows = await db.AuditLog()
                .OrderByDescending(a => a.OccurredOnUtc)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
            return Results.Ok(new { entries = AuditViewRenderer.RenderMany(rows) });
        });

        // One entity's chronological timeline.
        group.MapGet("/api/{entityType}/{key}", async (TDbContext db, string entityType, string key) =>
        {
            var rows = await db.AuditLog()
                .Where(a => a.EntityType == entityType && a.EntityId == key)
                .ToListAsync();
            return Results.Ok(new { entries = AuditViewRenderer.RenderMany(rows) });
        });

        // Audited type names + (in async mode) the capture-queue depth.
        group.MapGet("/api/meta", async (IAuditConfiguration config, IAuditDispatcher dispatcher) =>
        {
            var queueDepth = await dispatcher.GetQueueDepthAsync();
            return Results.Ok(new
            {
                auditedTypes = config.AuditedTypeNames,
                queueDepth,
            });
        });
    }
}
```

This references `IAuditConfiguration.AuditedTypeNames`. Open `src/Moongazing.OrionAudit/Configuration/IAuditConfiguration.cs`: if it already exposes the audited type names, use that member's exact name. If it does not, add to `IAuditConfiguration` and its implementation a property `IReadOnlyCollection<string> AuditedTypeNames` returning the assembly-qualified names of the registered audited types (the configuration already holds the registered type set internally — expose it). Adjust the test's `MetaDto` only if the member name differs.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Moongazing.OrionAudit.Viewer.Tests -c Debug --filter ViewerApiTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionAudit.Viewer/OrionAuditViewerApi.cs src/Moongazing.OrionAudit/Configuration/ tests/Moongazing.OrionAudit.Viewer.Tests/ViewerApiTests.cs
git commit -m "feat(viewer): implement log, timeline, and meta JSON API endpoints"
```

---

### Task 16: Embedded static SPA

**Files:**
- Create: `src/Moongazing.OrionAudit.Viewer/wwwroot/index.html`
- Modify: `src/Moongazing.OrionAudit.Viewer/OrionAuditViewerStaticFiles.cs`
- Test: `tests/Moongazing.OrionAudit.Viewer.Tests/ViewerStaticFilesTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Moongazing.OrionAudit.Viewer.Tests/ViewerStaticFilesTests.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moongazing.OrionAudit.Viewer;

namespace Moongazing.OrionAudit.Viewer.Tests;

public class ViewerStaticFilesTests
{
    public sealed class StaticDb : DbContext
    {
        public StaticDb(DbContextOptions<StaticDb> options) : base(options) { }
    }

    [Fact]
    public async Task Root_ServesEmbeddedHtml()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddAuthentication();
                    s.AddAuthorization();
                    s.AddDbContext<StaticDb>(o => o.UseSqlite("DataSource=:memory:"));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                        e.MapOrionAuditViewer<StaticDb>("/audit", o => o.AllowAnonymous()));
                }))
            .StartAsync();

        var response = await host.GetTestServer().CreateClient().GetAsync("/audit");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("OrionAudit", body, StringComparison.Ordinal);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Moongazing.OrionAudit.Viewer.Tests -c Debug --filter ViewerStaticFilesTests`
Expected: FAIL — `/audit` 404s (the static-files stub maps nothing).

- [ ] **Step 3: Create the SPA**

Create `src/Moongazing.OrionAudit.Viewer/wwwroot/index.html` — a single self-contained file (vanilla JS, no build step). Keep it small; it must fetch `./api/log`, `./api/meta`, and `./api/{type}/{key}` (relative paths so it works under any prefix) and render a timeline with before/after `FieldChange`s, a queue-depth badge, and visual flags for redacted/hashed fields:

```html
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>OrionAudit Viewer</title>
<style>
  body { font: 14px/1.5 system-ui, sans-serif; margin: 0; background: #0f1115; color: #e6e6e6; }
  header { padding: 14px 20px; background: #161922; border-bottom: 1px solid #2a2f3a; display: flex; align-items: center; gap: 12px; }
  h1 { font-size: 16px; margin: 0; font-weight: 600; }
  .badge { font-size: 12px; padding: 2px 8px; border-radius: 10px; background: #2a2f3a; }
  main { padding: 20px; max-width: 900px; margin: 0 auto; }
  .entry { border: 1px solid #2a2f3a; border-radius: 8px; margin-bottom: 12px; overflow: hidden; }
  .entry-head { padding: 10px 14px; background: #161922; display: flex; gap: 12px; }
  .action { font-weight: 600; }
  .change { padding: 6px 14px; border-top: 1px solid #2a2f3a; display: grid; grid-template-columns: 1fr 80px 1fr; gap: 10px; }
  .path { color: #8ab4f8; }
  .old { color: #f28b82; } .new { color: #81c995; }
  .kind { text-transform: uppercase; font-size: 11px; opacity: .7; }
</style>
</head>
<body>
<header>
  <h1>OrionAudit Viewer</h1>
  <span class="badge" id="queue">queue: …</span>
</header>
<main id="log">Loading…</main>
<script>
const base = location.pathname.replace(/\/$/, "");
async function load() {
  const meta = await fetch(base + "/api/meta").then(r => r.json());
  document.getElementById("queue").textContent =
    "pending: " + (meta.queueDepth ?? 0);
  const page = await fetch(base + "/api/log?page=1&size=50").then(r => r.json());
  const root = document.getElementById("log");
  root.innerHTML = "";
  for (const e of page.entries) {
    const div = document.createElement("div");
    div.className = "entry";
    const head = document.createElement("div");
    head.className = "entry-head";
    head.innerHTML = '<span class="action">' + e.action + '</span>' +
      '<span>' + new Date(e.occurredOnUtc).toLocaleString() + '</span>' +
      '<span>' + (e.userDisplay ?? "—") + '</span>';
    div.appendChild(head);
    for (const c of e.changes) {
      const row = document.createElement("div");
      row.className = "change";
      row.innerHTML = '<span class="path">' + c.propertyPath + '</span>' +
        '<span class="kind">' + c.changeKind + '</span>' +
        '<span><span class="old">' + (c.oldValue ?? "") + '</span> ' +
        '<span class="new">' + (c.newValue ?? "") + '</span></span>';
      div.appendChild(row);
    }
    root.appendChild(div);
  }
  if (!page.entries.length) root.textContent = "No audit entries yet.";
}
load().catch(err => document.getElementById("log").textContent = "Error: " + err);
</script>
</body>
</html>
```

- [ ] **Step 4: Implement embedded-resource serving**

Replace `src/Moongazing.OrionAudit.Viewer/OrionAuditViewerStaticFiles.cs` with:

```csharp
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Moongazing.OrionAudit.Viewer;

/// <summary>Serves the viewer's embedded single-page UI at the route-group root.</summary>
internal static class OrionAuditViewerStaticFiles
{
    private static readonly Lazy<string> Html = new(LoadHtml);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", () => Results.Content(Html.Value, "text/html"));
        group.MapGet("", () => Results.Content(Html.Value, "text/html"));
    }

    private static string LoadHtml()
    {
        var asm = typeof(OrionAuditViewerStaticFiles).Assembly;
        // EmbeddedResource logical name: <RootNamespace>.wwwroot.index.html
        var name = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith("wwwroot.index.html", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded viewer index.html not found.");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

If the `EmbeddedResource` logical name does not end with `wwwroot.index.html` (depends on the csproj `<EmbeddedResource>` element), run a one-off `asm.GetManifestResourceNames()` dump in the test to find the actual name and adjust the `EndsWith` argument.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Moongazing.OrionAudit.Viewer.Tests -c Debug --filter ViewerStaticFilesTests`
Expected: PASS.

- [ ] **Step 6: Remove the placeholder and commit**

```bash
git rm src/Moongazing.OrionAudit.Viewer/wwwroot/.gitkeep
git add src/Moongazing.OrionAudit.Viewer/wwwroot/index.html src/Moongazing.OrionAudit.Viewer/OrionAuditViewerStaticFiles.cs tests/Moongazing.OrionAudit.Viewer.Tests/ViewerStaticFilesTests.cs
git commit -m "feat(viewer): serve the embedded single-page audit UI"
```

---

### Task 17: Viewer tenant-filter test and full Viewer suite

**Files:**
- Test: `tests/Moongazing.OrionAudit.Viewer.Tests/ViewerTenantFilterTests.cs`

- [ ] **Step 1: Write the test**

Create `tests/Moongazing.OrionAudit.Viewer.Tests/ViewerTenantFilterTests.cs`. It registers an `IAuditTenantResolver` returning a fixed tenant, writes audit rows under two tenants directly into the table, and asserts `/audit/api/log` only returns the resolver's tenant rows (the API uses `db.AuditLog()`, which applies the tenant filter):

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

public class ViewerTenantFilterTests
{
    public sealed class TenantDb : DbContext
    {
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TenantDb(DbContextOptions<TenantDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyOrionAuditConfigurations();
    }

    private sealed class FixedTenant : IAuditTenantResolver
    {
        public string? Resolve(IServiceProvider serviceProvider) => "tenant-A";
    }

    private sealed record LogPage(IReadOnlyList<object> entries);

    [Fact]
    public async Task LogEndpoint_OnlyReturnsCurrentTenantRows()
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
                    s.AddScoped<IAuditTenantResolver, FixedTenant>();
                    s.AddDbContext<TenantDb>((sp, o) =>
                        o.UseSqlite(sp.GetRequiredService<SqliteConnection>()));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                        e.MapOrionAuditViewer<TenantDb>("/audit", o => o.AllowAnonymous()));
                }))
            .StartAsync();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TenantDb>();
            await ctx.Database.EnsureCreatedAsync();
            ctx.AuditLogs.Add(new AuditLog { EntityType = "T", EntityId = "1", TenantId = "tenant-A", OccurredOnUtc = DateTime.UtcNow });
            ctx.AuditLogs.Add(new AuditLog { EntityType = "T", EntityId = "2", TenantId = "tenant-B", OccurredOnUtc = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        var page = await host.GetTestServer().CreateClient()
            .GetFromJsonAsync<LogPage>("/audit/api/log?page=1&size=20");
        Assert.NotNull(page);
        Assert.Single(page!.entries);   // tenant-B row filtered out
    }
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test tests/Moongazing.OrionAudit.Viewer.Tests -c Debug --filter ViewerTenantFilterTests`
Expected: PASS. (`db.AuditLog()` applies the tenant filter via the registered resolver; no production change needed. If it FAILS, the API endpoints in Task 15 must use `db.AuditLog()` / `db.AuditFor<T>()` rather than `db.Set<AuditLog>()` directly — fix Task 15's code.)

- [ ] **Step 3: Run the entire Viewer test project**

Run: `dotnet test tests/Moongazing.OrionAudit.Viewer.Tests -c Debug`
Expected: PASS — all Viewer tests (authorization, API, static files, tenant filter).

- [ ] **Step 4: Commit**

```bash
git add tests/Moongazing.OrionAudit.Viewer.Tests/ViewerTenantFilterTests.cs
git commit -m "test(viewer): verify the viewer API honours tenant filtering"
```

---

## Phase 3 — Release

### Task 18: Version bump

**Files:**
- Modify: `Directory.Build.props`
- Modify: `src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<Version>0.4.0</Version>` to `<Version>0.5.0</Version>`.

- [ ] **Step 2: Bump the telemetry source version**

In `OrionAuditTelemetry.cs`, change both `new(ActivitySourceName, "0.4.0")` and `new Meter(MeterName, "0.4.0")` to `"0.5.0"`.

- [ ] **Step 3: Update the telemetry version test**

Open `tests/Moongazing.OrionAudit.Tests/OrionAuditTelemetryTests.cs`. If it asserts the version string `"0.4.0"`, change that assertion to `"0.5.0"`.

- [ ] **Step 4: Verify**

Run: `dotnet test tests/Moongazing.OrionAudit.Tests -c Debug --filter OrionAuditTelemetryTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Directory.Build.props src/Moongazing.OrionAudit/Telemetry/OrionAuditTelemetry.cs tests/Moongazing.OrionAudit.Tests/OrionAuditTelemetryTests.cs
git commit -m "release: bump version and telemetry to 0.5.0"
```

---

### Task 19: Run benchmarks and capture numbers

**Files:** none modified — this task produces the figures used in Task 20's README.

- [ ] **Step 1: Run the interceptor benchmark**

Run: `dotnet run -c Release --project bench/Moongazing.OrionAudit.Bench/Moongazing.OrionAudit.Bench.csproj -- --filter "*InterceptorBench*"`
Expected: a summary table with `SaveChanges_NoAudit`, `SaveChanges_WithAudit`, `SaveChanges_WithAsyncAudit` for batch 1/10/100.

- [ ] **Step 2: Run the dispatcher benchmark**

Run: `dotnet run -c Release --project bench/Moongazing.OrionAudit.Bench/Moongazing.OrionAudit.Bench.csproj -- --filter "*DispatcherBench*"`
Expected: a summary table with `FlushPending` for 100 / 1000 queued rows.

- [ ] **Step 3: Record the numbers**

Copy the two summary tables (Method / Mean / Ratio / Allocated columns) into a scratch note — they feed the README benchmark section in Task 20. No commit for this task.

---

### Task 20: Documentation — CHANGELOG, ROADMAP, ECOSYSTEM, README

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `ROADMAP.md`
- Modify: `ECOSYSTEM.md`
- Modify: `README.md`

- [ ] **Step 1: Add the CHANGELOG entry**

In `CHANGELOG.md`, insert a new section directly under the `# Changelog` preamble, above `## [0.4.0]`:

```markdown
## [0.5.0] - 2026-05-22

Throughput & Visibility release. Adds an opt-in async staging-capture mode that moves
diff/snapshot work off the `SaveChanges` hot path without weakening atomic, lossless capture,
plus `OrionAudit.Viewer` — a self-contained, Blazor-free audit-trail viewer.

### Added

- **Async staging-capture (`UseAsyncCapture`).** Opt-in. The interceptor writes a lightweight
  `OrionAudit_Capture_Queue` row in the consumer's transaction; the new
  `AuditDispatcherHostedService` background dispatcher computes the diff and writes the final
  `AuditLog` row shortly after. Capture stays atomic and lossless — the queue row commits with
  the data change — while audit becomes eventually consistent. Dispatch is exactly-once
  (`AuditLog` inserts and queue-row deletes commit in one transaction). A malformed row is
  dead-lettered after `MaxAttempts`.
- **`IAuditDispatcher`** with `FlushPendingAsync` (force-drain the queue — tests and
  read-after-write call sites) and `GetQueueDepthAsync`. A no-op implementation is registered
  in synchronous mode so the dependency is always resolvable.
- **`OrionAudit.Viewer` package.** `app.MapOrionAuditViewer<TDbContext>("/audit")` mounts a
  read-only JSON API plus a built-in embedded single-page UI. No Blazor dependency; drops into
  any ASP.NET Core host. Authorization is required by default.
- **Audit view render core.** `AuditViewRenderer` / `AuditEntryView` / `FieldChange` in
  `Moongazing.OrionAudit.Read` turn an `AuditLog` row and its RFC 6902 diff into a structured,
  human-readable view model.
- **Telemetry.** `OrionAudit.Dispatch` activity; counters `orionaudit.dispatch.rows_processed`
  / `orionaudit.dispatch.rows_deadlettered`; histogram `orionaudit.dispatch.batch.duration`;
  observable gauge `orionaudit.capture.queue_depth`. `ActivitySource` / `Meter` version → 0.5.0.

### Changed

- `ApplyOrionAuditConfigurations` now also maps the `OrionAudit_Capture_Queue` companion table
  (a new optional `captureQueueTableName` parameter overrides its name). Harmless when async
  capture is not configured — the table simply stays empty.

### Migration from v0.4.0

- **Synchronous consumers:** no code change. The capture path is byte-for-byte identical.
- **Schema:** adopting v0.5.0 requires one EF migration creating `OrionAudit_Capture_Queue`.
- **Async capture and the Viewer are both opt-in.** Audit becomes eventually consistent under
  `UseAsyncCapture`; use `IAuditDispatcher.FlushPendingAsync` where read-after-write is needed.
```

- [ ] **Step 2: Update ROADMAP**

In `ROADMAP.md`: rewrite the `## v0.5.0` section heading to `## v0.5.0 — Throughput & Visibility *(shipped)*` and replace its body bullet list with the async-capture + Viewer summary. Add a new `## v0.6.0 — Developer Experience *(planned)*` section carrying the deferred `AddColumn` extensibility and legacy import. Move the CLI diff renderer into a *Considered* bullet. Update the release-cadence table: mark v0.5.0 shipped, add a v0.6.0 row.

- [ ] **Step 3: Update ECOSYSTEM**

In `ECOSYSTEM.md`, in the "Shipped" table, change the OrionAudit row version to `v0.5.0` and update its headline to mention async capture + viewer.

- [ ] **Step 4: Update README**

In `README.md`: add an async-capture section (the `UseAsyncCapture` snippet and the eventual-consistency note), a Viewer section (the `MapOrionAuditViewer` snippet), and a benchmark subsection with the sync-vs-async hot-path table from Task 19. Add `OrionAudit.Viewer` to the ecosystem-packages table.

- [ ] **Step 5: Commit**

```bash
git add CHANGELOG.md ROADMAP.md ECOSYSTEM.md README.md
git commit -m "docs(release): document v0.5.0 — async staging-capture and OrionAudit.Viewer"
```

---

### Task 21: CI workflow and final full-solution verification

**Files:**
- Modify: `.github/workflows/ci-cd.yml`

- [ ] **Step 1: Inspect the workflow**

Open `.github/workflows/ci-cd.yml`. The `build-and-test` job builds `OrionAudit.sln` and runs `dotnet test` — adding the new projects to the solution (done in Tasks 13–14) means CI picks them up automatically. Confirm the `publish` job packs every `src/` package: if it packs by explicit project path, add
`src/Moongazing.OrionAudit.Viewer/Moongazing.OrionAudit.Viewer.csproj`; if it packs `OrionAudit.sln` or globs `src/**`, no change is needed.

- [ ] **Step 2: Update the publish job if needed**

If the `publish` job lists pack targets explicitly, add the Viewer project alongside `OrionAudit` and `OrionAudit.AspNetCore`. Otherwise leave it.

- [ ] **Step 3: Full build**

Run: `dotnet build OrionAudit.sln -c Release`
Expected: build succeeds across all TFMs, no warnings-as-errors failures.

- [ ] **Step 4: Full test run**

Run: `dotnet test OrionAudit.sln -c Release`
Expected: PASS — every test project green (core, integration, AspNetCore, Testing, Viewer).

- [ ] **Step 5: AOT probe**

Run: `dotnet publish aot/Moongazing.OrionAudit.AotProbe -c Release -r win-x64`
Expected: publish succeeds with no `IL2*` / `IL3*` warnings. The async-capture types (`AuditDispatcher`, `AuditCaptureQueueEntry`) are EF-coupled and not exercised by the probe; confirm none of the new *reflection-free* core types (`AuditViewRenderer`) introduced a warning. If the probe surfaces a warning, fix it before proceeding.

- [ ] **Step 6: Commit**

```bash
git add .github/workflows/ci-cd.yml
git commit -m "ci: include OrionAudit.Viewer in build, test, and publish"
```

- [ ] **Step 7: Tag the release**

Per ECOSYSTEM §7 release discipline — only after a maintainer confirms:

```bash
git tag v0.5.0
git push origin master --tags
```

The CI `publish` job runs on the GitHub release event and pushes `OrionAudit` and
`OrionAudit.Viewer` to NuGet.

---

## Self-Review

**Spec coverage:**

- §2.1 async staging-capture — Tasks 1–11. ✓
- §3 atomic/lossless capture — Task 5 (`QueueRow_RolledBackWithTheDataChange` test). ✓
- §4.1 hot-path/deferred split, redaction on hot path — Task 5 (`BuildQueueEntry` applies `SnapshotBuilder` rules). ✓
- §4.2 `OrionAudit_Capture_Queue` table — Tasks 2–3. ✓
- §4.3 dispatcher, exactly-once, dead-letter — Tasks 6–7. ✓
- §4.4 multi-instance claim — Task 6 (`ClaimToken`/`ClaimedUtc`, `ExecuteUpdateAsync` claim, stale-claim reclaim). Note: a dedicated two-dispatcher test is *not* in the plan; the claim mechanism is exercised indirectly. Acceptable for v0.5.0 — single-process is the common case — but a maintainer may add a concurrency test.
- §4.5 configuration surface — Task 1. ✓
- §5 `IAuditDispatcher` + `FlushPendingAsync` + `GetQueueDepthAsync` — Tasks 6, 8. ✓
- §6 read-side consistency — `FlushPendingAsync` (Task 6); no `includePending` (correctly absent). ✓
- §7 Viewer — Tasks 13–17. ✓
- §8 render core in core OrionAudit — Task 12. ✓
- §9 benchmark — Task 11 (arms) + Task 19 (run). ✓
- §10 telemetry — Task 6 (instruments) + Task 10 (gauge) + Task 18 (version). ✓
- §11 testing — covered across Tasks 5–9, 12, 14–17. ✓
- §12 versioning — Task 18; TFMs unchanged. ✓
- §13 docs — Task 20. ✓
- §14 release — Task 21. ✓
- §15 migration notes — Task 20 CHANGELOG. ✓

**Placeholder scan:** No "TBD"/"TODO". Two forward references are deliberate and resolved within the plan: Task 6 creates the three dispatch telemetry instruments early (with a note that Task 10 only adds the gauge); Task 14 creates `OrionAuditViewerApi`/`OrionAuditViewerStaticFiles` as stubs that Tasks 15–16 replace. Both are stated explicitly with full code.

**Type consistency:** `IAuditDispatcher.FlushPendingAsync` returns `Task<int>` everywhere (interface, `AuditDispatcher`, `NoOpAuditDispatcher`, all tests). `AuditCaptureQueueEntry` property names are identical across the entity, the EF configuration, `BuildQueueEntry`, and `AuditDispatcher.BuildAuditLog`. `AuditEntryView` / `FieldChange` / `ChangeKind` member names match between Task 12's definition and Task 15's API usage. `MapOrionAuditViewer<TDbContext>` signature is identical across Tasks 14–17.

**Known soft spot:** Task 15 depends on `IAuditConfiguration` exposing audited type names as `AuditedTypeNames`. The task explicitly instructs verifying the member against `IAuditConfiguration.cs` and adding it if absent — flagged rather than assumed.
