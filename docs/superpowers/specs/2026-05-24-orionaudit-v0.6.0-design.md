# OrionAudit v0.6.0 — Design Spec

**Date:** 2026-05-24
**Status:** Approved — ready for implementation planning.
**Authors:** Tunahan Ali Ozturk
**Family:** Orion (sibling of OrionGuard)
**Predecessors:** [v0.1.0][s1] / [v0.2.0][s2] / [v0.3.0][s3] / [v0.4.0][s4] / [v0.5.0][s5]

[s1]: 2026-05-13-orionaudit-v0.1.0-design.md
[s2]: 2026-05-19-orionaudit-v0.2.0-design.md
[s3]: 2026-05-19-orionaudit-v0.3.0-design.md
[s4]: 2026-05-20-orionaudit-v0.4.0-design.md
[s5]: 2026-05-22-orionaudit-v0.5.0-design.md

## 1. Goal

**Theme: Developer Experience.** Two opt-in additions that let consumers adopt OrionAudit into
existing systems and index the dimensions their business cares about, without forking:

1. **Extensible `AuditLog` row (`AddColumn`).** A fluent surface for adding tipped, indexable
   custom columns to the audit table — `o.AddColumn<int>("WorkflowStepId", ctx => ...)`. Real
   EF shadow-property columns (not a JSON bag), so consumers can write fast LINQ filters and
   create the indexes their queries need.
2. **Legacy import (`AuditImportBuilder`).** Fluent bulk-import of hand-rolled change history
   as synthetic, idempotent `AuditLog` rows. Diff produced by the same `Json6902` engine the
   capture path uses, so imported history is byte-for-byte indistinguishable from native
   capture and replays cleanly through `AuditReconstructor`.

Both items unlock real adoption scenarios that v0.5.0 deferred: a consumer migrating from a
home-grown audit table needs Import; a consumer that needs to query "audit rows for workflow
step 3" without scanning JSON needs `AddColumn`. The package count stays at four — both
features land in core `OrionAudit`.

## 2. Scope

### In scope (v0.6.0)

1. **`AddColumn<T>` configuration surface** on `OrionAuditOptions`, with `AuditColumnContext`
   carrying `Entity` / `Entry` / `Action` / `User` / `TenantId` to the value provider.
2. **`AuditLog` shadow-property mapping** — each registered custom column becomes a real,
   nullable, tipped EF column.
3. **Async-mode integration** — `OrionAudit_Capture_Queue` gains a nullable `CustomColumnsJson`
   text column; the interceptor's async branch serialises custom values into it, the
   dispatcher deserialises and applies them to the final `AuditLog` row.
4. **`AuditImportBuilder`** — fluent record-by-record builder, batched transactional writes,
   `ImportBatch`-tag idempotency stamped into `CorrelationId` (no schema change), per-record
   `WithColumn` for custom-column values, async-mode-agnostic (always writes `AuditLog`
   directly, bypassing the capture queue).
5. **`AuditEntryView.CustomColumns`** — read-side surface exposing custom columns as
   `IReadOnlyDictionary<string, object?>`; the Viewer SPA renders them as badges on each entry.
6. **Telemetry** for import, version + docs bump.

### Considered but not committed for v0.6.0

- **CLI diff renderer.** Stays in *Considered* per the v0.5.0 elimination decision — with the
  Viewer covering "see the audit trail," the cost/value trade-off for a `dotnet tool` is
  marginal until a concrete CI/log-inspection use case lands.
- **Per-entity / per-field viewer labels.** Roadmap entry for v0.7.0; small UI feature better
  shipped with the rest of the polymorphic-capture work.
- **Web push notifications on audit write.** Belongs to the v0.7.0 outbox theme.

### Explicitly not in scope

- **A JSON "extensions" column** (alternative to shadow properties). Rejected during v0.5.0
  brainstorming — the goal of `AddColumn` is *indexable* dimensions; a JSON bag undermines
  that. Tipped shadow properties are the right primitive even at the cost of a migration per
  column.
- **Column-level access control.** Custom columns inherit the audit table's
  authorization surface (the viewer's `RequireAuthorization` covers them). No separate
  per-column policy.
- **Streaming/incremental import.** The importer is write-only, write-forward: `Add` then
  `SaveAsync`. A consumer with very large legacy history splits it across multiple importer
  invocations — same idempotency tag, distinct `SourceId`s.

## 3. The load-bearing constraints

Two invariants from prior releases that v0.6.0 must not weaken:

- **Capture stays atomic and lossless** (the v0.1.0 promise, refined in v0.5.0 §3). Every new
  code path in this release runs *inside* the capture or import transaction, not afterwards;
  a custom-column provider exception is captured into `AuditLog.Error` exactly the way a
  failing diff is captured today, and the row is still written.
- **Imported history is byte-for-byte compatible with native capture.** The importer reuses
  `SnapshotBuilder` (including `[Hashed]` / `[Redacted]` / `[NotAuditable]` rules) and the
  `Json6902` engine. A test (§11) compares an import diff to the synchronous-capture diff for
  the same before/after state and requires byte equality — that test is the contract.

## 4. `AddColumn` — fluent configuration surface

### 4.1 Public API

```csharp
services.AddOrionAudit<AppDbContext>(o => o
    .Audit<Order>()
    .AddColumn<int>("WorkflowStepId", ctx => (ctx.Entity as IHasWorkflow)?.StepId)
    .AddColumn<string>("Source",      ctx => ctx.Action == AuditAction.Inserted ? "import" : "app")
    .AddColumn<Guid?>("RequestId",    ctx => ctx.User?.Id is { } id ? Guid.Parse(id) : null));
```

`AddColumn<T>` returns the same `OrionAuditOptions` for chaining. Calling it twice with the
same name throws `OrionAuditConfigurationException` at registration time.

### 4.2 `AuditColumnContext`

A record passed to every provider invocation:

```csharp
public sealed record AuditColumnContext(
    object Entity,            // the audited entity instance
    EntityEntry Entry,        // its EF change-tracker entry
    AuditAction Action,       // Inserted / Updated / Deleted / SoftDeleted
    AuditUser? User,          // resolved IAuditUserResolver output, may be null
    string? TenantId);        // resolved IAuditTenantResolver output, may be null
```

These are exactly the inputs already in scope inside `AuditSaveChangesInterceptor.SavingChangesAsync`.

### 4.3 Type constraints

`T` must be one of: `string`, `Guid`, `int`, `long`, `short`, `byte`, `bool`, `decimal`,
`double`, `float`, `DateTime`, `DateTimeOffset`, an `enum`, or a `Nullable<>` of any of those.
Validation runs in `AddColumn`'s body via a small `IsSupportedColumnType` helper and throws
`OrionAuditConfigurationException` with the column name and the offending type.

String columns default to `HasMaxLength(512)`. Other types use EF's default length conventions.

### 4.4 Shadow-property mapping

`AuditLogEntityTypeConfiguration` reads a new injected `IReadOnlyList<CustomColumn>` (from
DI; produced by `AuditConfigurationBuilder.Build`). For each:

```csharp
builder.Property(column.ClrType, column.Name)
    .IsRequired(false);
if (column.ClrType == typeof(string)) { ... HasMaxLength(512) ... }
```

The plain `Property(Type, string)` overload creates a shadow property — no CLR field needed on
`AuditLog`. The column shows up in the `AuditLog` table with the consumer's name and the
provided type.

### 4.5 Interceptor wiring — sync path

After `ctx.Add(auditLog)` succeeds, for each registered custom column:

```csharp
try
{
    var value = column.Provider(auditColumnContext);
    ctx.Entry(auditLog).Property(column.Name).CurrentValue = value;
}
catch (Exception ex)
{
    auditLog.Error = (auditLog.Error is null ? "" : auditLog.Error + "; ")
        + $"AddColumn '{column.Name}': {ex.Message}";
    // column stays NULL by default
}
```

Provider failures never break the save — they degrade to `NULL` on that column plus an
`Error` annotation, mirroring how diff failures are handled today.

### 4.6 Interceptor wiring — async path

The async branch builds an `AuditCaptureQueueEntry` instead of an `AuditLog`. For each
registered custom column, the interceptor invokes the provider and accumulates the results
into a `JsonObject`. The serialised value (or empty object) is written to the queue row's new
`CustomColumnsJson` column.

```csharp
JsonObject customs = new();
foreach (var column in customColumns)
{
    try
    {
        var value = column.Provider(auditColumnContext);
        customs[column.Name] = value is null ? null : JsonValue.Create(value);
    }
    catch (Exception ex) { /* same Error-annotation strategy */ }
}
queueEntry.CustomColumnsJson = customs.Count == 0 ? null : customs.ToJsonString();
```

### 4.7 Dispatcher wiring

`AuditDispatcher.BuildAuditLog` deserialises the queue row's `CustomColumnsJson`. For each
registered custom column it converts the `JsonValue` to the column's `ClrType` (using
`System.Text.Json`'s built-in primitive deserialisation) and sets the shadow property:

```csharp
var node = row.CustomColumnsJson is null ? null : JsonNode.Parse(row.CustomColumnsJson);
if (node is JsonObject customs)
{
    foreach (var column in customColumns)
    {
        if (customs[column.Name] is JsonValue v)
        {
            var clr = v.Deserialize(column.ClrType, JsonSerializerOptions.Default);
            ctx.Entry(auditLog).Property(column.Name).CurrentValue = clr;
        }
    }
}
```

Names registered after queue rows were written (a config change between deploy and dispatch)
are simply absent from the JSON and stay `NULL`. Names present in the JSON but no longer
registered are ignored — forward-compatible drift.

### 4.8 Migration responsibility

Adding `AddColumn` requires:

- One EF migration per column added to `OrionAudit_Log`.
- One **one-time** migration adding `CustomColumnsJson` to `OrionAudit_Capture_Queue` —
  required when adopting v0.6.0 regardless of whether `AddColumn` or `UseAsyncCapture` is used
  (the column is always mapped; it stays NULL when empty, same precedent as `SnapshotCursor`).

The README's "Migration from v0.5.0" subsection makes this explicit and shows the
`migrationBuilder.CreateIndex(...)` snippet a consumer typically pairs with their first
`AddColumn`.

### 4.9 Read-side surface

- LINQ: `db.AuditLog().Where(a => EF.Property<int>(a, "WorkflowStepId") == 3)`.
- View model: `AuditEntryView.CustomColumns` (`IReadOnlyDictionary<string, object?>`) maps
  column name → boxed value, sourced from the audit row via `EF.Property<object?>(...)` per
  registered column.
- Viewer SPA: each entry's head renders the non-null custom columns as small `key=value`
  badges next to the action label.

## 5. `AuditImportBuilder` — bulk import

### 5.1 Public API

```csharp
var import = db.CreateAuditImport(o => o
    .BatchSize(1000)                          // default 1000
    .ImportBatch("legacy-orders-2026"));      // REQUIRED — drives idempotency

import.Add<Order>(e => e
    .Key(legacy.OrderId)
    .Action(AuditAction.Updated)
    .Before(oldState)                         // null → Insert
    .After(newState)                          // null → Delete
    .By("u-123", "Legacy User")
    .At(legacy.ChangedAtUtc)
    .Tenant("t-1")
    .SourceId(legacy.RowId)                   // stable per-record dedup key
    .WithColumn("WorkflowStepId", legacy.StepId));

ImportResult result = await import.SaveAsync(ct);
// result.Written, result.Skipped, result.DeadLettered
```

`db.CreateAuditImport` is an `IServiceProvider`-free extension on `DbContext`; the builder
captures the context, an `AuditImportOptions`, the registered `IAuditConfiguration`, and the
optional `JsonSerializerContext`. `SaveAsync` may be called multiple times on the same builder
to resume after a partial failure — idempotency (§5.3) makes re-execution safe; previously
written rows are reported as `Skipped`. Records `Add`ed after a `SaveAsync` call are written
on the next `SaveAsync`. The builder is not thread-safe — one importer per thread.

### 5.2 Diff and snapshot production

`Before`/`After` entity instances flow through `SnapshotBuilder.Build` (the same path the
synchronous capture uses) — including `[Hashed]` / `[Redacted]` / `[NotAuditable]` rules. The
diff is `Json6902.Compute`. For Delete / SoftDelete actions `Snapshot` is populated from the
before/after node exactly as the capture path does; for periodic-snapshot-policy semantics the
importer does **not** evaluate `SnapshotPolicy` — imported rows do not advance the
`SnapshotCursor`. Rationale: import is historical replay, not new change traffic; mixing
imported rows into the snapshot cadence would distort the policy.

### 5.3 Idempotency — no schema change

`ImportBatch(tag)` is mandatory; `SaveAsync` without it throws `InvalidOperationException`.

Each written row's `CorrelationId` is stamped `import:{tag}#{sourceId}`. (When `SourceId` is
not provided, the format is `import:{tag}` and idempotency only protects against re-running
the *entire* batch — repeat-safe but not partial-resume.)

Before writing any batch, the importer queries:

```csharp
var existing = await db.AuditLog()
    .Where(a => a.CorrelationId != null
              && a.CorrelationId.StartsWith($"import:{tag}#"))
    .Select(a => a.CorrelationId!)
    .ToListAsync(ct);
var existingKeys = existing.ToHashSet(StringComparer.Ordinal);
```

For each record about to be written, the importer skips it (counts toward `Skipped`) if its
expected `CorrelationId` is in `existingKeys`. Re-running an import after a partial failure
resumes from where it stopped; running it twice cleanly produces `Written = N` then
`Written = 0, Skipped = N`.

### 5.4 Batched transactional write

`SaveAsync` partitions the records into `BatchSize` chunks. Each batch is one transaction:
`ctx.AddRange(auditLogs); await ctx.SaveChangesAsync(ct);`. A failure in one batch leaves the
prior batches committed; the consumer re-runs `SaveAsync` (idempotency will skip them).

### 5.5 Async-capture interaction

The importer **always writes `AuditLog` rows directly**, bypassing the capture queue, in both
sync and async modes. Import is bulk historical data; routing it through the live-traffic
dispatcher adds latency without value. The capture queue stays empty of imported rows; the
queue-depth gauge keeps reflecting live traffic only.

### 5.6 Custom-column values

`WithColumn(name, value)` accepts any registered custom column's name. The value is set
directly on the produced `AuditLog`'s shadow property (no provider invocation — import has no
`Entity`/`Entry`/etc. to feed `AuditColumnContext`). Unset columns stay `NULL`. A
`WithColumn` call referencing an unregistered column throws `OrionAuditConfigurationException`
at `Add` time so the consumer learns immediately.

### 5.7 Per-record failure handling

A record whose `SnapshotBuilder`/`Json6902` step throws produces an `AuditLog` row with
`Error` set and `Diff = "[]"` — identical to the capture path's failure semantics. The row
counts toward `result.DeadLettered`, not `result.Written`. Batches do not abort on a single
bad record.

### 5.8 Importer semantics summary

| Property | Value |
| - | - |
| Transactional scope | One transaction per batch |
| Idempotency | Per-record when `SourceId` set; per-batch otherwise |
| Async-mode behaviour | Bypasses queue, writes `AuditLog` directly |
| Snapshot policy | Not evaluated for imported rows |
| Tenant filter | Honoured — importer writes rows tagged with the supplied `Tenant(...)` value |
| Re-entrant | Yes — repeated `SaveAsync` with same `ImportBatch` tag is safe |

## 6. Telemetry

Version bump: `OrionAudit` `ActivitySource` / `Meter` → `0.6.0`.

New instruments for the importer (live capture/dispatch instruments unchanged):

- `OrionAudit.Import` activity per `SaveAsync` invocation.
- `orionaudit.import.rows_written` counter.
- `orionaudit.import.rows_skipped` counter.
- `orionaudit.import.rows_deadlettered` counter.
- `orionaudit.import.batch.duration` histogram.

No new instruments for `AddColumn` — custom-column writes are part of existing
`orionaudit.entries.written` / `orionaudit.dispatch.rows_processed` paths and don't merit
their own counter.

## 7. Read-side surface

Two additions to existing read APIs:

- `AuditEntryView.CustomColumns` (new property) — `IReadOnlyDictionary<string, object?>`,
  populated by `AuditViewRenderer` by looking each registered custom column up on the source
  row via `EF.Property<object?>(row, column.Name)`. Renderer takes an
  `IAuditConfiguration` so it knows which columns to project.
- Viewer SPA shows non-null custom columns as inline badges in each entry's head; the JSON
  shape returned by `/api/log` and `/api/{entityType}/{key}` includes the `customColumns`
  field. `/api/meta` gains a `customColumnNames` list so the SPA can render a column-filter
  pill row (deferred to a follow-up if it makes the SPA too busy).

## 8. Testing

**Core unit (`Moongazing.OrionAudit.Tests`):**

- `AddColumn` rejects unsupported types at registration with a clear exception.
- `AddColumn` rejects duplicate names.
- `AddColumn` registered → `AuditLog` model has the shadow property with the right CLR type
  and `IsNullable = true`.
- Provider returning `null` → column persisted as `NULL`.
- Provider throwing → row written with `Error` annotation, column `NULL`.
- `AuditImportBuilder`: `ImportBatch` missing → `InvalidOperationException` at `SaveAsync`.
- `AuditImportBuilder`: `BatchSize < 1` → `ArgumentOutOfRangeException`.
- `AuditImportBuilder.WithColumn` referencing an unregistered column → exception at `Add`.

**Integration (`Moongazing.OrionAudit.IntegrationTests`, real SQLite):**

- End-to-end: sync mode — `AddColumn` value flows from provider into queryable shadow
  property, verifiable via `EF.Property<int>(...)`.
- End-to-end: async mode — same scenario, additionally asserts the value round-trips through
  `CustomColumnsJson` and lands on the final `AuditLog`.
- Provider invoked with the right `AuditColumnContext` (Entity, Entry, Action, User, TenantId
  asserted via a recording provider).
- `AuditImportBuilder` produces an `AuditLog` whose `Diff` is byte-equal to what the
  capture path produces for the same before/after. (The contract guard from §3.)
- `AuditImportBuilder` idempotency: re-running `SaveAsync` with the same `ImportBatch` tag
  writes zero rows the second time and reports them all as `Skipped`.
- `AuditImportBuilder` bypasses the queue when async-capture is enabled (queue stays empty;
  `AuditLog` count equals `Written`).
- `AuditImportBuilder` per-record failure dead-letters one record without aborting the batch.

**Viewer (`Moongazing.OrionAudit.Viewer.Tests`):**

- `/api/log` response contains a `customColumns` object per entry when columns are registered.

**Testing helpers (`OrionAudit.Testing`):** unchanged for v0.6.0; the existing
`AuditAssertions` cover the new shapes via the standard `HaveLogged<T>` / `NotHaveLogged<T>`
surface (custom columns are queryable via `EF.Property<>` on the captured rows).

## 9. Versioning & metadata

- `Directory.Build.props`: `<Version>0.6.0</Version>`.
- `OrionAuditTelemetry` `ActivitySource` / `Meter` version → `0.6.0`.
- TFMs unchanged: `net8.0;net9.0;net10.0` for libraries; `net10.0` for tests/samples/bench.
- No new packages → CI publish matrix unchanged.

## 10. Documentation

- `CHANGELOG.md` — new `## [0.6.0] - 2026-05-24` section: Added (AddColumn, Import,
  AuditEntryView.CustomColumns, telemetry); Changed (`AuditLogEntityTypeConfiguration` reads
  custom columns from DI; `OrionAudit_Capture_Queue` gains `CustomColumnsJson`); Migration
  from v0.5.0.
- `ROADMAP.md` — v0.6.0 → *(shipped)*; release-cadence table updated; v0.7.0 stays *(planned,
  Q4 2026)* with outbox + TPH.
- `ECOSYSTEM.md` — OrionAudit row → v0.6.0, headline mentions "extensible columns + import".
- `README.md` — new "What's new in v0.6.0" section: `AddColumn` snippet with LINQ filter
  example, `AuditImportBuilder` snippet with the idempotency note, and the migration
  callout.

## 11. Release

Commit the implementation on a `worktree-orionaudit-v0.6.0` branch (per the v0.5.0 workflow),
merge into `master`, tag `v0.6.0`, push. CI runs `build-and-test` + `aot-publish-check` on
the push; the existing publish matrix already covers all packages — no CI changes.

## 12. Migration from v0.5.0

- **Synchronous consumers not using `AddColumn` or `AuditImportBuilder`:** no code change.
- **Schema:** adopting v0.5.0 → v0.6.0 requires one EF migration that adds the
  `OrionAudit_Capture_Queue.CustomColumnsJson` text column (nullable). It stays NULL when
  empty; precedent set by v0.2.0's `SnapshotCursor` and v0.5.0's queue table.
- **Adopting `AddColumn`:** one EF migration per column added to `OrionAudit_Log`. Indexes
  are the consumer's choice — add via `migrationBuilder.CreateIndex(...)` if the column will
  be filtered or grouped on.
- **Adopting `AuditImportBuilder`:** opt-in, no schema impact beyond the queue-column
  migration above. `ImportBatch` is mandatory — pick a stable per-import string so re-runs
  are idempotent.
