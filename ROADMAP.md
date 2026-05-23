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

## v0.3.0 — Source Generator *(shipped)*

Theme: *replace the runtime assembly scan with a compile-time generator; make the snapshot
and reconstruct paths trim-aware.*

- Source-generated `[Auditable]` discovery via the `[OrionAuditModule]` attribute — the
  generator (shipped in the `OrionAudit` NuGet under `analyzers/dotnet/cs/`) emits
  `RegisterAuditedTypes(AuditConfigurationBuilder)` and an `AuditedTypeNames` list. No runtime
  reflection, no assembly scan.
- `OrionAuditOptions.UseJsonContext(JsonSerializerContext)` — routes `SnapshotBuilder` and
  `AuditReconstructor` through a System.Text.Json source-gen context instead of reflective
  serialisation.
- `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]` on `AuditableTypeDiscovery.Discover`
  and `OrionAuditOptions.ScanAssembly` so trim/AOT publishes flag the reflective scan path.

### Scope cut: full Native AOT deferred to v0.4

`JsonPatch.Net` — the library behind `DiffEngine` — is not AOT-compatible (`CreatePatch` and
the `JsonPatch` type's (de)serialisation carry `[RequiresDynamicCode]`). v0.3.0 removes the
*assembly-scan* and *snapshot-serialisation* reflection, but the *diff* path still relies on
it. A fully AOT-clean OrionAudit needs a hand-rolled RFC 6902 emitter — that is the v0.4 theme.

---

## v0.4.0 — AOT-Clean Diff Engine *(shipped)*

Theme: *replace `JsonPatch.Net` with a source-gen-friendly RFC 6902 emitter so the whole
capture/reconstruct path is Native-AOT clean.*

- **Hand-rolled JSON Patch engine.** A focused RFC 6902 compute/apply implementation over
  `System.Text.Json.Nodes` with no `[RequiresDynamicCode]` surface, replacing the
  `JsonPatch.Net` dependency.
- **Native AOT smoke test in CI.** The `aot-publish-check` job + AOT probe project return —
  the probe Native-AOT publishes OrionAudit's full capture/reconstruct surface and fails the
  build on any `IL2*` / `IL3*` warning.
- **`[RequiresUnreferencedCode]` cleanup.** With the diff engine AOT-safe, the remaining
  reflective fallbacks are either annotated or eliminated; `UseJsonContext` becomes the
  documented AOT path end-to-end.

### Considered for v0.4

- Drop `net8.0` once the source generator and AOT story have settled on net9+.

---

## v0.5.0 — Throughput & Visibility *(shipped)*

Theme: *make audit cheap to write under load and easy to see.*

- **Async staging-capture (`UseAsyncCapture`).** Opt-in. The interceptor writes a lightweight
  `OrionAudit_Capture_Queue` row in the consumer's transaction; a new background dispatcher
  (`AuditDispatcherHostedService`) computes the diff and writes the final `AuditLog` row
  shortly after. Capture stays atomic and lossless; audit becomes eventually consistent under
  this mode. Dispatch is exactly-once via single-transaction insert + delete; a malformed row
  is dead-lettered after `MaxAttempts`.
- **`OrionAudit.Viewer` companion package.** `app.MapOrionAuditViewer<TDbContext>("/audit")`
  mounts a read-only JSON API and a built-in embedded single-page UI. No Blazor dependency,
  no build step, authorization-required by default.
- **Audit view render core.** `AuditViewRenderer` / `AuditEntryView` / `FieldChange` in
  core OrionAudit — pure, type-agnostic, used by the viewer and available to any consumer.
- **Telemetry.** `OrionAudit.Dispatch` activity, processed/dead-lettered counters, a batch
  duration histogram, and an observable `orionaudit.capture.queue_depth` gauge.

### Deferred to v0.6

- **Extensible `AuditLog` row (`o.AddColumn<int>(...)`).** Real EF shadow-property columns
  for custom indexable dimensions. Polished feature in its own right; held back so this release
  could ship two large items at quality.
- **Legacy import (`AuditImportBuilder`).** Fluent bulk-import of hand-rolled change history
  as synthetic `AuditLog` rows.

---

## v0.5.1 — Logo refresh *(shipped 2026-05-23)*

New minimalist family-style logo: indigo line-art `📜` document with timeline ticks in the
Moongazing indigo (`#312E81`). No code changes; aligns OrionAudit with the rest of the family.

---

## v0.6.0 — Developer Experience *(planned, Q3 2026)*

Theme: *adopt OrionAudit into an existing system without forking, and index whatever the
business case demands.*

- **Extensible `AuditLog` row.** `o.AddColumn<int>("WorkflowStepId", ctx => ...)` adds real,
  tipped, indexable EF shadow-property columns. Each `AddColumn` requires one consumer-side
  migration; indexing is left to the consumer's migration so they can choose.
- **Legacy import helper.** Fluent `AuditImportBuilder` writes synthetic, idempotent
  `AuditLog` rows from a hand-rolled change-tracking source — diff produced by the same
  `Json6902` engine the capture path uses, so imported history is byte-for-byte compatible.

### Considered for v0.6

- **CLI diff renderer.** A `dotnet tool` that pretty-prints an `AuditLog.Diff`. With the
  viewer covering "see the audit trail" the value/maintenance trade-off is marginal; ship only
  if a clear use case (CI log inspection, ops scripting) lands.
- **Per-entity / per-field UI labels** for the viewer.
- **Web push notifications on audit-row write.** Could ride on the v0.2 outbox hook.

---

## v0.7.0 — Outbox & polymorphic capture *(planned, Q4 2026)*

Theme: *unblock downstream replication and stop bleeding TPH hierarchies at the entity-type
boundary.*

- **Outbox-style publish hook on audit write.** A first-class `IAuditEventPublisher` interface
  invoked inside the capture transaction. Ships with an in-process `ChannelAuditEventPublisher`
  default and a documented contract for plugging in a real broker (RabbitMQ, Azure Service Bus,
  Kafka) from consumer code. Resolves the v0.2 "considered but not promised" item.
- **TPH / polymorphic entity capture.** `[Auditable(BaseType = typeof(Document))]` and the
  fluent equivalent record the runtime class on the row but allow `AuditFor<Document>()` to
  return the full inheritance hierarchy. `EntityType` stays a stable string; a new
  `EntityBaseType` column makes the relationship queryable.
- **Viewer: per-entity / per-field display labels.** `o.Label<Order>(o => o.SubTotal, "Net")`
  surfaces in the viewer table and detail panel. No schema impact — labels are configuration.
- **Provider matrix expansion.** Add MySQL/MariaDB to the supported provider list with a
  `MySqlText` column hint and integration tests.

---

## v0.8.0 — Separate audit store & operator tools *(planned, Q1 2027)*

Theme: *let audit grow at its own pace, on its own iron.*

- **Separate-database audit storage (`o.UseSeparateAuditDb(...)`).** Promoted out of v1.0 so
  large-volume consumers can adopt it earlier. Audit table moves to its own connection / schema
  / DB; primary-write throughput stops paying for audit retention growth. Single-transaction
  capture guarantee is preserved on the primary DB; the audit-side write becomes
  outbox-dispatched.
- **CLI diff renderer (`dotnet orionaudit diff`).** Reads an `AuditLog.Id` (or stdin JSON) and
  pretty-prints the patch with red/green inline rendering. Useful for CI log inspection and ops
  scripting; designed to plug into `git show`-style workflows.
- **Compaction job.** Background hosted-service variant of retention that merges runs of
  small diffs into snapshot rows past a configurable age threshold. Bounds the worst-case
  reconstruction cost without losing fidelity.
- **Viewer auth presets.** First-class `RequirePolicy` / `RequireRole` configuration on
  `MapOrionAuditViewer`; in-box documentation for the three common deployment shapes
  (admin-only, tenant-scoped, read-only public).

---

## v0.9.0 — Documentation & AOT polish *(planned, Q1-Q2 2027)*

Theme: *make OrionAudit the easiest audit library to learn, and finish the AOT story.*

- **Documentation site.** Hosted reference + recipes + migration guides. Replaces the
  repo-readme-as-docs status quo. Includes a runnable cookbook ("audit a multi-tenant SaaS",
  "audit with TPH", "audit with a separate database").
- **Full Native AOT pass.** `JsonPatch.Net` is already gone (v0.4); this milestone audits the
  remaining reflective paths in the dispatcher, viewer, and reconstruction surfaces, and lifts
  the AOT smoke test to assert *zero* `IL2*` / `IL3*` warnings on the full surface.
- **OpenTelemetry semantic-convention pass.** Align span and metric names with the upcoming
  OTel database / messaging semantic conventions instead of the OrionAudit-internal scheme.
  Coordinated with [[orionguard]] / [[orionlock]] so the family ships a consistent telemetry
  shape.

---

## v1.0.0 — Stable API *(planned, Q2 2027)*

Target theme: *commit to the surface, slow down, support it.*

- **API freeze + SemVer 2.0.0 commitment.** Public types are locked; breaking changes only on
  major version bumps from here.
- **Strong-named assemblies.** Required by some enterprise / GAC scenarios.
- **LTS support window** — security and correctness fixes backported to v1.x for 18 months
  after v2 ships.
- **Final documentation polish.** Every public type on the docs site has a runnable example;
  migration guide from any breaking change introduced in 0.x.
- **`net8.0` drop decision.** With net10 mainstream and net12 on the horizon, decide and
  publish whether v1.x ships TFM `net8.0` or starts at `net9.0`. This is the last chance to
  cut net8 before SemVer locks it in.

---

## Considered (no commitment yet)

- **`OrionAudit.PostgresLogical`** — read change events from a Postgres logical-replication
  slot instead of an EF Core interceptor, for consumers who can't (or won't) route writes
  through EF.
- **GraphQL viewer query API.** Higher-fidelity slicing of the audit trail than the current
  REST endpoint. Only worth it if the viewer grows beyond "browse and filter".
- **Optional row-level encryption** for `AuditLog.Diff`/`Snapshot` columns, against a
  consumer-supplied KMS. Soft veto today (see "Out of scope") but the consumer demand signal is
  growing; revisit before v1.

If any of the above maps to a real workload you are on right now, open an issue with the
`roadmap` label and a short description — that is how items move from *considered* to *planned*.

---

## Out of scope for v1.x

These come up in conversation; we're saying no on purpose.

- **A general-purpose event-sourcing framework.** OrionAudit captures *what changed in your
  domain model*, not a replayable command log. If you need event sourcing, use something built
  for it.
- **Cross-database / cross-system audit aggregation.** Centralising audit from N microservices
  is an infra job; OrionAudit ships one row per save in *your* DB.
- **GUI report builder / dashboard product.** The v0.5.0 Viewer is a primitive component, not
  a reporting suite.
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
| v0.1.0    | shipped                             | capture + reconstruction     |
| v0.2.0    | shipped                             | reliability + composite keys |
| v0.3.0    | shipped                             | source generator             |
| v0.4.0    | shipped                             | AOT-clean diff engine        |
| v0.5.0    | shipped                             | async capture + viewer       |
| v0.5.1    | shipped 2026-05-23                  | logo refresh                 |
| v0.6.0    | Q3 2026                             | developer experience         |
| v0.7.0    | Q4 2026                             | outbox + polymorphic capture |
| v0.8.0    | Q1 2027                             | separate audit DB + ops      |
| v0.9.0    | Q1-Q2 2027                          | docs site + AOT polish       |
| v1.0.0    | Q2 2027                             | API freeze                   |

Patch releases (`0.x.y`) ship as needed for bugs and security. Minor releases (`0.x.0`) cluster
features around the themes above and never break documented public APIs without a deprecation
cycle. Dates are targets, not commitments. If a milestone slips by more than four weeks, the
delay is reflected here.
