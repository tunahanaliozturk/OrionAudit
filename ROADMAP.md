# OrionAudit Roadmap

This document lists what's shipped, what's actively planned, and what we're deliberately *not*
building. It's a planning artifact, not a contract — dates slip, priorities reshuffle. If
something here matters to you, open a GitHub issue so we can weigh it against everything else.

## Status legend

- **Shipped** — in the named release.
- **Planned** — committed to the named milestone; design is firm.
- **Considered** — interesting but unscheduled. Needs a concrete use case before we'll commit.
- **Out of scope** — explicitly declined for v1.x. The library stays small; some features belong
  in adjacent packages or in user code.

---

## v0.1.0 — Capture & Reconstruction *(shipped)*

The foundational release. Enough to deploy an audit trail in a real ASP.NET / EF Core
application today.

- EF Core `SaveChangesInterceptor` writes one `AuditLog` row per audited entity per save,
  in the same transaction as the originating change.
- JSON Patch (RFC 6902) diffs via `JsonPatch.Net`, replayable for time-travel reconstruction.
- Sensitive-field handling: `[NotAuditable]`, `[HashedAudit]`, `[RedactedAudit]` attributes
  plus fluent overrides.
- Multi-tenancy: pluggable `IAuditTenantResolver` + automatic read-side filter
  (`AuditFor<T>()`).
- User attribution: pluggable `IAuditUserResolver`; `HttpContextAuditUserResolver` in the
  AspNetCore companion.
- Time-travel: `IAuditReconstructor.ReconstructAsync` / `ReconstructManyAsync` (single-key and
  batch).
- DI surface: `AddOrionAudit<TContext>`, `UseOrionAudit(sp)`, `ApplyOrionAuditConfigurations()`.
- OpenTelemetry: `OrionAudit` `ActivitySource` + `Meter` with capture/reconstruct spans and
  counters/histograms.
- Test helpers (framework-agnostic): `AuditCapture`, fluent `AuditAssertions`, in-memory
  user/tenant resolvers.
- Multi-targets `net8.0`, `net9.0`, `net10.0`.

### Known limitations carried into v0.1.0

- Single-column primary keys only. Composite PKs throw `OrionAuditConfigurationException`.
- `AuditLog.Snapshot` is populated on Delete only. Reconstruction at any prior timestamp replays
  diffs from the Insert row forward, which is O(N) in history depth.
- No built-in retention or compaction. Operators are expected to archive `OrionAudit_Log`
  themselves.

---

## v0.2.0 — Reliability & Scale *(shipped)*

Theme: *make OrionAudit pleasant to live with on a real production database past the
first 100k rows.*

- Composite primary key support via stable ordinal-joined `AuditKey` serialisation
  (`"key1|key2|..."`, `|` percent-escaped in source values).
- Periodic snapshotting policy (`SnapshotEvery(N)` / `SnapshotEvery(TimeSpan)`) backed by the
  new `OrionAudit_Snapshot_Cursors` table; reconstruction walks back to the latest snapshot
  `<= asOf` and replays only the diffs after it — O(K) instead of O(N).
- Retention policy (`RetainFor(TimeSpan)` / `RetainCount(int)`) with the
  `AuditRetentionHostedService` background sweep, bounded by `MaxRowsPerSweep` per cycle.
- Provider-aware column hints (`OrionAuditColumnHints.SqlServerNvarcharMax`,
  `PostgresJsonb`, `SqliteText`) on `ApplyOrionAuditConfigurations`.
- Soft-delete capture via `[SoftDelete(nameof(IsDeleted))]` attribute and equivalent fluent
  `b.SoftDelete(...)`; flips false → true emit new `AuditAction.SoftDeleted` (byte = 3);
  reconstruction treats it like a hard delete.
- `AuditScope.Push(correlationId)` ambient `AsyncLocal<string?>` correlation id, preferred over
  `Activity.Current?.Id` when stamping `AuditLog.CorrelationId`. Useful for background jobs
  and console runners.

### Considered for v0.2 but not promised

- **Outbox-style event publish on audit write.** Useful for downstream replication / search
  indexers; only ships if a clear public hook design lands.
- **Polymorphic / TPH entity handling.** Today an entity's `EntityType` is the runtime class.
  TPH base-type filtering needs explicit modelling.

---

## v0.3.0 — AOT & Source-Gen *(planned)*

Target theme: *no reflection on the hot path, no `RequiresUnreferencedCode` warnings, Native
AOT-clean.*

- **Source-generated `[Auditable]` discovery.** Replaces the runtime assembly scan with a
  compile-time `partial` registration class. Trim-safe, AOT-safe, zero startup cost.
- **Source-generated JSON serialization for snapshots.** Per-entity `JsonSerializerContext`
  emitted by the generator; eliminates `JsonSerializer.SerializeToNode` reflection on the
  primitive fallback path.
- **`[RequiresUnreferencedCode]` / `[RequiresDynamicCode]` annotations** on the remaining
  reflective APIs so consumers get accurate trim diagnostics.
- **Native AOT smoke test** in CI (`PublishAot=true` on the sample console).

### Considered for v0.3

- Drop net8.0 once the source-gen lands cleanly on net9+ — depends on adoption signal.

---

## v0.4.0 — Developer Experience *(planned)*

Target theme: *seeing the audit trail should be as easy as writing it.*

- **`OrionAudit.Viewer` companion package.** Embeddable Razor Pages / Blazor component that
  renders the audit timeline for a given entity, including before/after diffs in a human-readable
  format.
- **CLI diff renderer.** `dotnet tool` that pretty-prints an `AuditLog.Diff` against a target
  type from any DB connection string.
- **Extensible `AuditLog` row.** Opt-in fluent surface for adding columns
  (`o.AddColumn<int>("WorkflowStepId", e => ...)`) so consumers can index custom dimensions
  without a fork.
- **Migration helpers.** Templates and helpers for adopting OrionAudit into a system that
  already has hand-rolled change tracking — bulk-import legacy history as synthetic `AuditLog`
  rows.

### Considered for v0.4

- **Web push notifications on audit-row write.** Could ride on the v0.2 outbox hook.
- **Per-entity / per-field UI labels** for the viewer.

---

## v1.0.0 — Stable API *(planned)*

Target theme: *commit to the surface, slow down, support it.*

- **API freeze + SemVer 2.0.0 commitment.** Public types are locked; breaking changes only on
  major version bumps from here.
- **Separate-database audit storage.** First-class `o.UseSeparateAuditDb(...)` path so the
  audit table lives on its own connection / schema / DB. Decouples primary write throughput
  from audit retention growth.
- **Strong-named assemblies.** Required by some enterprise / GAC scenarios.
- **LTS support window** — security and correctness fixes backported to v1.x for 18 months
  after v2 ships.
- **Documentation site** with API reference, recipes, and migration guides.

---

## Out of scope for v1.x

These come up in conversation; we're saying no on purpose.

- **A general-purpose event-sourcing framework.** OrionAudit captures *what changed in your
  domain model*, not a replayable command log. If you need event sourcing, use something built
  for it.
- **Cross-database / cross-system audit aggregation.** Centralising audit from N microservices
  is an infra job; OrionAudit ships one row per save in *your* DB.
- **GUI report builder / dashboard product.** The Viewer in v0.4 is a primitive component, not a
  reporting suite.
- **Replacing EF Core's change tracking.** The library lives on top of EF Core's interceptor
  surface and inherits its semantics — entities outside the change tracker (raw SQL, dapper) are
  not captured.
- **Encryption-at-rest of audit values.** Use database-level TDE or a transparent wrapping
  provider; OrionAudit refuses to own a key management story.

---

## How to influence this roadmap

- **Open an issue** describing the use case, the workaround you have today, and what success
  looks like. Concrete > abstract.
- **PRs welcome** for any *Planned* item — coordinate via the issue first so we don't double-work.
- **Sponsor the project** via GitHub Sponsors if you want to nudge priority on a specific item.
  See [`.github/FUNDING.yml`](.github/FUNDING.yml).

---

## Release cadence (rough)

| Milestone | Target window                       | Driver                       |
| --------- | ----------------------------------- | ---------------------------- |
| v0.1.x    | initial                             | bug fixes only               |
| v0.2.0    | ~1 quarter post-v0.1                | scale + composite keys       |
| v0.3.0    | ~2 quarters post-v0.1               | AOT + source-gen             |
| v0.4.0    | ~3 quarters post-v0.1               | viewer + DX                  |
| v1.0.0    | when v0.4 is stable in production   | API freeze                   |

Patch releases (`0.x.y`) ship as needed for bugs and security. Minor releases (`0.x.0`) cluster
features around the themes above and never break documented public APIs without a deprecation
cycle.
