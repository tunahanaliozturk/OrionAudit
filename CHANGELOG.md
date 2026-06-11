<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionAudit will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.7.16] - 2026-06-11

### Added

#### `orionaudit.retention.errors` counter

`Counter<long>` that increments when the background retention loop swallows an unexpected exception from `SweepOnceAsync`. Operators page on `rate(orionaudit_retention_errors_total[5m])` to catch a stuck or thrashing sweep long before retention SLAs slip.

- Tag: `exception_type` - short type name (e.g. `TimeoutException`, `DbUpdateConcurrencyException`) so dashboards can split by root cause.
- Cancellation does NOT emit - the cancellation catch above this filter is its own branch.
- Public `OrionAuditTelemetry.RecordRetentionError(string exceptionType)` so consumer-owned retention drivers can opt in.

### Tests

1 new fact; 239 total.

### Migration from v0.7.15

Source-compatible.

## [0.7.15] - 2026-06-11

### Added

#### `orionaudit.retention.dispatched` counter

`Counter<long>` incremented once per `SweepOnceAsync` cycle with the policy branch the dispatcher took. Operators graph the rate to confirm the live policy matches the configured one across a rolling deployment.

- Tag: `policy` - one of `retain_for`, `retain_count`, `per_tenant`, `per_entity_type`, `none`, `unknown` (forward-compat).
- Pairs with the existing `RetentionRowsDeleted` counter: rows_deleted is rate-meaningful, dispatched is policy-shape-meaningful.
- Emitted inside `DispatchPolicyAsync` BEFORE the policy-specific sweep runs so a sweep that throws mid-run still records its branch.

### Tests

5 new facts (parameterised Theory).

### Migration from v0.7.14

Source-compatible.

## [0.7.14] - 2026-06-11

### Added

#### `orionaudit.capture.entries_per_save` histogram

`Histogram<int>` on the existing `OrionAudit` Meter. Operators graph p99 to spot outlier saves (bulk import paths that should have been audited in smaller chunks) and right-size capture-queue partitioning.

- Recorded inside `AuditSaveChangesInterceptor` on every save that produces at least one audited entry, in BOTH async-capture and inline-capture modes.
- Zero-row saves do NOT emit so the histogram tail does not get polluted with 0 samples.
- Complements the steady-state `orionaudit.capture.entries_written` counter (which is rate-meaningful) by exposing distribution (which is outlier-meaningful).

### Tests

2 new integration facts; 29 total.

### Migration from v0.7.13

Source-compatible.

## [0.7.13] - 2026-06-11

### Added

#### `orionaudit.dispatch.lag` histogram

Operators graph p50/p99 dispatch lag to spot capture-queue backlog or dispatcher slowdown long before rows pile up beyond the steady-state `orionaudit.dispatch.rows_processed` rate.

- New `Histogram<double>` named `orionaudit.dispatch.lag` (unit `ms`) on the existing `Moongazing.OrionAudit` Meter.
- Recorded inside `AuditDispatcher.DispatchOnceAsync` after each successful row promotion.
- Negative deltas (clock skew between capture and dispatcher hosts) are clamped to 0 so they do not pull the histogram p50 down.

### Tests

1 new fact (integration).

### Migration from v0.7.12

Source-compatible.

## [0.7.12] - 2026-06-11

### Added

#### `RetentionSweepOptions.MaxSweepDuration` - wall-clock budget per cycle

Operators running retention on a maintenance window need a guarantee that a single sweep gives up control by a known deadline rather than chewing through `MaxRowsPerSweep` rows of a stuck backend.

- `RetentionSweepOptions.MaxSweepDuration` (nullable, default null = unlimited, preserves v0.7.11 behaviour).
- The deadline is captured at the start of each `SweepOnceAsync` call and consulted between per-tenant / per-entity-type branches via `DeadlineReached()`. Inner branches return early when it has elapsed; the sweep returns whatever total it has accumulated so far.
- Does NOT preempt an in-flight delete batch - granularity is per dispatch unit (tenant, entity type), which keeps the bounded-transaction guarantee from v0.7.x intact.
- Plays well with `MaxRowsPerSweep`: whichever cap fires first wins.

### Tests

3 new facts.

### Migration from v0.7.11

Source-compatible.

## [0.7.11] - 2026-06-11

### Added

#### Retention dry-run mode

`RetentionSweepOptions.DryRun` (default false) flips the sweep into count-only mode. Operators use this to validate a new `RetentionPolicy` (especially `PerTenant` / `PerEntityType`) on production data without touching any row.

- Internally, dry-run wraps the configured archiver in `DryRunAuditArchiver` so all eligibility logic from v0.7.7-v0.7.10 (PerTenant / PerEntityType / archiver-aware paths) applies unchanged.
- The would-have-removed total flows back to `SweepOnceAsync` and is exposed under a new telemetry counter `orionaudit.retention.dry_run_rows` (distinct from `orionaudit.retention.rows_deleted` so dashboards can differentiate dry runs from real cycles).
- The activity tag mirrors the counter.

### Tests

4 new facts; 230 total.

### Migration from v0.7.10

Source-compatible. Default behaviour is unchanged.

## [0.7.10] - 2026-06-11

### Added

#### `RetentionPolicy.PerEntityType` + nested `PerTenant` -> `PerEntityType`

Extends the v0.7.9 per-tenant retention. v0.7.9 evaluated one policy per tenant; v0.7.10 lets each tenant carry a per-entity-type policy so compliance windows can be expressed at the row-class level.

- `RetentionPolicy.PerEntityType(byEntityType, fallback)` per-entity-type policy factory.
- `PerTenant` now accepts a `PerEntityType` policy as a tenant value -> per-(tenant, entity-type) windows.
- `SweepPerEntityTypeAsync` discovers entity types (optionally tenant-scoped) and dispatches per entity type. Cross-cycle budget enforced.
- Rejects empty mapping, null policy values, nested `PerTenant` / `PerEntityType`, null arguments.

### Tests

7 new facts; 226 total.

### Migration from v0.7.9

Source-compatible.

## [0.7.9] - 2026-06-10

### Added

#### `RetentionPolicy.PerTenant` - per-tenant retention policies

Different tenants frequently have distinct compliance windows (90 days for one customer, 7 years for another). v0.7.6-v0.7.8 forced one policy across the whole audit table; v0.7.9 lets the sweep evaluate each tenant policy independently.

- `RetentionPolicy.PerTenant(IReadOnlyDictionary<string, RetentionPolicy> byTenantId, RetentionPolicy fallback)`.
- Snapshot at construction. Rejects empty mapping, nested `PerTenant`, null arguments.
- `AuditRetentionHostedService.SweepPerTenantAsync` discovers tenants and dispatches per tenant.
- Age-based path respects the v0.7.8 `IAuditArchiver` strategy hook.

### Tests

7 new facts; 219 total.

### Migration from v0.7.8

Source-compatible.

## [0.7.8] - 2026-06-10

### Added

#### `IAuditArchiver` strategy hook for the retention sweep

Mirrors the OrionGuard v6.5.6 `IOutboxArchiver` pattern. v0.7.7 retention always hard-deleted; v0.7.8 lets consumers register an archiver that ships expiring rows to a separate cold store (S3, Parquet, archive table) BEFORE deleting them.

- `IAuditArchiver` interface: `ArchiveAsync(DbContext, IReadOnlyList<AuditLog>, RetentionPolicy, CancellationToken) -> Task<int>`.
- `DeleteAuditArchiver` default - keeps the v0.7.7 fast path (single `ExecuteDelete`, no row materialisation).
- `CopyToTableAuditArchiver<TArchiveRow>` generic - transactional copy-into-archive then delete-from-live.
- `AuditRetentionHostedService<TDbContext>` 6-arg ctor with optional archiver; v0.7.7 5-arg ctor retained for ABI compat.
- `AddOrionAudit` registers `DeleteAuditArchiver` via `TryAddSingleton` so custom archivers win without explicit removal.

### Tests

7 new facts; 212 total in core suite.

### Migration from v0.7.7

Source-compatible.

## [0.7.7] - 2026-06-10

### Added

#### `AuditRollupExtensions` - time-series rollups

Pairs with the v0.7.6 composable filters: chain rollup helpers AFTER `AuditFor<T>()` / `AuditLog()` + filters to scope the aggregate. Operator dashboards rendering activity histograms or per-day leaderboards previously had to materialise rows in memory and group on the client; v0.7.7 emits SQL `GROUP BY` so the aggregate fits in a single round-trip.

- **`RollupByDay()`** -> `IQueryable<AuditDailyBucket>` ordered ascending. `AuditDailyBucket(DateOnly Day, int Count)`. Empty days are NOT materialised; fill gaps in memory if you need a dense series.
- **`RollupByMonth()`** -> `IQueryable<AuditMonthlyBucket(int Year, int Month, int Count)>` ordered ascending.
- **`RollupByDayAndAction()`** -> `IQueryable<AuditDailyActionBucket(DateOnly Day, AuditAction Action, int Count)>`. One row per (day, action) pair. Useful for stacked charts that distinguish create / update / delete / soft-delete.
- **`RollupByDayAndUser(IEnumerable<AuditLog>, topUsersPerDay)`** -> `IEnumerable<AuditDailyUserBucket(DateOnly Day, string UserId, int ActivityCount)>`. Materialises in-memory rather than translating to SQL because the per-day Top-N sub-grouping is awkward across providers; consumers call `ToListAsync()` first and then pipe through the rollup.

### Tests

8 new facts cover: `RollupByDay` count + ascending order, `RollupByMonth` distinct buckets, `RollupByDayAndAction` independent (day, action) keys, `RollupByDayAndUser` Top-N per day, non-positive Top-N rejected, null-query rejection on all four helpers, composition with `ByAction` filter. SQLite in-memory fixture so `GROUP BY` exercises a relational translator. 205 facts total.

### Migration from v0.7.6

Source-compatible.

```csharp
var last30Days = await dbContext.AuditLog()
    .WithinLast(TimeSpan.FromDays(30))
    .RollupByDay()
    .ToListAsync();

var monthlyBreakdownByAction = await dbContext.AuditFor<Order>()
    .RollupByDayAndAction()
    .ToListAsync();
```

## [0.7.6] - 2026-06-10

### Added

#### `AuditLogQueryExtensions` - composable filter / projection helpers

`AuditQueryExtensions.AuditFor<T>()` / `AuditLog()` already auto-resolved the audit table and tenant. v0.7.6 ships a composable set of extensions on `IQueryable<AuditLog>` so consumers can stack filters AFTER the entry point AND share the same DSL when the audit query comes from a different `DbContext` (the cross-context scenario where audit storage lives on a dedicated DB but operator projections combine it with primary-DB data).

- **`BetweenDates(fromUtc, toUtc)`** / **`WithinLast(window)`** - time-window helpers; reject inverted ranges and non-positive windows so misconfigured callers fail fast.
- **`ByUser(id)`** / **`ByUsers(ids)`** / **`ByUserType(type)`** / **`ByTenant(id)`** / **`ByAction(AuditAction)`** / **`ByCorrelation(id)`** - the common operator-dashboard filters expressed as a fluent chain. `ByUsers` materialises the id sequence to a `List<string>` so the EF Core LINQ translator picks the SQL `IN` overload instead of the `ReadOnlySpan`-based array extension on .NET 9+.
- **`Newest()`** / **`Oldest()`** - explicit ordering for paging.
- **`DistinctUserIds()`** - projection of distinct non-null user ids; the canonical building block for cross-context joins. Take the result in-process, then issue a single `WHERE Id IN (...)` against the user-store context to materialise display names without paying for a SQL-side JOIN.
- **`TopActorsByCount(top)`** - returns `UserActivitySummary(UserId, ActivityCount)` ordered by descending activity. Two-stage projection (GroupBy -> anonymous shape -> record) so SQLite / SQL Server / Postgres translators all accept it.
- **`Matching(Expression<Func<AuditLog, bool>>)`** - free-form predicate continuation that reads as part of the DSL.

### Tests

15 new facts (`AuditLogQueryExtensionsTests`). The test suite uses SQLite in-memory rather than EF Core InMemory so `Contains`, `GroupBy`, and `OrderBy` exercise a relational translator equivalent to what production providers ship. 197 facts total (+15 new + 1 pre-existing skip).

### Migration from v0.7.5

Source-compatible. Existing `AuditFor<T>()` / `AuditLog()` calls keep working; the new helpers chain on top.

```csharp
var last30Days = await dbContext.AuditFor<Order>()
    .WithinLast(TimeSpan.FromDays(30))
    .ByUserType("user")
    .Newest()
    .Take(50)
    .ToListAsync();

var topActors = await dbContext.AuditLog()
    .WithinLast(TimeSpan.FromDays(7))
    .TopActorsByCount(10)
    .ToListAsync();
```

## [0.7.5] - 2026-06-10

### Added

#### LDAP / IdP user resolution hooks

Lands the user-resolution-hook deferral from chain 4. The single-purpose `HttpContextAuditUserResolver` only checked `NameIdentifier` / `sub`; v0.7.5 introduces a claim-driven resolver that handles real-world IdP shapes (Azure AD `oid`, single-tenant `preferred_username`, custom service-principal classifications) without forking the resolver per tenant.

- **`ClaimAuditUserResolverOptions`** in the core package - configurable ordered lists of `IdClaimTypes` (defaults: `sub`, `NameIdentifier`, `oid`, `preferred_username`), `DisplayNameClaimTypes` (defaults: `Name`, `name`, `preferred_username`, `Email`, `email`), optional `TypeClaimType`, `DefaultUserType` (default `"user"`), and `RequireAuthenticated` (default `true`). First match wins.
- **`ClaimAuditUserResolver`** in `Moongazing.OrionAudit.AspNetCore` - reads the current `ClaimsPrincipal` from `IHttpContextAccessor` and applies the options.
- **`IAuditUserEnricher`** in the core package - optional scoped hook invoked after the resolver produces an `AuditUser`. Lets consumers replace display name / type / other metadata from an IdP or LDAP directory. Synchronous by design (composes with the synchronous `IAuditUserResolver.Resolve` contract); consumer implementations MUST cache directory lookups because the interceptor is on the SaveChanges hot path. Returning `null` drops attribution entirely; throwing aborts SaveChanges.
- **`AddOrionAuditClaimResolver(this IServiceCollection, configure?)`** DI helper - registers the claim-driven resolver, removes any previously-registered `IAuditUserResolver` (typically the default `HttpContextAuditUserResolver` wired by `AddOrionAuditAspNetCore`), and wires `IHttpContextAccessor` + `IOptions<ClaimAuditUserResolverOptions>`. Idempotent.

### Migration from v0.7.4

Source-compatible. The default `HttpContextAuditUserResolver` is unchanged for consumers who keep using it. Opt in to the claim-driven path:

```csharp
services.AddOrionAuditClaimResolver(o =>
{
    o.IdClaimTypes.Insert(0, "employee_id");       // try internal claim first
    o.TypeClaimType = "idp_kind";                   // "interactive" / "service-principal"
});

// Optional enrichment hook (LDAP / Graph API; consumers must cache).
services.AddScoped<IAuditUserEnricher, MyLdapEnricher>();
```

### Tests

12 new `ClaimAuditUserResolver` / `AddOrionAuditClaimResolver` facts; existing 6 `HttpContextAuditUserResolver` facts unchanged. Total AspNetCore suite: 18 facts.

## [0.7.4] - 2026-06-09

### Added

#### `Moongazing.OrionAudit.MySql` (NEW PACKAGE) - MySQL / MariaDB integration

Adds MySQL / MariaDB-aware entity configuration so consumers on the Pomelo or Oracle EF Core providers can apply OrionAudit with one call instead of hand-rolling column types.

- **`OrionAuditColumnHints.MySqlJson`** (= 4) maps `Diff` and `Snapshot` to native `json` columns (MySQL 5.7+, MariaDB 10.2+). The native `json` type validates payload shape at write time and is queryable with `JSON_EXTRACT`. On MariaDB it is an alias for `LONGTEXT` but still participates in the JSON SQL functions.
- **`OrionAuditColumnHints.MySqlLongText`** (= 5) maps both columns to `longtext` for legacy MySQL builds without native JSON validation. Existing Sql Server / Postgres / Sqlite hints unchanged.
- **`OrionAuditMySqlModelBuilderExtensions.ApplyOrionAuditMySqlConfigurations(this ModelBuilder, DbContext, useLongText, ...)`** forwards through to the existing DbContext-aware `ApplyOrionAuditConfigurations` overload with the right hint pre-selected. Default `useLongText: false` uses `MySqlJson`; pass `true` for the LONGTEXT variant.
- Existing custom column / table-name overrides flow through unchanged.

### Deferred

Remaining v0.7.x items keep their targets:

- LDAP / IdP user resolution hooks -> v0.7.5

### Migration from v0.7.3

Source-compatible. Adopt the new entity hint by either:

```csharp
// One-call DbContext-aware overload (recommended):
modelBuilder.ApplyOrionAuditMySqlConfigurations(this, useLongText: false);

// OR explicit hint via the existing API (no extra package needed):
modelBuilder.ApplyOrionAuditConfigurations(this, columnHints: OrionAuditColumnHints.MySqlJson);
```

Consumers staying on SQL Server / Postgres / Sqlite see no behaviour change.

## [0.7.3] - 2026-06-09

### Added

#### Viewer per-entity / per-field display labels

Consumer-friendly labels flow through the existing `AuditViewRenderer` so the viewer can show `"Net"` for a property captured as `SubTotal`, `"Sales Order"` for the `Order` CLR type, etc. without renaming the entity or the schema.

- **`AuditTypeBuilder<T>.Label<TProp>(selector, displayLabel)`** - assigns a per-property label. Example: `o.Audit<Order>(b => b.Label(o => o.SubTotal, "Net"));`
- **`AuditTypeBuilder<T>.Label(displayLabel)`** - assigns an entity-level label. Example: `b.Label("Sales Order")`.
- **`AuditableTypeConfig.EntityLabel`** + **`AuditableTypeConfig.FieldLabel(propertyName)`** - public read-only accessors so consumers building custom viewers can resolve labels directly.
- **`AuditViewRenderer.Render(AuditLog, IAuditConfiguration)`** + **`Render(AuditLog, IAuditConfiguration, customColumns)`** - new overloads that decorate the view with labels. The existing parameterless `Render` overloads are unchanged; consumers who do not want labels see no behaviour change.
- **`AuditEntryView.EntityDisplayLabel`** + **`FieldChange.DisplayLabel`** - new optional properties on the view types. Null when no label is configured; the viewer falls back to the property path / CLR type name.

### Label resolution

- Labels resolve through the row's `EntityType` AQN via `Type.GetType`. When the type cannot be resolved (legacy row from another assembly, AQN missing) labels fall back to null - the viewer surfaces the raw property path / type name and never throws.
- Nested property changes (`/ShippingAddress/Street`) inherit their root property's label so a single `b.Label(o => o.ShippingAddress, "Ship-to")` covers `Street` / `City` / `PostalCode` together.

### Deferred

Remaining v0.7.x items keep their previously published targets:

- MySQL / MariaDB provider matrix -> v0.7.4

### Migration from v0.7.2

Source-compatible. The new `Label(...)` builder methods and `Render(..., config)` overloads are additive; existing `Render(AuditLog)` callers see byte-for-byte identical output.

## [0.7.2] - 2026-06-09

### Added

#### `AuditFor<TBase>()` inheritance-aware query

Completes the TPH/polymorphic capture pipeline: rows stamped with the new `AuditLog.EntityBaseType` column (v0.7.1) are now reachable through the existing query API by passing the base type. The runtime CLR type stays on `AuditLog.EntityType`, so consumers can still narrow to a concrete subclass.

- `AuditFor<T>(this DbContext, bool crossTenant = false)` now matches when **either** `EntityType` equals the AQN of `T` **or** `EntityBaseType` equals the `FullName` of `T`. A row stamped `EntityType=MyApp.Invoice` + `EntityBaseType=MyApp.Document` returns from both `AuditFor<Invoice>()` (concrete-type narrow) and `AuditFor<Document>()` (hierarchy roll-up).
- Pre-v0.7.1 rows carry `EntityBaseType=null` and continue to match only via the exact-type predicate, preserving v0.7.0 query semantics for legacy data.

xmldoc on `AuditFor<T>` documents the resolution rule, the legacy-row behaviour, and the relationship to the `[Auditable(typeof(TBase))]` / `UseBaseType<TBase>()` declarations from v0.7.1.

### Deferred

Remaining v0.7.x items keep their previously published targets:

- Viewer per-entity / per-field labels -> v0.7.3
- MySQL / MariaDB provider matrix -> v0.7.4

`ROADMAP.md` already reflects these targets.

### Migration from v0.7.1

Source-compatible. `AuditFor<T>` extends the WHERE predicate from `EntityType == typeof(T).AQN` to `EntityType == typeof(T).AQN || EntityBaseType == typeof(T).FullName`. Existing concrete-type queries continue to return the same rows; only base-type queries gain the new hierarchy roll-up.

## [0.7.1] - 2026-06-04

### Added

#### TPH / polymorphic capture (first slice)

The TPH / polymorphic-entity-capture promise from v0.7.0 lands in v0.7.1 with the schema column and capture-side stamping. Inheritance-aware querying (so `AuditFor<TBase>()` returns the full hierarchy) lands in v0.7.2.

- **`AuditLog.EntityBaseType`** new nullable column. Holds the declared base type's `Type.FullName` for entities whose configuration declares a base, otherwise stays null. The capture interceptor stamps it; the EF Core configuration maps it as a nullable `string` with `HasMaxLength(512)`.
- **`AuditableAttribute(Type baseType)`** new constructor overload. Declarative path: `[Auditable(typeof(Document))]` on a derived entity records the base type for capture.
- **`AuditTypeBuilder<T>.UseBaseType<TBase>()`** new fluent method. Programmatic path: `o.Audit<Invoice>(b => b.UseBaseType<Document>())` records the base type without touching the entity class.
- **`AuditableTypeConfig.BaseType`** new public read-only property carrying the declared base type, accessible to consumers building custom capture extensions.
- **`AuditConfigurationBuilder`** picks up the base type from both paths (attribute and fluent) at `Build()` time so the resolved configuration is uniform.

### Migration from v0.7.0

The new `EntityBaseType` column is **nullable**; existing audit rows leave it null. Consumers should add a column migration for the new property:

```csharp
migrationBuilder.AddColumn<string>(
    name: "EntityBaseType",
    table: "OrionAudit_Log",
    type: "character varying(512)",
    maxLength: 512,
    nullable: true);
```

Existing capture behaviour stays unchanged for entities that do not declare a base type. The new column carries values only for entities decorated with the new `[Auditable(typeof(TBase))]` or the new `UseBaseType<TBase>()` fluent call.

### Deferred from v0.7.1

- **`AuditFor<TBase>()` inheritance-aware querying** -> v0.7.2. The current reconstructor + read API stays at the runtime CLR type; the v0.7.2 work adds an inheritance filter that consults `EntityBaseType` alongside `EntityType`.
- **Viewer per-entity / per-field labels** -> v0.7.3.
- **MySQL / MariaDB provider matrix** -> v0.7.4.

`ROADMAP.md` reflects the new targets.

## [0.7.0] - 2026-06-01

Minor release focused on the publisher hook from the original v0.7.0 theme ("Outbox &
polymorphic capture"). The other three items on that roadmap entry are deferred to follow-on
patches so this release ships at quality. See `### Deferred from v0.7.0` below for the new
target versions.

### Added

- **`IAuditEventPublisher` hook.** First-class extension point invoked from inside the capture
  transaction (sync mode) or the dispatcher transaction (async-capture mode). Consumers can fan
  `AuditLog` rows out to downstream pipelines (message broker, search indexer, webhook) without
  writing a custom `SaveChangesInterceptor`. A publisher exception aborts the same transaction
  that holds the audit write, so either both the row exists and the publisher was called, or
  neither. Resolves the v0.2.0 "considered but not promised" outbox hook item.
- **`AuditLogEvent` wire shape.** Public record mirroring `AuditLog` columns. Stays decoupled
  from the EF entity type so downstream consumers (broker bindings, indexers) do not depend on
  the persisted entity.
- **`NullAuditEventPublisher`.** Default registration when nothing is wired. Allocation-free
  no-op; existing consumers see zero behaviour change.
- **`ChannelAuditEventPublisher`.** In-process default backed by a bounded
  `System.Threading.Channels.Channel<AuditLogEvent>` with `BoundedChannelFullMode.Wait` and a
  single dedicated reader task that invokes a consumer-supplied
  `Func<AuditLogEvent, CancellationToken, ValueTask>` delegate per event. Intentionally
  toy-grade: suitable for monoliths and tests; production deployments that need at-least-once
  delivery to a real broker should write their own `IAuditEventPublisher` against RabbitMQ /
  Azure Service Bus / Kafka / etc. and call `UseEventPublisher<TPublisher>()`. Implements
  `IAsyncDisposable` so the DI container drains it on shutdown.
- **DI builder methods.** `o.UseEventPublisher<TPublisher>()` registers a custom publisher as
  a singleton; `o.UseChannelEventPublisher((evt, ct) => ..., opts => ...)` registers the
  channel-based default with a consumer-supplied per-event delegate. Both are mutually
  exclusive; the latter call wins if both are made.
- **Publisher telemetry.** Counter `orionaudit.events.published` bumps on every published
  event; counter `orionaudit.events.dropped` bumps on handler exceptions in
  `ChannelAuditEventPublisher` and on shutdown-abandoned events. ActivitySource span
  `OrionAudit.Publish` wraps every per-event handler invocation in the channel publisher.

### Changed

- `AuditSaveChangesInterceptor` calls `IAuditEventPublisher.PublishAsync` BEFORE returning
  from `SavingChangesAsync` in sync-capture mode, so a publisher exception aborts the consumer
  transaction.
- `AuditDispatcher` calls `IAuditEventPublisher.PublishAsync` BEFORE its own `SaveChangesAsync`
  in async-capture mode, so a publisher exception aborts the dispatcher batch (the queue rows
  stay claimed-but-undeleted and become available for retry after `ClaimLease`).
- `OrionAudit` `ActivitySource` / `Meter` version bumped to `0.7.0`.

### Deferred from v0.7.0

The original v0.7.0 roadmap entry listed four items. Three are deferred to keep this release
focused on the publisher hook:

- **TPH / polymorphic entity capture** retargeted to **v0.7.1**. `[Auditable(BaseType = typeof(Document))]`
  plus a new `EntityBaseType` column on `AuditLog` so `AuditFor<Document>()` can return the
  full inheritance hierarchy.
- **Viewer per-entity / per-field display labels** retargeted to **v0.7.2**.
  `o.Label<Order>(o => o.SubTotal, "Net")` and the viewer surface to render it.
- **MySQL / MariaDB provider matrix** retargeted to **v0.7.3**. `MySqlText` column hint plus
  integration tests against the provider.

### Migration from v0.6.x

- **Existing consumers:** no code change required. The default `NullAuditEventPublisher`
  registration means `AddOrionAudit` callers who do not opt into a publisher see zero behaviour
  change.
- **Adopting the publisher hook:** add `o.UseChannelEventPublisher(...)` for the in-process
  default, or implement `IAuditEventPublisher` and call `o.UseEventPublisher<MyPublisher>()`.
  No schema impact.

## [0.6.2] - 2026-05-26

### Fixed

- Packaged logo is now actually the cream-bg version. v0.6.1 shipped the per-csproj copy of the old transparent logo because csproj `<None Include="docs/logo.png">` resolves relative to the csproj, not the repo root. Per-csproj copies are now synced to the cream-bg root file. No functional change.

## [0.6.1] - 2026-05-26

### Changed

- Logo now ships with a cream (#F7F1E3) background instead of transparent. Improves contrast against dark-mode README rendering and NuGet package card backgrounds. No functional change.

## [0.6.0] - 2026-05-24

Developer Experience release. Two opt-in additions that unlock common adoption scenarios:
extensible `AuditLog` rows for custom indexable dimensions, and bulk legacy-history import
with byte-equal diffs.

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
  picks up registered `CustomColumn`s automatically from the application service provider.
  The parameter-list overload also gained a `customColumns` parameter for advanced scenarios.
- `IAuditConfiguration` gained a `CustomColumns` collection.
- `AuditDispatcher` now resolves `IAuditConfiguration` from DI to apply custom columns
  during dispatch.
- `OrionAudit` `ActivitySource` / `Meter` version bumped to `0.6.0`.

### Migration from v0.5.x

- **Sync consumers not using `AddColumn` or import:** no code change.
- **Schema:** one EF migration adds `OrionAudit_Capture_Queue.CustomColumnsJson` (nullable
  text). The column is always mapped; it stays NULL when empty. Same precedent as v0.2.0's
  `SnapshotCursor` and v0.5.0's queue table.
- **Adopting `AddColumn`:** one EF migration per column on `OrionAudit_Log`. Pair with
  `migrationBuilder.CreateIndex(...)` if you'll filter on it. Switch `OnModelCreating` to
  `modelBuilder.ApplyOrionAuditConfigurations(this);` so registered columns are picked up
  automatically.
- **Adopting `AuditImportBuilder`:** opt-in API; no schema impact beyond the queue-column
  migration above. `ImportBatch` is mandatory — pick a stable per-import string.

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
