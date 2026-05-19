# OrionAudit v0.2.0 — Design Spec

**Date:** 2026-05-19
**Status:** Draft (design); pending implementation plan
**Authors:** Tunahan Ali Ozturk
**Family:** Orion (sibling of OrionGuard)
**Predecessor:** [v0.1.0 design spec](2026-05-13-orionaudit-v0.1.0-design.md)

## 1. Goal

Make OrionAudit pleasant to live with on a real production database past the first 100k rows.
v0.1.0 nailed the happy path; v0.2.0 covers the operational concerns that surface once an
application has been in production for a quarter: composite keys, slow reconstructions over deep
history, unbounded audit-table growth, large diff JSON in narrow column types, soft-delete
semantics, and missing correlation ids for background jobs.

## 2. Scope

### In scope (v0.2.0)

1. **Composite primary key support.** Multi-column PKs serialise as a stable ordinal-ordered
   tuple stored in `AuditLog.EntityId`. Reconstruction round-trips the tuple.
2. **Periodic snapshotting policy.** Optional opt-in: write a full `Snapshot` every N changes
   (or every T elapsed) on Update rows. Reconstruction picks the most recent snapshot
   `<= asOf` and replays diffs forward from there.
3. **Retention policy.** Declarative `RetainFor(TimeSpan)` and `RetainCount(int)` on
   `OrionAuditOptions`. A hosted service performs batched deletes inside a single transaction
   per batch.
4. **Provider-aware column types.** SQL Server gets `nvarchar(max)`, Postgres gets `jsonb`, Sqlite
   gets `TEXT`, with operator-overridable column-type metadata. Indexes follow.
5. **Soft-delete-friendly capture.** Entities with EF Core query filters tagged via the new
   `[SoftDelete]` attribute (or fluent equivalent) emit `AuditAction.SoftDeleted` distinct from
   `Deleted`. Reads pick up both.
6. **Correlation override.** `AuditScope.Push(correlationId)` AsyncLocal API for ambient
   correlation id propagation in background jobs / console runners.

### Considered but not committed for v0.2.0

- **Outbox-style publish on audit write.** Ships only if a `IAuditPublishedHandler` design lands
  cleanly without forcing a queue or transactional outbox dependency on consumers. Open question
  tracked in §11.
- **Polymorphic / TPH entity handling.** Today an entity's `EntityType` is the runtime class.
  TPH base-type filtering needs explicit modelling. Deferred.

### Explicitly *not* in scope

- Snapshot **encryption**. Use database-level TDE; OrionAudit refuses to own keys.
- Cross-database aggregation. Audit lives in the consumer's DB.
- A query DSL beyond LINQ.

## 3. Architecture changes vs. v0.1.0

### 3.1 New & modified components

```
AuditConfigurationBuilder
  └─ new: SnapshotPolicy { Every(int n) | EveryDuration(TimeSpan) | Never }
  └─ new: RetentionPolicy { RetainFor(TimeSpan) | RetainCount(int) | None }
  └─ new: SoftDelete<T>(Expression<Func<T, bool>> isDeletedSelector)

AuditSaveChangesInterceptor
  ├─ modified: ExtractPrimaryKey now produces composite-key tuples
  ├─ modified: emits AuditAction.SoftDeleted when SoftDelete predicate flips to true on Update
  ├─ new: SnapshotPolicyEvaluator decides whether to populate AuditLog.Snapshot on Update
  └─ new: pulls correlation id from AuditScope.Current ?? Activity.Current?.Id

AuditReconstructor
  └─ modified: walks back to the latest Snapshot <= asOf, hydrates from it,
     then replays diffs forward instead of replaying from Insert

AuditRetentionHostedService           (new)
  └─ background BackgroundService; deletes rows past the configured retention window
     in batches with a tunable delay between batches.

AuditScope                             (new, public)
  └─ AsyncLocal<string?> ambient correlation id; Push returns a disposable for
     scope-based usage.

Provider hints
  └─ AuditLogEntityTypeConfiguration learns about IRelationalDatabaseProviderInfoService
     (or similar EF Core 9/10 surface) and emits provider-specific column types.
```

### 3.2 Composite key serialisation

```
PK columns (ordinal order from EF Core metadata) → values .ToString() each →
join with "|" (URL-encode any "|" in source values).

Example: { TenantId = "acme", DocumentId = Guid("...") } →
EntityId = "acme|3b8a..."

Round trip on reconstruction: parse "|" → assign in same ordinal order.
```

`EntityId` stays `string` (no schema change). Documented format; consumers can introspect.

### 3.3 Snapshot policy decision tree

```
On Update entry:
  policy.Strategy match
    Never        → no snapshot written
    EveryNth(N)  → if (++counter % N == 0) write snapshot
    EveryT(span) → if (now - lastSnapshotForEntity >= span) write snapshot
```

Counter / last-snapshot timestamps live in the `OrionAudit_Snapshot_Cursors` companion table
(see §4). Per (EntityType, EntityId, TenantId) row.

### 3.4 Retention model

`RetainFor(TimeSpan)` deletes rows with `OccurredOnUtc < (now - span)`.
`RetainCount(int)` keeps the latest N rows per (EntityType, EntityId, TenantId).
Both throw `OrionAuditConfigurationException` if combined incompatibly (mutually exclusive in
v0.2; combining them is a v0.3 question once we see usage patterns).

Hosted service runs every `policy.SweepInterval` (default 1h) and deletes up to
`policy.MaxRowsPerSweep` (default 10_000) per cycle to keep transactions short.

### 3.5 Soft-delete capture

A v0.2 entity declares its soft-delete signal:

```csharp
[Auditable]
[SoftDelete(nameof(IsDeleted))]      // attribute form
public sealed class Order { ...; public bool IsDeleted { get; set; } }

// or fluent
o.Audit<Order>(b => b.SoftDelete(x => x.IsDeleted));
```

On an Update where the soft-delete predicate flips from `false → true`, the row is captured as
`AuditAction.SoftDeleted` (new enum value, byte = 3). Reconstruction treats `SoftDeleted` like
`Deleted` (returns `null`).

## 4. New schema additions

### 4.1 `OrionAudit_Snapshot_Cursors`

```
CREATE TABLE OrionAudit_Snapshot_Cursors (
    EntityType        nvarchar(512) NOT NULL,
    EntityId          nvarchar(128) NOT NULL,
    TenantId          nvarchar(128) NULL,
    UpdatesSinceLast  int NOT NULL DEFAULT 0,
    LastSnapshotUtc   datetime NULL,
    PRIMARY KEY (EntityType, EntityId, TenantId)
);
```

Read/written inside the interceptor's transaction.

### 4.2 `AuditAction` enum additions

```csharp
public enum AuditAction : byte
{
    Inserted    = 0,
    Updated     = 1,
    Deleted     = 2,
    SoftDeleted = 3,  // new in v0.2
}
```

Forward-compatible: v0.1.0 readers see an unknown byte for new rows and should still display the
diff/snapshot. Downgrade is one-way (v0.1.0 can't write `SoftDeleted`).

### 4.3 Snapshot column behavior

`AuditLog.Snapshot` is now populated on:

- `Deleted` (unchanged from v0.1.0)
- `SoftDeleted` (new)
- `Updated` rows where `SnapshotPolicy` matched

Existing nullable column; no schema migration needed for the audit table itself. Only the
cursor table is new.

## 5. Public API additions

### 5.1 Configuration

```csharp
services.AddOrionAudit<AppDb>(o => o
    .Audit<Order>(b => b.SoftDelete(x => x.IsDeleted))
    .SnapshotEvery(50)                       // OR .SnapshotEvery(TimeSpan.FromMinutes(15))
    .RetainFor(TimeSpan.FromDays(180))       // mutually exclusive with RetainCount
    .RetentionSweepInterval(TimeSpan.FromHours(1)));
```

### 5.2 Correlation scope

```csharp
using (AuditScope.Push(jobId.ToString()))
{
    await processor.RunAsync(); // every SaveChanges inside gets CorrelationId = jobId
}
```

`AuditScope.Current` returns the current ambient id (or null). Interceptor uses
`AuditScope.Current ?? Activity.Current?.Id`.

### 5.3 Composite-key reconstruction

No API change. `ReconstructAsync<Order>(entityId, asOf)` accepts the same `string` shape; for
composite keys, callers either pass the canonical `"key1|key2"` form or use the new helper:

```csharp
var id = AuditKey.From("acme", documentId);
var order = await reconstructor.ReconstructAsync<Order>(id, asOf);
```

`AuditKey.From(params object[])` performs the same join the interceptor uses.

### 5.4 Retention diagnostics

`OrionAuditTelemetry` gains:

| Signal                              | Type      | Description                                |
| ----------------------------------- | --------- | ------------------------------------------ |
| `OrionAudit.Retention.Sweep`        | Activity  | One span per sweep cycle                   |
| `orionaudit.retention.rows_deleted` | Counter   | Rows hard-deleted by the sweep             |
| `orionaudit.retention.sweep.duration` | Histogram | Sweep duration in milliseconds           |
| `orionaudit.snapshots.written`      | Counter   | Snapshots written by the periodic policy   |

## 6. Lifetimes & threading

- `AuditScope` uses `AsyncLocal<string?>` — flow safely across `await` boundaries.
- `AuditRetentionHostedService` is registered as a singleton `IHostedService`, runs on a single
  task, no per-tenant fanout (sweep is tenant-agnostic by design — the WHERE clause includes
  tenant only when retention is configured per-tenant in v0.3).
- Snapshot cursors are read with `UPDATE ... OUTPUT` (SQL Server) or `SELECT ... FOR UPDATE`
  (Postgres) where supported; Sqlite uses an explicit transaction with `BEGIN IMMEDIATE`.
  Cursor contention is acceptable: missing the policy fence by a single tick produces one extra
  snapshot, not a correctness bug.

## 7. Migration from v0.1.0

- **No code changes required** for v0.1.0 consumers who only used single-column PKs and didn't
  enable any of the new options.
- **Schema migration** is needed only if `SnapshotEvery` is enabled — the `OrionAudit_Snapshot_Cursors`
  table is created via a new EF migration template documented in the v0.2 README.
- `AuditAction.SoftDeleted` is a new enum value; readers compiled against v0.1.0 should treat
  unknown values gracefully (existing code uses pattern-matching switches with `_ => ...`
  fallbacks, which keep working).
- `ExtractPrimaryKey` no longer throws on composite PKs. Callers who relied on the old throw to
  detect misconfiguration must migrate to a startup-time validation step (offered as
  `services.ValidateOrionAuditConfiguration()`).

## 8. Performance characteristics

- **Reconstruction with snapshots:** O(K) where K = updates since the last snapshot. For an
  entity with 5_000 historical changes and `SnapshotEvery(100)`, reconstruction reads 1 snapshot
  + up to 99 diffs vs. 5_000 diffs in v0.1.0.
- **Capture overhead per save:** unchanged for the common path; +1 cursor read on Update
  (single-row index seek) when `SnapshotEvery` is enabled.
- **Retention sweep:** bounded by `MaxRowsPerSweep`; uses `WHERE OccurredOnUtc < @cutoff`
  followed by `DELETE TOP (@batch)` on SQL Server / `DELETE ... LIMIT` on Postgres / batched
  delete on Sqlite. Index `IX_OrionAudit_TenantTimeline` already covers the predicate.

## 9. Definition of Done

- All v0.1.0 tests still pass unchanged.
- 25+ new tests covering each v0.2.0 feature in isolation + 4 integration tests against Sqlite
  end-to-end (composite key, snapshot-policy reconstruction, retention deletes, soft-delete
  capture).
- Release build clean across net8/9/10.
- 3 NuGet packages updated to v0.2.0 (PackageId unchanged: `OrionAudit`, `OrionAudit.AspNetCore`,
  `OrionAudit.Testing`).
- Sample app extended with a "v0.2 features tour" section.
- Benchmarks updated: new `SnapshotPolicyReconstructionBench` shows the O(K) win.
- CHANGELOG entry under `[0.2.0]`.
- ROADMAP updated: v0.2 items marked *Shipped*.

## 10. Test plan

| Area                | Cases                                                                   |
| ------------------- | ----------------------------------------------------------------------- |
| Composite PK        | 2-column, 3-column, with Guid + string, with int + DateTime             |
| Snapshot policy     | EveryN matches; EveryT matches; reconstruction picks correct snapshot   |
| Retention           | RetainFor deletes; RetainCount keeps latest N; sweep respects batch cap |
| Soft delete         | flip true → SoftDeleted row; un-flip true → false → new Update row      |
| Correlation scope   | nested scopes restore parent; async flow preserves value across awaits  |
| Provider hints      | Postgres jsonb mapping, SQL Server nvarchar(max), Sqlite TEXT           |
| Telemetry           | retention sweep activity emitted; counters incremented                  |
| Migration safety    | v0.1.0 rows readable by v0.2.0 readers (round-trip)                     |

## 11. Open questions

- **Outbox publish hook.** Two designs on the table: (a) a synchronous
  `IAuditPublishedHandler.OnAuditRowAddedAsync` invoked inside the interceptor's transaction;
  (b) an `IAuditOutboxSink` that buffers rows for a follow-up `IHostedService` to drain. (a) is
  simpler but couples consumers to interceptor timing; (b) is more flexible but introduces an
  at-least-once delivery story we'd rather not own. Decision: ship neither in v0.2, gather
  feedback after release.
- **Per-tenant retention.** Should `RetainFor` accept a per-tenant override? Useful for SaaS
  apps with mixed compliance regimes. Probably v0.3; reach decision after first v0.2 adopters.
- **Snapshot compaction.** Should we delete the diffs between two consecutive snapshots? Saves
  storage but loses fine-grained history. Strongly leaning *no* — diffs are the value
  proposition. Document the trade-off, leave the choice to operators via retention.
