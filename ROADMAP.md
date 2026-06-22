# OrionAudit Roadmap

This document lists what's shipped, what's actively planned, and what we're deliberately *not*
building. It's a planning artifact, not a contract — dates slip, priorities reshuffle. If
something here matters to you, open a GitHub issue so we can weigh it against everything else.

**Current version: 0.9.0** (shipped 2026-06-22). Queryable audit-history read API and snapshot
compaction landed in 0.8.0; 0.8.1 removed a redundant deep-equals pass from the JSON Patch diff
hot path; 0.9.0 added opt-in tamper-evident hash-chaining (a keyed-MAC per-row chain with a
per-stream anchor, plus an `IAuditIntegrityVerifier`). Next up is audit-log lifecycle
(retention/archival of the log itself, export/streaming) and richer queries on the way to the 1.0.0
API freeze.

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

## v0.6.0 — Developer Experience *(shipped)*

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

## v0.7.0 — Outbox publish hook *(shipped 2026-06-01)*

Theme: *unblock downstream replication without committing to a broker binding.*

Scope was reduced from the original four-item entry so the publisher hook could ship at quality.
The other three items are retargeted below (v0.7.1, v0.7.2, v0.7.3).

- **`IAuditEventPublisher` hook.** First-class extension point invoked inside the capture
  transaction (sync mode) or the dispatcher transaction (async mode). A publisher exception
  aborts the same transaction that holds the audit write. Resolves the v0.2 "considered but
  not promised" outbox item.
- **`AuditLogEvent` wire shape.** Public record mirroring `AuditLog` columns; decoupled from
  the EF entity so downstream consumers do not depend on the persisted entity type.
- **`NullAuditEventPublisher`.** Default registration when nothing is wired — zero behaviour
  change for existing consumers.
- **`ChannelAuditEventPublisher`.** Intentionally toy-grade in-process default with bounded
  buffering and an `IAsyncDisposable` drain. Suitable for monoliths and tests; production
  deployments write a custom `IAuditEventPublisher` against their broker.
- **Telemetry.** `OrionAudit.Publish` ActivitySource span, `orionaudit.events.published` /
  `orionaudit.events.dropped` counters.

---

## v0.7.1 — TPH / polymorphic capture (first slice) *(shipped 2026-06-04)*

Ships the schema column + capture-side stamping. Inheritance-aware querying lands in v0.7.2.

- New `AuditLog.EntityBaseType` nullable column.
- `[Auditable(typeof(TBase))]` constructor overload + fluent `AuditTypeBuilder<T>.UseBaseType<TBase>()`.
- `AuditableTypeConfig.BaseType` public read-only property.
- Capture interceptor stamps `EntityBaseType` from the resolved config when present.

---

## v0.7.2 — TPH inheritance-aware query *(shipped 2026-06-09)*

- `AuditFor<TBase>()` consults `EntityBaseType` alongside `EntityType` so a query for a base
  type returns rows for the whole hierarchy. Existing queries for concrete types stay byte-for-byte
  identical; pre-v0.7.1 rows with null `EntityBaseType` continue to match only via the exact-type predicate.

---

## v0.7.3 — Viewer labels *(shipped 2026-06-09)*

- **`AuditTypeBuilder<T>.Label<TProp>(selector, label)`** + **`Label(label)`** for entity-level labels.
- **`AuditableTypeConfig.EntityLabel`** + **`AuditableTypeConfig.FieldLabel(propertyName)`** public read-only accessors.
- **`AuditViewRenderer.Render(AuditLog, IAuditConfiguration)`** + customColumns overload that decorate the view with labels.
- **`AuditEntryView.EntityDisplayLabel`** + **`FieldChange.DisplayLabel`** populated when a label is configured. Nested property changes inherit their root property's label. No schema impact.

---

## v0.7.4 — MySQL / MariaDB provider matrix *(shipped 2026-06-09)*

- `OrionAuditColumnHints.MySqlJson` (native `json` on MySQL 5.7+ / MariaDB 10.2+) and `OrionAuditColumnHints.MySqlLongText` added to the existing enum.
- New `Moongazing.OrionAudit.MySql` add-on package: `ApplyOrionAuditMySqlConfigurations(this ModelBuilder, DbContext, useLongText, ...)` forwards through to the existing DbContext-aware `ApplyOrionAuditConfigurations` overload with the right hint pre-selected.

## v0.7.5 — LDAP / IdP user resolution hooks *(shipped 2026-06-10)*

- `ClaimAuditUserResolverOptions` (configurable ordered claim lists for id / display name / type).
- `ClaimAuditUserResolver` in `Moongazing.OrionAudit.AspNetCore`.
- `IAuditUserEnricher` synchronous post-resolution hook for IdP / LDAP enrichment (consumers cache).
- `AddOrionAuditClaimResolver(configure?)` DI helper.

---

## v0.8.0 - Queryable history & snapshot compaction *(shipped 2026-06-19)*

Theme: *let consumers read the audit trail through a backend-agnostic surface, and bound its
growth without losing fidelity.*

Scope was narrowed from the original "separate audit store & operator tools" entry. The two
read/maintenance items below shipped at quality; the separate-DB store, CLI diff renderer, and
viewer auth presets are retargeted to the v0.10.0 / v0.11.0 milestones below.

- **Queryable audit-history read API (`IAuditHistoryStore`).** A storage-agnostic read surface
  in the new `Moongazing.OrionAudit.Store` namespace. `QueryAsync(AuditHistoryQuery, ...)`
  returns a paged `AuditHistoryPage` (rows + `TotalCount` + `HasMore`), filtering by entity
  type, polymorphic base type, entity id (subject), `AuditAction`, user id, tenant id, and an
  inclusive `FromUtc`..`ToUtc` UTC range, with `Skip`/`Take` paging and newest-first /
  oldest-first ordering. Every filter is optional; an unfiltered query is bounded by
  `DefaultPageSize` (100). `EfCoreAuditHistoryStore` is the default, registered by
  `AddOrionAudit`; `InMemoryAuditHistoryStore` ships in OrionAudit.Testing.
  `AuditHistoryStoreBase` throws `NotSupportedException` per operation so a backend overrides
  only what it can honour (the `DeleteAuditArchiver`-as-default pattern).
- **Snapshot compaction (`CompactAsync`).** Folds a long change-history for one entity into a
  single compacted snapshot row (latest reconstructable state at the boundary) plus a bounded
  retained tail of the most-recent rows kept verbatim, then removes the folded rows. A folded
  `Deleted` / `SoftDeleted` boundary stays terminal; optional `TenantId` scopes the compaction.
  The `AuditHistoryCompactor` folding engine replays over `AuditLog` JSON via `DiffEngine` (no
  reflection, trim-safe / Native-AOT clean), and the EF Core store applies the plan as one
  insert + delete in a single `SaveChanges` transaction, so a failure leaves history untouched.

---

## v0.8.1 - Diff hot-path perf *(shipped 2026-06-20)*

- **Removed a redundant deep-equals pass from the JSON Patch diff.** `Json6902.Diff` (reached on
  every audited change) opened with a full recursive `JsonNode.DeepEquals` short-circuit, then
  `DiffObject` re-walked the same tree to emit ops, comparing every property twice for the
  common object/object case. The deep-equal guard now runs only on the leaf (scalar /
  kind-mismatch) branch where it actually suppresses a spurious `replace`; containers go straight
  to the structural diff. Byte-identical patch output, no public API or wire-format change. A
  throwaway micro-benchmark on a 20-property entity with one changed field measured roughly
  7-19% less time in `Compute`, scaling with audited-property count.

---

## v0.9.0 - Tamper-evident hash chain *(shipped 2026-06-22)*

Theme: *make the audit log defensible as evidence: prove no row was altered, deleted, or
reordered after it was written.*

Pulled forward from the original v0.10.0 entry as the strongest self-contained integrity feature.
Opt-in and fully additive; capture, diff, compaction, and the read APIs are unchanged when it is
off.

- **Tamper-evident hash-chaining (`o.UseHashChain(h => h.UseKey(...))`).** Each captured `AuditLog`
  row gains a **keyed** HMAC-SHA256 `EntryHash = HMAC(key, canonical(row fields + custom columns) ||
  PreviousHash)`, plus a `PreviousHash` column and a `HashKeyId` column. The key comes from an
  `IAuditChainKeyProvider` that lives **outside** the audit database, which closes a bare hash's
  weakness: with a plain SHA-256 chain, anyone able to write rows could recompute the hashes and forge
  a valid-looking chain; a keyed MAC cannot be forged without the key. Canonicalization is
  deterministic and round-trip-stable: fixed field order, length-prefixed fields (content cannot
  migrate across field boundaries undetected), registered custom-column values folded in with
  deterministic name ordering, invariant culture, UTF-8, and a Kind/precision-stable timestamp (epoch
  milliseconds) so a row MACs identically before and after persistence across SQLite / SQL Server /
  PostgreSQL / MySQL. A key is required - enabling without one fails clearly; the key id is stored per
  row so keys can rotate without invalidating older rows.
- **Per-stream anchor + concurrency + truncation.** A persisted head row per stream
  (`OrionAudit_Chain_Anchor`, keyed by `EntityType` + `EntityId` + `TenantId`) stores the latest hash,
  row count, and key id. The writer row-locks the anchor inside the consumer's `SaveChanges`
  transaction, stamps `PreviousHash` from it, and advances it in the same transaction, so concurrent
  same-stream appends serialize on the anchor (no two transactions stamp the same predecessor) while
  different streams stay parallel. The anchor doubles as the truncation guard: verification compares
  the walked tail hash + count against it, so deleting the tail row(s) - or an entire stream - is
  caught (`Truncated`) even though the surviving prefix links intact.
- **Chain scope is per entity stream, per tenant** (rows sharing `EntityType` + `EntityId` +
  `TenantId`, ordered by `OccurredOnUtc` then `Id`). Putting the tenant in the chain key means
  tenant-scoped verification walks a self-consistent chain - the first row of a second tenant is its
  own genesis, not a broken link onto the first tenant's head. Stamping runs inside the capture
  transaction on both the synchronous interceptor and the async dispatcher.
- **`IAuditIntegrityVerifier.VerifyChainAsync`.** Walks rows in chain order, recomputes each keyed MAC
  (binding registered custom columns), checks each stream against its anchor, and returns either valid
  (with the hashed-row count) or the first broken row's id + entity and an `AuditChainBreakReason`
  (`ContentMismatch`, `BrokenLink`, `MissingHashAfterChainStart`, `Truncated`, `UnknownKey`). Verify
  one entity stream or the whole table, optionally tenant-scoped. Read-only and idempotent.
  `EfCoreAuditIntegrityVerifier` is the default, registered automatically when chaining is enabled.
- **Backward compatible.** Pre-existing rows (null `EntryHash`) verify as an unchained prefix the
  verifier skips; verification begins at each stream's first hashed (genesis) row. Enabling the
  feature requires the consumer to add an EF Core migration for the new columns and the
  `OrionAudit_Chain_Anchor` table; both are emitted by the OrionAudit entity configurations like every
  other audit table. Integrity holds against an attacker who can modify the DB but not obtain the MAC
  key, which must be stored outside the audit database.

---

## v0.10.0 - Audit-log lifecycle, export & docs *(planned, Q3 2026)*

Theme: *manage the audit log itself as a first-class, long-lived store (its retention,
its integrity, and getting data out of it), and finish the docs.*

- **Background compaction job.** A hosted-service variant of the v0.8.0 `CompactAsync` operation
  that folds runs of small diffs into snapshot rows past a configurable age / depth threshold,
  bounding worst-case reconstruction cost without an operator script. Reuses the
  `AuditHistoryCompactor` engine.
- **Archival of the audit log itself.** Extends the v0.7.8 `IAuditArchiver` retention hook with
  a cold-store path tuned for the audit table specifically (age-tiered move to S3 / Parquet /
  archive table), so an aged audit row can leave the live DB while staying reconstructable on
  demand from the archive.
- **Audit-history export / streaming.** A bulk, paged export off `IAuditHistoryStore` (NDJSON /
  CSV) plus a cursor-based streaming read for feeding a warehouse or SIEM, without holding an
  unbounded result in memory. Builds on the v0.8.0 paged query.
- **Documentation site.** Hosted reference + recipes + migration guides, replacing the
  repo-readme-as-docs status quo. Runnable cookbook ("audit a multi-tenant SaaS", "audit with
  TPH", "query and compact history").
- **Viewer auth presets.** First-class `RequirePolicy` / `RequireRole` configuration on
  `MapOrionAuditViewer` plus in-box docs for the three common deployment shapes (admin-only,
  tenant-scoped, read-only public). Carried from the original v0.8.0 entry.

---

## v0.11.0 - Richer queries & separate-store audit *(planned, Q4 2026)*

Theme: *richer to slice, and able to live off the primary database.*

Tamper-evident hash-chaining shipped early in v0.9.0; this milestone keeps the remaining
query/storage items from the original entry.

- **Richer query filters / aggregations.** Promote the v0.7.6 / v0.7.7 composable filters and
  rollups onto `IAuditHistoryStore` so the backend-agnostic surface gains correlation-id and
  free-predicate filters plus count/by-day/by-action aggregations, not just paged row reads.
- **Separate-database audit storage (`o.UseSeparateAuditDb(...)`).** Audit table moves to its
  own connection / schema / DB so primary-write throughput stops paying for audit growth. The
  single-transaction capture guarantee is preserved on the primary DB; the audit-side write
  becomes outbox-dispatched. Carried from the original v0.8.0 entry; sequenced after the read
  surface so cross-store queries land on a stable `IAuditHistoryStore`.
- **CLI diff renderer (`dotnet orionaudit diff`).** Reads an `AuditLog.Id` (or stdin JSON) and
  pretty-prints the patch with red/green inline rendering for CI log inspection and ops
  scripting. Carried from the original v0.8.0 entry.

---

## v0.12.0 - Store backends & AOT polish *(planned, Q1 2027)*

Theme: *more places to put the audit log, and finish the AOT story.*

- **Additional `IAuditHistoryStore` backends.** With the read/maintenance surface stable, ship
  at least one non-EF backend (a document / append-only store is the leading candidate) to prove
  the abstraction holds away from a relational `DbContext`, for consumers who keep audit off
  their primary database engine.
- **Performance at scale.** A large-history benchmark (millions of rows, deep per-entity
  histories) driving index guidance for the query filters, a server-side paging audit, and
  compaction throughput numbers. Publishes the scaling envelope the query API is good for.
- **Full Native AOT pass.** `JsonPatch.Net` is already gone (v0.4); this milestone audits the
  remaining reflective paths in the dispatcher, viewer, and reconstruction surfaces, and lifts
  the AOT smoke test to assert *zero* `IL2*` / `IL3*` warnings on the full surface.
- **OpenTelemetry semantic-convention pass.** Align span and metric names with the upcoming
  OTel database / messaging semantic conventions instead of the OrionAudit-internal scheme.
  Coordinated with [[orionguard]] / [[orionlock]] so the family ships a consistent telemetry
  shape.

---

## v1.0.0 — Stable API *(planned, Q1-Q2 2027)*

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
  growing; revisit before v1. Distinct from the v0.9.0 tamper-evidence work, which proves a row
  was not altered but does not encrypt its contents.

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

| Milestone | Target window      | Driver                           |
| --------- | ------------------ | -------------------------------- |
| v0.1.0    | shipped            | capture + reconstruction         |
| v0.2.0    | shipped            | reliability + composite keys     |
| v0.3.0    | shipped            | source generator                 |
| v0.4.0    | shipped            | AOT-clean diff engine            |
| v0.5.0    | shipped            | async capture + viewer           |
| v0.5.1    | shipped 2026-05-23 | logo refresh                     |
| v0.6.0    | shipped 2026-05-24 | developer experience             |
| v0.7.0    | shipped 2026-06-01 | publisher hook                   |
| v0.7.1    | shipped 2026-06-04 | TPH / polymorphic capture        |
| v0.7.2    | shipped 2026-06-09 | TPH inheritance-aware query      |
| v0.7.3    | shipped 2026-06-09 | viewer labels                    |
| v0.7.4    | shipped 2026-06-09 | MySQL / MariaDB matrix           |
| v0.7.5    | shipped 2026-06-10 | LDAP / IdP user resolution       |
| v0.8.0    | shipped 2026-06-19 | queryable history + compaction   |
| v0.8.1    | shipped 2026-06-20 | diff hot-path perf               |
| v0.9.0    | shipped 2026-06-22 | tamper-evident hash chain        |
| v0.10.0   | Q3 2026            | log lifecycle + export + docs    |
| v0.11.0   | Q4 2026            | richer queries + separate store  |
| v0.12.0   | Q1 2027            | store backends + AOT polish      |
| v1.0.0    | Q1-Q2 2027         | API freeze                       |

Patch releases (`0.x.y`) ship as needed for bugs and security. Minor releases (`0.x.0`) cluster
features around the themes above and never break documented public APIs without a deprecation
cycle. Dates are targets, not commitments. If a milestone slips by more than four weeks, the
delay is reflected here.
