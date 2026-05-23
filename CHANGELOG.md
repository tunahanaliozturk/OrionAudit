<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionAudit will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.5.1] - 2026-05-23

### Changed

- New minimalist family-style logo (magnifying glass with an Orion star inside the lens, indigo line-art, no badge ring) replaces the previous emblem. Applied to the README and to every published package's NuGet icon. The Viewer package now also carries `PackageIcon` (previously the only packable project without one).

## [0.5.0] - 2026-05-23

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
  human-readable view model. A consumer can render their own UI; the Viewer is its first client.
- **Telemetry.** `OrionAudit.Dispatch` activity; counters `orionaudit.dispatch.rows_processed`
  / `orionaudit.dispatch.rows_deadlettered`; histogram `orionaudit.dispatch.batch.duration`;
  observable gauge `orionaudit.capture.queue_depth`. `ActivitySource` / `Meter` version → 0.5.0.

### Changed

- `ApplyOrionAuditConfigurations` now also maps the `OrionAudit_Capture_Queue` companion table
  (a new optional `captureQueueTableName` parameter overrides its name). Harmless when async
  capture is not configured — the table simply stays empty.
- `IAuditConfiguration` gained an `AuditedTypeNames` collection so the viewer's `/api/meta`
  endpoint can surface the registered audited types.
- `AuditEntryView.Action` and `FieldChange.ChangeKind` serialize as JSON strings rather than
  integer enum values so the embedded viewer UI (and any other consumer) sees `"Inserted"`
  rather than `0`.

### Migration from v0.4.0

- **Synchronous consumers:** no code change. The capture path is byte-for-byte identical.
- **Schema:** adopting v0.5.0 requires one EF migration creating `OrionAudit_Capture_Queue`.
  The table stays empty unless `UseAsyncCapture` is called — the v0.2.0
  `OrionAudit_Snapshot_Cursors` precedent.
- **Opting into async capture:** call `o.UseAsyncCapture(...)` in `AddOrionAudit`. Be aware
  that audit becomes eventually consistent — `AuditFor<T>()` sees only dispatched rows. Use
  `IAuditDispatcher.FlushPendingAsync` where read-after-write is required.
- **The Viewer** is a separate, optional package. Installing it changes nothing until
  `MapOrionAuditViewer` is called.

## [0.4.0] - 2026-05-21

AOT-Clean Diff Engine release. Replaces the `JsonPatch.Net` dependency with an in-house,
reflection-free RFC 6902 engine, making the diff engine fully reflection-free and the
snapshot-capture path Native-AOT clean when wired through `UseJsonContext`.

### Added

- **`Json6902` engine.** A reflection-free RFC 6902 compute/apply implementation built only on
  `System.Text.Json.Nodes`, with no `[RequiresDynamicCode]` surface. `DiffEngine` is now a thin
  facade over it; its public `Compute` / `Apply` signatures are unchanged.
- **Native AOT CI gate restored.** The `aot/Moongazing.OrionAudit.AotProbe` project and the
  `aot-publish-check` workflow job return. The probe Native-AOT publishes OrionAudit's
  reflection-free surface with `TreatWarningsAsErrors`; any `IL2*` / `IL3*` warning fails the
  build. The `publish` job depends on it again.

### Changed

- `DiffEngine.Compute` / `Apply` no longer depend on `JsonPatch.Net`. `Compute` emits only
  `add` / `remove` / `replace` operations; `Apply` supports all six RFC 6902 operations
  (`add` / `remove` / `replace` / `move` / `copy` / `test`) so historical patches written by
  `JsonPatch.Net` (which can carry `move` / `copy`) still replay.
- **`SnapshotBuilder.Build` split into two overloads.** The overload taking a
  `JsonSerializerContext` is reflection-free and Native-AOT clean (the CI AOT probe exercises
  it end to end); the context-less overload is reflective and annotated with
  `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`. A non-primitive value whose type is
  not registered in the supplied context now throws `OrionAuditException` with a clear message
  instead of silently reflecting.
- **`AuditConfigurationBuilder` trim annotations.** `Audit<T>` / `Audit(Type)` and the
  attribute-scan path carry `[DynamicallyAccessedMembers(PublicProperties)]`, so types
  registered by the `[OrionAuditModule]` source generator stay trim- and AOT-safe.
- Hashed (`[HashedAudit]`) non-string values now derive their hash from the canonical JSON
  representation instead of reflective `JsonSerializer.Serialize`. String values are unchanged.
- `OrionAudit` `ActivitySource` / `Meter` version bumped to `0.4.0`.

### Removed

- **`JsonPatch.Net` package dependency.** OrionAudit no longer pulls in `JsonPatch.Net` or its
  transitive `Json.Pointer` / `Json.More.Net` graph.

### Migration from v0.3.0

- **No code changes required** for typical consumers. `DiffEngine`'s public surface is
  identical, and the standard `AddOrionAudit` / interceptor wiring is unaffected.
- **No schema or data migration.** The persisted `AuditLog.Diff` format is unchanged RFC 6902
  JSON. Existing audit history replays as-is.
- **`SnapshotBuilder` (low-level type) callers:** the four-argument `Build` overload now takes
  a non-nullable `JsonSerializerContext`. Code that called `Build(type, values, config)` is
  unaffected — it binds to the context-less overload. Code that passed an explicit `null`
  context should drop the argument and call the three-argument overload instead.

## [0.3.0] - 2026-05-20

Source Generator release. Replaces the runtime assembly scan with a compile-time generator and
plumbs a `JsonSerializerContext` through the snapshot/reconstruct paths so trim-aware consumers
can keep those reflection-free.

### Added

- **`Moongazing.OrionAudit.Generators`** — a new Roslyn incremental source generator, shipped
  inside the existing `OrionAudit` NuGet under `analyzers/dotnet/cs/` (no separate package to
  install).
- **`[OrionAuditModule]` attribute.** Decorate a `partial class` with it and the generator emits
  a `RegisterAuditedTypes(AuditConfigurationBuilder)` method plus an `AuditedTypeNames` list.
  `RegisterAuditedTypes` registers every `[Auditable]` type discovered at compile time — no
  runtime reflection, no assembly scan.
- **`OrionAuditOptions.UseJsonContext(JsonSerializerContext)`.** Supplies a System.Text.Json
  source-generated context; `SnapshotBuilder` and `AuditReconstructor` route non-primitive
  property values and replayed state through it instead of through reflective
  `JsonSerializer.SerializeToNode` / `Deserialize<T>`.
- **Trim annotations.** `AuditableTypeDiscovery.Discover` and `OrionAuditOptions.ScanAssembly`
  now carry `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`, so trim/AOT publishes flag
  the reflective assembly-scan path and point consumers at the `[OrionAuditModule]` generator.

### Changed

- `OrionAuditOptions.ConfigurationBuilder` is promoted from internal to public so the
  generated `RegisterAuditedTypes` can register types against it.
- `SnapshotBuilder.Build` and `AuditReconstructor` gained optional `JsonSerializerContext`
  parameters / constructor overloads (default `null` keeps the v0.2.0 reflective behaviour).
- `OrionAudit` `ActivitySource` / `Meter` version bumped to `0.3.0`.
- Sample console migrated to the `[OrionAuditModule]` + `UseJsonContext` wiring.

### Deferred to v0.4

- **Full Native AOT cleanliness.** `JsonPatch.Net` — the library behind `DiffEngine` — is not
  AOT-compatible. Making OrionAudit's diff engine AOT-clean requires replacing it with a
  hand-rolled RFC 6902 emitter; that is the v0.4 theme. v0.3.0 removes the runtime *assembly
  scan* and *snapshot serialisation* reflection, but the diff path still uses reflection.

### Migration from v0.2.0

- **No code changes required.** Every v0.2.0 API still works unchanged; the generator and
  `UseJsonContext` are purely opt-in.
- To adopt the generator: add `[OrionAuditModule] partial class AppAuditModule { }`, then in
  `AddOrionAudit` call `AppAuditModule.RegisterAuditedTypes(o.ConfigurationBuilder)` and
  `o.UseJsonContext(YourJsonContext.Default)`.

## [0.2.0] - 2026-05-19

Reliability & scale release. Composite primary keys, periodic snapshotting that turns O(N)
reconstruction into O(K), background retention sweeps, provider-aware column types, soft-delete
semantics, and an ambient correlation-id scope for background jobs.

### Added

- **Composite primary keys.** `ExtractPrimaryKey` no longer throws on multi-column PKs; values
  serialise as a stable ordinal-joined string (`"key1|key2|..."`, `|` percent-escaped in source
  values). New public helper `AuditKey.From(params object?[])` round-trips the format for
  reconstruction callers.
- **`AuditScope.Push(correlationId)`** — ambient `AsyncLocal<string?>` correlation id, preferred
  over `Activity.Current?.Id` when stamping `AuditLog.CorrelationId`. Useful for background
  jobs, console runners, and other contexts without a W3C trace in flight.
- **Soft-delete capture.** Class-level `[SoftDelete(nameof(IsDeleted))]` attribute and
  equivalent fluent `b.SoftDelete(x => x.IsDeleted)` declare the boolean property whose flip
  `false → true` is recorded as new `AuditAction.SoftDeleted` (byte = 3) instead of
  `Updated`. Reconstruction treats soft-deletes like hard deletes.
- **Periodic snapshotting policy.** `OrionAuditOptions.SnapshotEvery(int)` and
  `SnapshotEvery(TimeSpan)` opt-in to writing a full `AuditLog.Snapshot` on every Nth update or
  after T elapsed since the last snapshot. New `OrionAudit_Snapshot_Cursors` companion table
  tracks per-entity progress (mapped automatically by `ApplyOrionAuditConfigurations`).
  Reconstruction walks backwards to the most recent snapshot at or before `asOf` and replays
  only the diffs after it — O(K) instead of O(N).
- **Retention policy.** `RetainFor(TimeSpan)` and `RetainCount(int)` declarative policies plus
  `AuditRetentionHostedService<TDbContext>` background sweep (auto-registered when policy is
  configured). Bounded by `MaxRowsPerSweep` (default 10_000) and `RetentionSweepInterval`
  (default 1h) so each batch transaction stays short.
- **Provider-aware column types.** `OrionAuditColumnHints` enum
  (`Auto` / `SqlServerNvarcharMax` / `PostgresJsonb` / `SqliteText`) passed to
  `ApplyOrionAuditConfigurations(columnHints: ...)` maps `Diff` / `Snapshot` to provider-native
  JSON/text types. Default `Auto` emits no hint and lets EF Core pick.
- **Telemetry additions.** `OrionAudit.Retention.Sweep` activity, counters
  `orionaudit.snapshots.written` / `orionaudit.retention.rows_deleted`, and histogram
  `orionaudit.retention.sweep.duration`. `OrionAudit` `ActivitySource` / `Meter` version
  bumped to `0.2.0`.
- **Dependency added.** `Microsoft.Extensions.Hosting.Abstractions` (for the retention
  background service base class).

### Changed

- `AuditLogEntityTypeConfiguration` constructor now accepts an `OrionAuditColumnHints` overload
  (default `Auto` keeps v0.1.0 behaviour byte-for-byte).
- `ApplyOrionAuditConfigurations` now also maps `SnapshotCursor`. Harmless when periodic
  snapshotting is not configured — the table simply stays empty.

### Migration from v0.1.0

- **No code changes required** for consumers that use single-column PKs and don't enable any
  v0.2.0 feature.
- **Schema migration** needed only when adopting `SnapshotEvery(...)` — generate a migration
  that creates the new `OrionAudit_Snapshot_Cursors` table.
- `AuditAction.SoftDeleted` is a new enum value; readers compiled against v0.1.0 stay
  forward-compatible (existing pattern-matching switches with a `_ => ...` fallback keep
  working).

## [0.1.0] - 2026-05-19

Initial public release of OrionAudit.

### Packages

- `OrionAudit` — core library
- `OrionAudit.AspNetCore` — ASP.NET Core integration
- `OrionAudit.Testing` — framework-agnostic test helpers

### Added

- `AuditSaveChangesInterceptor` — EF Core interceptor that captures Insert / Update / Delete
  operations against audited entities and writes `AuditLog` rows in the same transaction.
- JSON Patch (RFC 6902) diff engine via `JsonPatch.Net` for compact, replayable change records.
- Sensitive-field handling via `[NotAuditable]`, `[HashedAudit]`, and `[RedactedAudit]` attributes,
  plus equivalent fluent overrides (`b.Exclude(...)`, `b.Hash(...)`, `b.Redact(...)`).
- Fluent configuration surface (`AuditConfigurationBuilder`, frozen `AuditConfiguration` runtime
  view) with attribute-discovered defaults and explicit overrides.
- Assembly scanning via `AuditableTypeDiscovery` and `OrionAuditOptions.ScanAssembly(...)`.
- Pluggable user / tenant attribution: `IAuditUserResolver`, `IAuditTenantResolver`, `AuditUser`
  record.
- Read API: `DbContext.AuditFor<T>()` and `DbContext.AuditLog()` extensions with automatic
  tenant filtering (bypassable with `crossTenant: true`).
- `IAuditReconstructor` with `ReconstructAsync` and `ReconstructManyAsync` for time-travel
  state reconstruction by diff replay.
- DI surface: `AddOrionAudit<TContext>`, `UseOrionAudit`, `ApplyOrionAuditConfigurations`.
- OpenTelemetry instrumentation via `OrionAuditTelemetry.ActivitySource` and `Meter` with capture
  / reconstruct activities and counters/histograms (`orionaudit.entries.written`,
  `orionaudit.entries.failed`, `orionaudit.capture.duration`, `orionaudit.reconstruct.duration`).
- ASP.NET Core integration: `HttpContextAuditUserResolver`, `AddOrionAuditAspNetCore()`.
- Testing helpers: `AuditCapture` snapshot + `AuditAssertions` fluent surface
  (`HaveLogged<T>`, `NotHaveLogged<T>`, `HaveLoggedExactly(n).Of<T>()`), plus `InMemoryAuditUserResolver`
  and `InMemoryAuditTenantResolver` test doubles.

### Limitations (v0.1.0)

- Composite primary keys throw `OrionAuditConfigurationException` at runtime — single-column PKs only.
- Snapshot column populated only on Delete; in-place reconstruction at any timestamp uses diff
  replay from Insert forward.
