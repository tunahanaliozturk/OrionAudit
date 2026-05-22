# OrionAudit v0.5.0 — Design Spec

**Date:** 2026-05-22
**Status:** Approved — ready for implementation planning.
**Authors:** Tunahan Ali Ozturk
**Family:** Orion (sibling of OrionGuard)
**Predecessors:** [v0.1.0][s1] / [v0.2.0][s2] / [v0.3.0][s3] / [v0.4.0][s4]

[s1]: 2026-05-13-orionaudit-v0.1.0-design.md
[s2]: 2026-05-19-orionaudit-v0.2.0-design.md
[s3]: 2026-05-19-orionaudit-v0.3.0-design.md
[s4]: 2026-05-20-orionaudit-v0.4.0-design.md

## 1. Goal

**Theme: Throughput & visibility.** Two complementary deliverables — one *felt*, one *seen*:

1. **Async staging-capture** — an opt-in capture mode that moves the heavy diff/snapshot work
   off the consumer's `SaveChanges` transaction and onto a background dispatcher, without
   losing the lossless, atomic-with-the-data-change guarantee that an audit library exists to
   provide. This is the *felt* deliverable: a measurably lighter write path under load.
2. **`OrionAudit.Viewer`** — a self-contained, framework-agnostic companion package that
   renders the audit trail through a single endpoint registration. This is the *seen*
   deliverable: a feature with a screenshot, the thing that markets the release.

The release pairs an invisible performance win with a visible, demo-able feature on purpose.
A purely under-the-hood release is technically sound but commercially thin; a viewer alone
does not differentiate OrionAudit from the audit libraries that already have one. Together
they give v0.5.0 both a benchmark chart and a screenshot for its README.

### 1.1 Why this theme — the measurement

`InterceptorBench` (in-memory SQLite, .NET 10) was run to decide the theme with data rather
than intuition. Audit capture is not noise:

| Scenario     | Batch | No audit  | With audit | Ratio | Alloc ratio          |
| ------------ | ----- | --------- | ---------- | ----- | -------------------- |
| `SaveChanges`| 1     | 141 µs    | 314 µs     | 2.24× | 1.33× (71 → 95 KB)   |
| `SaveChanges`| 10    | 434 µs    | 1 160 µs   | 2.69× | 2.37× (140 → 332 KB) |
| `SaveChanges`| 100   | 1 855 µs  | 6 102 µs   | 3.29× | 3.31× (817 KB → 2.7 MB) |

Reading the numbers honestly:

- **The cost is real.** Capture is 2.2–3.3× the bare `SaveChanges` time and triples managed
  allocation at batch 100 (2.7 MB per 100-row save).
- **The ratio is the pessimistic end.** In-memory SQLite makes the baseline trivially fast.
  On a real SQL Server / Postgres the baseline carries a network round-trip, so capture's
  fixed CPU cost is a smaller *fraction* — likely 1.2–1.5×, not 3×.
- **The allocation ratio is DB-independent** and therefore the portable signal: capture
  triples managed allocation regardless of provider.
- **Absolute numbers are small.** A single audited save is ~314 µs; in a typical CRUD app
  behind a real DB this is lost in latency. The cost is *felt* in write-heavy / batch-heavy
  systems — which is exactly who the async mode targets.

The overhead is almost entirely the **movable kind** — snapshot JSON building, diff
computation, the `AuditLog` object graph, and (on a real DB) the `SnapshotCursor` lookup
query — not the unavoidable INSERT. That is what makes deferring it a legitimate feature
rather than a micro-optimisation.

## 2. Scope

### In scope (v0.5.0)

1. **Async staging-capture** — opt-in `OrionAuditOptions.UseAsyncCapture(...)`; a
   `OrionAudit_Capture_Queue` companion table; an `AuditDispatcherHostedService` background
   dispatcher. Default behaviour stays synchronous and byte-for-byte identical to v0.4.0.
2. **`OrionAudit.Viewer`** — a new NuGet package: `MapOrionAuditViewer<TDbContext>()` endpoint
   serving a JSON API plus an embedded static single-page UI. No Blazor dependency.
3. **Audit view render core** — `AuditEntryView` / `FieldChange` / `AuditViewRenderer` in core
   `OrionAudit` under `Read/`. Turns an `AuditLog` row + RFC 6902 diff into a structured,
   human-readable view model. Consumed by the Viewer.
4. **`IAuditDispatcher` + `FlushPendingAsync`** — a small read/control surface for forcing the
   queue to drain (tests, "I need it now" call sites).
5. **Telemetry, benchmark, version, docs** — dispatch instrumentation, an async arm on
   `InterceptorBench`, version bump to `0.5.0`, CHANGELOG / ROADMAP / ECOSYSTEM / README.

### Considered but not committed for v0.5.0 (deferred to v0.6.0)

- **Extensible `AuditLog` row (`AddColumn<T>`).** Real EF shadow-property columns for custom
  indexable dimensions. A complete feature in its own right; shipping it polished alongside
  two other large items would compromise all three. → v0.6.0.
- **Legacy import (`AuditImportBuilder`).** Fluent bulk-import of hand-rolled change history
  as synthetic `AuditLog` rows. → v0.6.0.
- **CLI diff renderer.** A `dotnet tool` was on the v0.5.0 roadmap; with the Viewer covering
  "see the audit trail", a type-agnostic CLI adds marginal value against real cost (three EF
  provider packages, a separate tool publish path). Moved to ROADMAP *Considered*.

### Explicitly not in scope

- **A pure in-process queue.** An in-memory channel would improve throughput but discard the
  one guarantee an *audit* library exists to provide: that no audited change is ever silently
  lost. See §3.
- **Blazor components.** The Viewer must drop into any ASP.NET Core host regardless of its UI
  stack; a Razor Class Library would only work in a Blazor host. See §7.
- **Read-side `includePending` projection.** Surfacing un-dispatched queue rows as synthetic
  audit entries in `AuditFor<T>()` — rejected as unnecessary complexity. See §6.

## 3. The load-bearing constraint — atomic, lossless capture

OrionAudit's foundational promise (v0.1.0 onward) is that an `AuditLog` row is written **in
the same transaction** as the change it records. An audit trail that can silently drop
entries is worthless for compliance and forensics — worse than a slow one.

Async capture must not weaken this. The design therefore splits the guarantee in two:

- **Capture stays atomic and lossless.** The `OrionAudit_Capture_Queue` row is written in the
  *same transaction* as the originating data change. If the process crashes immediately after
  commit, the queue row is already durable on disk. No audited change is ever lost.
- **Only materialisation is deferred.** Diff computation and the final `AuditLog` row appear
  after a short, bounded delay. Audit becomes **eventually consistent**, not lossy.

Because this changes observable semantics — "the audit row is ready when `SaveChanges`
returns" becomes "the audit row is ready shortly after" — async capture is **strictly
opt-in**. A consumer that never calls `UseAsyncCapture` gets the v0.4.0 synchronous path,
unchanged.

## 4. Async staging-capture — architecture

```
SaveChanges  (consumer transaction)
  └─ AuditSaveChangesInterceptor
       async mode → write one OrionAudit_Capture_Queue row per audited entity
                    in the SAME transaction  (no diff, no AuditLog graph)
              │
              │  transaction commits
              ▼
  AuditDispatcherHostedService  (background, off the hot path)
       poll queue → compute diff → build AuditLog row → insert AuditLog
                  → delete queue row     (all in one transaction per batch)
```

### 4.1 The hot-path / deferred split

A security constraint dictates the split. `[RedactedAudit]` and `[HashedAudit]` fields must
**never** be persisted in raw form, and the queue table is in the database. Therefore
redaction and hashing **cannot be deferred** — they must be applied before any row is
written. Snapshot serialisation, which applies those rules, stays on the hot path.

**Stays on the hot path (inside the consumer transaction):**

- Enumerate audited `ChangeTracker` entries (already cheap — a state check + a
  `FrozenDictionary` lookup).
- For each entry, build the before/after state as JSON via `SnapshotBuilder` **with all
  hash / redact / exclude rules applied** — identical to today's synchronous path.
- Write one `OrionAudit_Capture_Queue` row carrying `BeforeJson`, `AfterJson`, `EntityType`,
  `EntityId`, `Action`, `UserId` / `UserDisplay` / `UserType`, `TenantId`, `CorrelationId`,
  and `OccurredOnUtc`.

**Deferred to the dispatcher:**

- `DiffEngine.Compute(before, after)` — the diff CPU and the `Diff` string allocation.
- The `AuditLog` object graph, its EF change-tracking, and the `AuditLog` INSERT.
- The periodic-snapshot policy, including the `SnapshotCursor` lookup — today a DB query
  executed *inside the consumer's transaction* on every updated entity. Deferring it is a
  real latency win on a production DB that the in-memory SQLite benchmark cannot show.

**Honest accounting of the win.** Snapshot serialisation remains on the hot path, so the
saving is *partial* — what moves off is diff-compute, `AuditLog` change-tracking, and the
snapshot-cursor IO. The exact split will be measured during implementation by adding a third
arm to `InterceptorBench` (§9); the README's benchmark figure is sourced from that arm, not
from a projection.

### 4.2 `OrionAudit_Capture_Queue` table

New entity `AuditCaptureQueueEntry` and `AuditCaptureQueueEntityTypeConfiguration`, mapped by
`ApplyOrionAuditConfigurations` exactly as `SnapshotCursor` is (v0.2.0 precedent). Columns:

| Column          | Purpose                                                              |
| --------------- | -------------------------------------------------------------------- |
| `Id`            | Surrogate PK; also the dispatch order key.                           |
| `EntityType`    | `AssemblyQualifiedName`, as on `AuditLog`.                           |
| `EntityId`      | Serialised primary key (single or composite, `AuditKey` format).     |
| `Action`        | `AuditAction` byte.                                                  |
| `BeforeJson` / `AfterJson` | Rule-applied state snapshots (the dispatcher diffs these). |
| `UserId` / `UserDisplay` / `UserType` / `TenantId` / `CorrelationId` | Attribution, captured at write time. |
| `OccurredOnUtc` | The originating change's timestamp — copied verbatim onto the final `AuditLog` row so reconstruction order is correct regardless of dispatch order. |
| `Attempts`      | Dispatch attempt counter; drives dead-lettering.                     |
| `Error`         | Null until dead-lettered; then the failure detail (parity with `AuditLog.Error`). |
| `ClaimToken` / `ClaimedUtc` | Multi-instance claim fields (§4.4).                      |

The table is mapped unconditionally. When `UseAsyncCapture` is not called it simply stays
empty — harmless, the same way `OrionAudit_Snapshot_Cursors` is harmless when periodic
snapshotting is off.

### 4.3 Dispatcher — `AuditDispatcherHostedService`

Follows the `AuditRetentionHostedService` pattern (v0.2.0). Every `PollInterval` it processes
up to `BatchSize` queue rows.

**Exactly-once via a single transaction.** Each batch runs in one transaction: claim N queue
rows ordered by `Id`, compute their diffs, build and insert the `AuditLog` rows, delete the
processed queue rows. Insert and delete commit together — if the transaction commits, the
`AuditLog` rows exist and the queue is cleared atomically; if the process crashes mid-batch,
the transaction rolls back, the queue rows survive, and the next poll reprocesses them. No
`AuditLog` row is ever written twice.

**Failure isolation.** A single malformed queue row must not block its batch. On a per-row
exception the dispatcher increments `Attempts`; once `Attempts` reaches `MaxAttempts` the row
is **dead-lettered** — `Error` is set, the row is left in place but skipped by future polls,
and the event is surfaced via telemetry and logging. The row is never silently dropped. This
mirrors the synchronous path setting `AuditLog.Error` on a capture failure.

### 4.4 Multiple application instances

Several app instances mean several dispatchers competing for the same queue. To stop two
dispatchers processing the same rows, the dispatcher **claims** a batch before working it: a
transactional `UPDATE ... SET ClaimToken = @token, ClaimedUtc = @now` over the next N
unclaimed rows, then it processes only rows bearing its token. The claim is provider-aware —
`SELECT ... FOR UPDATE SKIP LOCKED` semantics on SQL Server / Postgres, single-writer on
SQLite. A claim older than a configurable lease is considered abandoned and may be re-claimed,
so a crashed instance's rows are not stranded.

### 4.5 Configuration surface

```csharp
o.UseAsyncCapture(q =>
{
    q.PollInterval(TimeSpan.FromSeconds(2));   // default 2s
    q.BatchSize(500);                          // default 500
    q.MaxAttempts(5);                          // default 5, then dead-letter
    q.ClaimLease(TimeSpan.FromMinutes(5));     // default 5m, abandoned-claim reclaim window
});
```

Calling `UseAsyncCapture` registers the `AuditDispatcherHostedService` and flips the
interceptor to async mode. Omitting it leaves the synchronous v0.4.0 path in place.

## 5. `IAuditDispatcher` and forced drain

A small control surface, registered only in async mode:

- `IAuditDispatcher.FlushPendingAsync(CancellationToken)` — synchronously drains the queue to
  completion (claim, diff, write, delete until empty). Intended for integration tests and for
  the rare call site that must observe the audit row immediately after a write. In synchronous
  mode the implementation is a no-op so test code can call it unconditionally.
- `IAuditDispatcher.GetQueueDepthAsync(CancellationToken)` — count of un-dispatched rows;
  feeds the Viewer's "pending" indicator (§7) and is also useful for health checks.

## 6. Read-side consistency

In async mode `AuditFor<T>()` and `AuditLog()` see only **dispatched** `AuditLog` rows. Code
that reads immediately after a write may miss the most recent change until the dispatcher
catches up. This is the defining property of the eventually-consistent model, not a bug:

- **It is documented**, plainly, as the contract of async mode.
- **`FlushPendingAsync`** (§5) is the escape hatch for call sites that need read-after-write.
- **The read API stays unchanged.** No `includePending` flag — projecting queue rows into
  synthetic `AuditEntryView`s would duplicate the dispatcher's diff logic and complicate the
  read surface for a marginal case (YAGNI).
- **The Viewer** shows the current queue depth so an operator can see the dispatch lag.

## 7. `OrionAudit.Viewer`

A new NuGet package, `OrionAudit.Viewer` (CLR namespace `Moongazing.OrionAudit.Viewer`).
It uses the `Microsoft.AspNetCore.App` framework reference. **No Blazor dependency** — a
companion package must not constrain the consumer's UI stack, and a Razor Class Library would
only work inside a Blazor host. The Viewer drops into any ASP.NET Core application — MVC,
Razor Pages, minimal API — through one endpoint registration.

### 7.1 Registration

```csharp
app.MapOrionAuditViewer<AppDbContext>("/audit", o =>
{
    o.RequireAuthorization("AuditViewers");   // an authorization policy name
    // o.AllowAnonymous();                    // explicit opt-out, "dev only" in the docs
});
```

`MapOrionAuditViewer` is an `IEndpointRouteBuilder` extension. `TDbContext` names the context
the audit data is read from.

### 7.2 JSON API

Endpoints mounted under the supplied path prefix:

- `GET {prefix}/api/log?page=&size=&entityType=` — paged recent audit rows as `AuditEntryView`.
- `GET {prefix}/api/{entityType}/{key}` — one entity's timeline: chronologically ordered
  `AuditEntryView`s, each carrying its before/after `FieldChange` list.
- `GET {prefix}/api/meta` — the audited type names (for the SPA's filters) and, in async
  mode, the current capture-queue depth (for the "N pending" indicator).

All endpoints read through the existing `AuditFor<T>()` / `AuditLog()` API, so tenant
filtering via `IAuditTenantResolver` applies automatically and unchanged.

### 7.3 Static SPA

A single `index.html` plus vanilla JS/CSS, shipped as embedded resources (no build step),
served from the path-prefix root. It renders the `FieldChange` data the render core produces
as a before/after table and a timeline. `[Redacted]` and `[Hashed]` fields are visually
marked. A small badge shows the queue depth when async capture is on.

### 7.4 Security — opt-in exposure

Audit data is sensitive. `MapOrionAuditViewer` **requires authorization by default**: absent
an explicit policy it applies a default authenticated-user requirement; the endpoint group is
returned with `.RequireAuthorization(...)` applied. A consumer who genuinely wants an open
endpoint must call `AllowAnonymous()` explicitly, and the docs flag that as dev-only.

### 7.5 Scope boundary

The Viewer is a **primitive** — a read-only timeline and diff view. No report builder, no
export, no charts. (ROADMAP states this explicitly.)

## 8. Audit view render core

New file `src/Moongazing.OrionAudit/Read/AuditView.cs`, alongside the existing read API
(`AuditFor<T>()`, `IAuditReconstructor`). It is pure — depends only on `System.Text.Json` and
the existing `Json6902` engine, with no UI or ASP.NET dependency — so it lives in core
`OrionAudit` rather than in a separate abstractions package (ECOSYSTEM §2: compose, don't
fragment). A consumer can use it to render their own UI; the Viewer is its first client.

- `AuditEntryView` — the readable view of one `AuditLog` row: `Action`, `UserDisplay`,
  `OccurredOnUtc`, `CorrelationId`, and `IReadOnlyList<FieldChange> Changes`.
- `FieldChange` — `PropertyPath`, `OldValue`, `NewValue`, `ChangeKind` (`Added` / `Removed` /
  `Modified`); fields governed by `[RedactedAudit]` / `[HashedAudit]` are flagged.
- `AuditViewRenderer` — turns `AuditLog` row(s) into `AuditEntryView`s. It parses the RFC 6902
  diff with `Json6902` and works type-agnostically (JSON-path based).

## 9. Benchmark

`InterceptorBench` gains a third arm, `SaveChanges_WithAsyncAudit`, measuring the hot-path
cost in async mode (queue-row write, no diff). The sync-vs-async hot-path delta is the source
of the README's benchmark figure. A separate `DispatcherBench` measures dispatch throughput
(rows processed per second) so the deferred cost is also quantified, not hidden.

## 10. Telemetry

`OrionAudit` `ActivitySource` / `Meter` version bumps to `0.5.0`. New instrumentation, async
mode only:

- `OrionAudit.Dispatch` activity per dispatcher batch.
- Counters `orionaudit.dispatch.rows_processed` and `orionaudit.dispatch.rows_deadlettered`.
- Histogram `orionaudit.dispatch.batch.duration`.
- `orionaudit.capture.queue_depth` (observable gauge).

The synchronous path's existing metrics (`orionaudit.entries.written`,
`orionaudit.capture.duration`, etc.) are unchanged.

## 11. Testing

**Core — async capture:**

- The capture-queue row is written in the *same transaction* as the data change — a rolled-
  back `SaveChanges` leaves no queue row.
- Dispatcher exactly-once — a simulated crash mid-batch produces no duplicate `AuditLog` rows.
- Dead-lettering — a row that fails `MaxAttempts` times gets `Error` set and is then skipped.
- `FlushPendingAsync` drains the queue to empty; the resulting `AuditLog` rows match what the
  synchronous path would have written for the same changes (byte-for-byte `Diff` parity).
- Redaction / hashing is applied to `BeforeJson` / `AfterJson` *before* the queue row is
  written — no raw sensitive value ever reaches the queue table.
- Multi-instance claim — two dispatchers over one queue process disjoint row sets.

**Core — render core:**

- `AuditViewRenderer` maps `add` / `remove` / `replace` diff ops to the correct `ChangeKind`;
  redacted / hashed fields are flagged.

**`OrionAudit.Viewer`:**

- `WebApplicationFactory` endpoint tests — JSON shape of each endpoint, tenant filtering,
  authorization (no policy / unauthenticated → 401), the `meta` queue-depth field.

**`OrionAudit.Testing`:** unchanged for v0.5.0.

## 12. Versioning & metadata

- `Directory.Build.props`: `<Version>0.5.0</Version>`.
- `OrionAuditTelemetry` `ActivitySource` / `Meter` version → `0.5.0`.
- Target frameworks unchanged: `net8.0;net9.0;net10.0` for libraries (the new
  `OrionAudit.Viewer` included); single-target `net10.0` for tests / samples / benchmarks.
- `OrionAudit.Viewer` added to `OrionAudit.sln` and to the CI `build-and-test` and `publish`
  jobs.

## 13. Documentation

- `CHANGELOG.md` — a new `## [0.5.0] - 2026-05-22` section: async staging-capture (opt-in),
  `OrionAudit.Viewer`, the render core, telemetry additions. A "Migration from v0.4.0" note:
  no code change for synchronous consumers; adopting v0.5.0 requires one EF migration that
  creates the `OrionAudit_Capture_Queue` table (empty unless `UseAsyncCapture` is called —
  the `SnapshotCursor` precedent); async capture and the Viewer are both opt-in.
- `ROADMAP.md` — v0.5.0 is re-themed from "Developer Experience" to "Throughput & Visibility";
  `AddColumn` and the import helpers move to a new v0.6.0 milestone; the CLI diff renderer
  moves to *Considered*; the release-cadence table is updated.
- `ECOSYSTEM.md` — the OrionAudit row updates to v0.5.0; the package count reflects
  `OrionAudit.Viewer`.
- `README.md` — a benchmark chart (sync vs. async hot path) and a Viewer screenshot; a new
  `src/Moongazing.OrionAudit.Viewer/docs/README.md` for the Viewer package.

## 14. Release

Commit the implementation, tag `v0.5.0`, push the tag. The CI `publish` job runs on the
GitHub release event and pushes both `OrionAudit` and `OrionAudit.Viewer` to NuGet.

## 15. Migration from v0.4.0

- **Synchronous consumers:** no code change. The capture path is byte-for-byte identical.
- **Schema:** adopting v0.5.0 requires one EF migration creating `OrionAudit_Capture_Queue`.
  The table stays empty unless `UseAsyncCapture` is called.
- **Opting into async capture:** call `o.UseAsyncCapture(...)` in `AddOrionAudit`. Be aware
  that audit becomes eventually consistent — see §6 — and use `FlushPendingAsync` where
  read-after-write is required.
- **The Viewer** is a separate, optional package. Installing it changes nothing until
  `MapOrionAuditViewer` is called.
