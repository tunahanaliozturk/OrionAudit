<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionAudit will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `Microsoft.CodeAnalysis.PublicApiAnalyzers` on all five packable projects (`OrionAudit`,
  `OrionAudit.AspNetCore`, `OrionAudit.MySql`, `OrionAudit.Viewer`, `OrionAudit.Testing`), each with
  a `PublicAPI.Shipped.txt` baseline and a `PublicAPI.Unshipped.txt` holding only `#nullable enable`.
  The baselines record the surface exactly as 1.0.0 shipped it — 938 / 9 / 2 / 7 / 34 entries
  respectively — so the stability promise 1.0.0 made is now enforced by the compiler instead of by
  memory. With `TreatWarningsAsErrors` on, adding public API without recording it fails the build
  (RS0016) and removing or changing recorded API fails the build (RS0017), on every one of
  `net8.0`, `net9.0` and `net10.0`. No code changed; this is a guard, not a behaviour change.
  Adding public API now means adding a line to `PublicAPI.Unshipped.txt`; the release cut promotes
  those lines into `PublicAPI.Shipped.txt`. See [CONTRIBUTING.md](CONTRIBUTING.md#public-api).

## [1.0.0] - 2026-09-20

The public API is stable from here. Any breaking change after this release requires 2.0.0.

What made this a 1.0.0 rather than an 0.12.0 is that the test gate was not one. The five test
projects are xunit.v3 on Microsoft Testing Platform with no VSTest adapter, so `dotnet test`
discovered nothing, ran nothing, and exited 0 — and every pull request since the suite was written
had merged on that green check. Fixing the gate turned a zero-test run into a 579-test run and
surfaced everything below it: a synchronous `SaveChanges()` that captured no audit rows at all,
`[RedactedAudit]` values persisted in plaintext under `UseLazyLoadingProxies()`, pooled contexts
attributing every row to the first request's user, stored XSS in the viewer, a viewer any
authenticated user could read, a tenant filter that failed open, a reconstructor that merged two
tenants into one entity, a hash-chain anchor lock that was never actually held, and a retention
sweep that broke the chain permanently on its first purge.

Several fixes are breaking. They are listed together immediately below; the sections after that
describe every change in full.

### Breaking changes

1. **`MapOrionAuditViewer` requires an explicit access decision.** A registration that named no
   policy used to fall through to a bare `RequireAuthorization()` — any authenticated user could
   read the entire audit trail. The viewer now throws `InvalidOperationException` at startup
   instead. **You will hit this as a startup failure on the first run after upgrading.** Name the
   decision at the call site:

   ```csharp
   app.MapOrionAuditViewer<AppDbContext>("/audit", o => o.RequireAuthorization("AuditViewers"));
   ```

   `o.RequireAuthorization(p => p.RequireRole("Auditor"))` (new inline-policy overload),
   `o.RequireAuthorization(p => p.RequireAuthenticatedUser())` (the old default, chosen
   deliberately) and `o.AllowAnonymous()` (local development) all satisfy it. Registrations that
   already named a policy or called `AllowAnonymous()` need no change.

2. **Pooled and factory-registered contexts refuse to guess the user.** `AddDbContextPool`, and
   `AddDbContextFactory` at its default `Singleton` lifetime, run the options lambda once from the
   **root** provider, so `UseOrionAudit(sp)` captured the root provider and every later save
   resolved the *first* request's `IAuditUserResolver` / `IAuditTenantResolver`. Rather than keep
   writing a trail that names the wrong person, the first audited save — and the first
   tenant-scoped read — now throws `OrionAuditConfigurationException`. Push the real request scope:

   ```csharp
   using var scope = AuditScope.PushServices(httpContext.RequestServices);
   await db.SaveChangesAsync();
   ```

   Or switch that registration to `AddDbContext<T>((sp, o) => o.UseOrionAudit(sp))`, which is
   unaffected and always was. Single-tenant applications with no resolver registered are
   unaffected, as is `crossTenant: true`.

3. **Two schema additions — one migration.** `OrionAudit_Log` gains a nullable `ChainSequence`
   (`bigint`), and `OrionAudit_Chain_Anchor` gains `PrunedRowCount` (`bigint`, defaults to `0`) and
   `PrunedThroughHash` (`char(64)`, nullable). All three are emitted by the shipped entity
   configurations, so one migration covers them:

   ```bash
   dotnet ef migrations add OrionAudit_1_0_0 --context AppDbContext
   dotnet ef database update --context AppDbContext
   ```

   **No backfill is needed and none should be attempted.** Every column defaults to "not known" /
   "never pruned", and both the walk and the verification treat those defaults as the pre-upgrade
   behaviour exactly. **The schema change itself is rolling-safe**: a 0.11.3 process simply leaves
   `ChainSequence` null, and rows it writes while a 1.0.0 process is already appending keep their
   place in write order rather than being dragged to the front of their stream. That is a statement
   about the *columns* only — if you use `UseHashChain`, read step 4 before you deploy. Consumers
   who create the audit tables outside EF migrations need the three columns added by hand.

4. **`UseHashChain` users must not run 0.11.3 and 1.0.0 writers against the same audit database at
   the same time.** This is a step to perform *before* deploying, not a note to read afterwards.

   One of the defects this release fixes is that 0.11.3's anchor lock was never actually held — it
   was acquired and released before the anchor head was read. A 1.0.0 writer takes and holds that
   lock correctly, but it cannot serialize against a 0.11.3 writer that is not honouring it. So
   during an overlap window, an old process and a new process writing the **same stream**
   concurrently can both read head `H` and both stamp `PreviousHash = H`. That is a **fork**, not a
   mis-ordering: `ChainSequence` orders a chain, it cannot repair one that branched, and no later
   verification or re-anchoring undoes it. `VerifyChainAsync` will report `BrokenLink` on a trail
   nobody tampered with, permanently.

   So do not roll the two side by side. Drain the 0.11.3 instances fully — let in-flight saves
   finish and stop them accepting new audited work — then start the 1.0.0 instances. A blue/green
   cutover works if the old side is drained before the new side takes traffic; an instance-by-
   instance rolling restart does not, because it is defined by the two versions serving at once.
   Apply the step-3 migration first; it is backward compatible, so a 0.11.3 process runs against
   the migrated schema unchanged, which is what lets you migrate before the cutover rather than
   during it.

   **Consumers who do not enable `UseHashChain` are unaffected and need no drain.** Without
   chaining there is no anchor, no lock, and nothing stamped — the only thing that reaches them is
   one nullable column that no 0.11.3 code writes and no 1.0.0 code reads for them. Roll normally.
   The same is true if your streams are already partitioned so that one stream is only ever written
   by one process; the requirement is about two versions writing *one* stream, not about the two
   versions coexisting as such.

5. **A retrying execution strategy now has to own its own transaction — but only with
   `UseHashChain`.** EF Core allows a transaction inside a retriable unit only from the code that
   owns the `SaveChanges` call, and an interceptor is not that code. If your `DbContext` uses
   `EnableRetryOnFailure()` *and* you enable hash chaining, the first chained save throws
   `OrionAuditConfigurationException` carrying the snippet you need:

   ```csharp
   var strategy = db.Database.CreateExecutionStrategy();
   await strategy.ExecuteAsync(async () =>
   {
       await using var transaction = await db.Database.BeginTransactionAsync();
       await db.SaveChangesAsync();
       await transaction.CommitAsync();
   });
   ```

   Consumers who do not enable hash chaining are unaffected.

6. **A raised dependency floor.** On `net10.0`, `OrionAudit` and `OrionAudit.MySql` now require EF
   Core 10.0.12 and can no longer resolve against EF Core 9; `net8.0` / `net9.0` consumers move
   from EF Core 9.0.0 to 9.0.20. `Microsoft.Extensions.DependencyInjection.Abstractions`,
   `.Hosting.Abstractions` and `.Logging.Abstractions` move to 10.0.12 on every target, which is
   not optional — EF Core 10.0.12 lifts the transitive minimum above a direct 9.0.0 reference and
   fails restore with `NU1605`. No public API changed with either.

### Security

- **`IAuditReconstructor` no longer replays other tenants' audit rows.** `AuditReconstructor`
  queried `context.Set<AuditLog>()` directly, which is the one read path that never went through
  the tenant filter `AuditFor<T>()` and `AuditLog()` apply. Both `ReconstructAsync<T>()` and
  `ReconstructManyAsync<T>()` therefore replayed **every** tenant's history for the requested
  entity id into a single object. Two failures at once: tenant A's field values were handed to
  tenant B, and the reconstruction itself was wrong — tenant B's diff applied on top of tenant A's
  snapshot produces an entity that existed in no tenant, silently, with no error to notice it by.
  Deployments that stamp a tenant and reconstruct by an id that is not globally unique (a per-tenant
  sequence, a natural key) are the exposed case; single-tenant deployments are unaffected.

  Both queries now start from the same tenant-scoped audit query the read DSL uses, so the
  reconstructor inherits its semantics exactly, including the unresolved-tenant deny below: a
  registered `IAuditTenantResolver` that cannot name a tenant scopes the replay to the no-tenant
  stream instead of widening it. The scoping logic now lives in exactly one place rather than two.
  No public signature changed — `IAuditReconstructor` deliberately gained no `crossTenant` escape
  hatch, because a cross-tenant replay is not a wider read but an incorrect entity; read across
  tenants with `AuditFor<T>(crossTenant: true)`, which returns rows rather than merging them.

- **BREAKING — `MapOrionAuditViewer` no longer defaults to "any authenticated user".** A
  registration that stated no access decision fell through to a bare `RequireAuthorization()`, so
  every logged-in account — every customer, every low-privilege internal user — could read the
  complete change history of every audited entity, other users' actions included, and see values
  that the redaction feature exists to keep out of reach. The README steered consumers to a named
  policy, but the safe configuration was the opt-in one: skip that paragraph and you shipped your
  audit log to your whole user base with nothing saying so. The viewer now refuses to register
  until the decision is explicit, and throws `InvalidOperationException` at startup naming the
  calls that satisfy it.

  **You will hit this as a startup failure on the first run after upgrading.** The one-line fix,
  at the `MapOrionAuditViewer` call site:

  ```csharp
  app.MapOrionAuditViewer<AppDbContext>("/audit", o => o.RequireAuthorization("AuditViewers"));
  ```

  Any of these also satisfies it: `o.RequireAuthorization(p => p.RequireRole("Auditor"))` (new
  inline-policy overload), `o.RequireAuthorization(p => p.RequireAuthenticatedUser())` (the former
  default, restored deliberately — use it only if every authenticated user really is entitled to
  the audit trail), or `o.AllowAnonymous()` (unchanged; local development only). Registrations
  that already named a policy or called `AllowAnonymous()` are unaffected.

- **Fixed stored XSS in `OrionAudit.Viewer`.** The viewer page built its markup in the browser by
  concatenating audit values into `innerHTML` (`wwwroot/index.html`), so any value an attacker could
  write into an audited entity - a display name of `<img src=x onerror=...>`, for example - executed
  as script in the session of whoever later reviewed the audit log, which is an administrator by
  definition. The entry list is now rendered server-side in `OrionAuditViewerStaticFiles` and every
  audit value passes through `HtmlEncoder.Default` on the way out; the one remaining script in the
  page only writes `textContent`, which the browser never parses as markup. Every interpolation site
  is an HTML text node - no audit value reaches an attribute, a `<script>` block, or a JSON island -
  so the HTML encoder is the correct encoder at each of them. The JSON API (`/api/log`,
  `/api/{entityType}/{key}`, `/api/meta`) is unchanged and still returns raw values.

  **Consumer-visible change:** the viewer's root page now resolves `TDbContext` and
  `IAuditConfiguration` per request, exactly as the JSON API endpoints in the same route group
  already did. A host that registered the viewer is unaffected; the page simply arrives filled in
  rather than filling itself in from a follow-up `fetch`.

- **Fixed a fail-open tenant filter on the audit read path.** `AuditQueryExtensions.AuditFor<T>()` and
  `AuditLog()` apply a tenant filter when an `IAuditTenantResolver` is registered. When that resolver
  returned null - a dropped header, a claim the gateway did not forward, a background thread with no
  ambient context - the filter fell through **unfiltered** and handed the caller every tenant's audit
  rows. The read widened to all tenants at exactly the moment the caller's identity was unknown, and
  it carried into everything built on those extensions, including the viewer's `/api/log` and
  `/api/{entityType}/{key}`. An unresolved tenant now denies: the read is scoped to the no-tenant
  stream (`TenantId` null or `""`), which is the read-side mirror of the canonical value the write
  path persists. In any tenant-stamped deployment that is the empty set; a genuinely single-tenant
  deployment, whose resolver returns null by design, still reads its own history unchanged. A
  deliberate empty result rather than a throw - these extensions run on request paths, and the
  library reserves exceptions for configuration and programming boundaries. `crossTenant: true` is
  still the explicit, auditable way to read across tenants, and an application with no resolver
  registered at all is unaffected.

### Fixed

- **`VerifyChainAsync` no longer reports `BrokenLink` when two concurrent writers touch one entity.**
  A stream's chain order is decided by which writer wins the anchor lock, but `OccurredOnUtc` is
  stamped near the *start* of capture, long before that. For two concurrent same-stream writers the
  two orders can therefore invert: the writer holding the earlier timestamp loses the race and lands
  second in the chain. The walk ordered by `OccurredOnUtc` first, so it read that stream backwards
  and reported tampering on a chain nobody had touched — two requests hitting one entity at the same
  moment was enough. Reproduced deterministically, 12 runs out of 12, and it also surfaced as an
  intermittent failure in the suite's own concurrency test.

  The walk now derives the order from `ChainSequence`, which the writer assigns under the anchor lock
  and which therefore *is* the chain's order, with no timestamp comparison able to distort it. The
  ordering moved out of SQL and into `AuditChainVerifier.VerifyStream` itself, so the walk cannot be
  got wrong by a caller sorting the rows the obvious way, and rows written before `ChainSequence`
  existed are still handled: a stream with no sequenced rows comes back exactly as the database
  returned it (so a pre-upgrade chain verifies bit for bit as before, including the database's own
  tie-breaking on `Id`), a stream with no unsequenced rows is ordered purely by sequence, and a
  stream holding both — a rolling deployment, where an old build keeps appending unsequenced rows
  after new ones — is merged by timestamp, which is the only comparison two builds' rows share. Each
  side keeps its own exact order through that merge whatever the timestamps say.

  The retention sweep's prune watermark had the same dependency and is fixed with it:
  `PrunedThroughHash` is now taken from the newest row the sweep actually removed, in the same chain
  order the verifier walks, instead of asking the table for the "oldest surviving row" — an ordering
  question no `ORDER BY` can answer correctly. That is one query fewer, and the two can no longer
  disagree about which row the chain's head is.

  `OccurredOnUtc` is deliberately unchanged: it still records when the change was captured, not when
  the audit row reached the front of the queue. Stamping it after the anchor lock would have made it
  agree with chain order at the cost of changing what the column means for every consumer, including
  those not using the chain, and once the walk uses the sequence there is nothing to gain by it.

- **The retention sweep can no longer prune a hole into a chain.** The sweep selects by age — which is
  what a retention policy means, and is not going to change — but age is not chain order, for the
  reason above. A "keep the newest N" boundary, or a batch bounded by `MaxRowsPerSweep`, could
  therefore fall between two rows that two concurrent writers had inverted and take the chain-*later*
  one while keeping the chain-earlier one. That is a deletion from the middle, and re-anchoring cannot
  repair it: the anchor records one watermark, not a set of holes. The next verification then reported
  a break on a trail retention itself had pruned.

  The selection is untouched, including its cross-stream age semantics. Instead the prune is narrowed
  to what can leave safely: per stream, the longest run that starts at the stream's current head and
  follows its `PreviousHash` links unbroken. Anything after the first gap stays for a later sweep, and
  goes as soon as the row that blocked it ages out too, so the two leave together as a contiguous
  head. Nothing is deleted that the policy did not ask for — the blocking row is by definition still
  inside the retention window, so extending the prune over it was never an option. The check is the
  chain itself rather than a column standing in for it, so it costs no query, needs no
  `ChainSequence`, and holds for streams written before that column existed.

  Detection is unchanged in both directions: a mutated row still fails as `ContentMismatch`, and a
  deletion no sweep recorded still fails as `Truncated`. Holding rows back only ever removes *fewer*
  rows, so nothing about the truncation guard is relaxed.

- **Pooled and factory-registered contexts no longer attribute every audit row to the first
  request's user.** `UseOrionAudit(sp)` captures whatever provider EF Core hands the options lambda.
  With `AddDbContext<T>((sp, o) => ...)` that lambda runs once per scope, so `sp` *is* the request
  scope and attribution was always correct. With `AddDbContextPool` — and with `AddDbContextFactory`,
  whose `lifetime` argument defaults to `Singleton` — EF Core registers `DbContextOptions` as a
  singleton, so the lambda runs once from the **root** provider and the interceptor held that root
  provider forever: a scoped `IAuditUserResolver` / `IAuditTenantResolver` pulled out of it was the
  first request's instance on every later save. Three requests as alice / bob / carol all recorded
  `UserId=alice`, with the diffs correctly distinct — and the same wiring under `ValidateScopes`
  threw `InvalidOperationException` instead, so it failed loudly in Development and misattributed
  silently in Production. Tenant stamping was wrong the same way, on the read side too: the
  `AuditFor<T>()` tenant filter resolves through the same captured provider, so one tenant could be
  shown another tenant's history — with scope validation off, a caller whose own tenant was `t-2`
  got back `t-1`'s rows and none of its own.
  Capture now prefers an **ambient request scope** over the captured provider, on both the write and
  read paths: `AuditScope.PushServices(sp)` flows the real scope on `AsyncLocal` (the same primitive
  the correlation-id scope already used), which fixes pooling, both factory registrations, and
  background/console runners that own their scope. A pooled context is the one wiring where the
  request scope is provably unreachable — the lease carries no provider and the context's own
  `ApplicationServiceProvider` is the root one we already have — so when a resolver is registered and
  no scope was pushed, the first audited save now throws `OrionAuditConfigurationException` naming
  the three ways out, instead of writing a trail that looks healthy and names the wrong person.
  **The read path makes the same refusal from the same code**: the check lives in one internal
  `PooledAttributionGuard` that both the interceptor and the tenant filter call, so a pooled
  registration cannot mean one thing to a write and another to a read — which is exactly how the
  read side kept the hole after the write side closed it. The guard sits inside the single helper
  every tenant-scoped read funnels through, so anything routed through it later inherits the
  refusal. `crossTenant: true` is an explicit opt-out of tenant scoping and is unaffected, as is a
  single-tenant app with no resolver registered.
  `AddDbContext` is unchanged and unaffected. Non-pooled `AddDbContextFactory` cannot be detected the
  same way (Microsoft DI hands out the same scope type for the root container and every child scope,
  and `IsRootScope` is internal), so it is covered by `AuditScope.PushServices`, the documentation,
  and `ValidateScopes`.
- **`UseHashChain`'s anchor lock is actually held now, so concurrent same-stream writes really do
  serialize.** `EfCoreAuditHashChainWriter` issues its pessimistic anchor lock
  (`SELECT ... FOR UPDATE` / `WITH (UPDLOCK, HOLDLOCK)`) through `Database.ExecuteSqlRawAsync`,
  which joins the context's current transaction *only if one exists* - and on the default path none
  did. At interceptor time a plain `SaveChangesAsync` has no transaction (EF opens its own **after**
  the interceptor runs), and the async dispatcher never opened one at all. The lock was therefore
  acquired and released before the anchor was even read, so two concurrent same-stream saves both
  read head `H`, both stamped `PreviousHash = H`, and both committed; `AuditChainAnchor` carries no
  concurrency token, so the lost anchor update went unnoticed too, and verification then reported
  `BrokenLink` on a trail nobody had touched. The README's promise that same-stream writes
  "serialize on the anchor row inside your transaction" only held for consumers who happened to open
  a transaction by hand.

  Both write paths now open one around the stamp when the consumer has none
  (`ChainWriteTransaction`), so the lock, the head read, the stamped rows and the advanced anchor
  commit as one unit: the interceptor commits it in `SavedChanges` / `SavedChangesAsync` and releases
  it in `SaveChangesFailed(Async)` / `SaveChangesCanceled(Async)`, covering the synchronous and
  asynchronous entry points alike, and the dispatcher wraps its materialise-and-insert region. A
  transaction the consumer opened themselves is left alone - it already spans the stamp, and
  committing someone else's transaction is not ours to do. A provider without transaction support
  (the EF in-memory provider) raises on begin; that is caught and the work runs unwrapped, the same
  degradation `CopyToTableAuditArchiver` and `ChainPruneArchiver` already use. Consumers who never
  enable hash-chaining are untouched: no chain, no transaction, no schema change.

  On SQLite a contending same-stream save **waits** rather than failing: EF's `BeginTransaction` maps
  to Microsoft.Data.Sqlite's Serializable isolation, which emits `BEGIN IMMEDIATE`, so the second
  writer blocks at its own `BEGIN` on the connection's busy timeout (30 seconds by default) and then
  reads the head the first one committed. One `SaveChangesAsync` per writer is enough; no retry loop
  is needed. The exception is a *shared-cache in-memory* database (`mode=memory&cache=shared`), which
  serialises with table locks reporting `SQLITE_LOCKED` - something SQLite's busy handler does not
  wait on - so concurrent writers there fail rather than queue. That is a test-fixture shape, not a
  deployment one, and OrionAudit's own concurrency tests now use a file database accordingly.

  A save or dispatch batch touching **several** streams also takes their anchor locks in a fixed
  global order now (ordinal over the whole stream key), instead of whatever order the rows were
  captured in. A multi-stream batch holds each lock while it goes after the next, so two concurrent
  batches that both touched streams A and B and approached them in opposite orders held one each and
  waited on the other - a deadlock the database can only resolve by killing one of them. EF's own
  command ordering cannot prevent it, because these locks are raw statements issued before any of the
  batch's commands.

  **If your `DbContext` uses a retrying execution strategy (`EnableRetryOnFailure()`), you must own
  the transaction yourself.** EF Core allows a transaction inside a retriable unit only from the code
  that owns the `SaveChanges` call, and an interceptor is not that code - so OrionAudit cannot open
  one for you there, and cannot make your save retriable on your behalf either. Wrap your saves once:

  ```csharp
  var strategy = db.Database.CreateExecutionStrategy();
  await strategy.ExecuteAsync(async () =>
  {
      await using var transaction = await db.Database.BeginTransactionAsync();
      await db.SaveChangesAsync();
      await transaction.CommitAsync();
  });
  ```

  The chain then stamps inside your transaction and the guarantee is unchanged. Until you do, the
  first hash-chained save throws `OrionAuditConfigurationException` carrying that snippet, rather than
  EF's own message about user-initiated transactions, which never mentions OrionAudit. The refusal
  fires on that first save rather than at registration because whether a strategy *retries* can only
  be answered from a live `DbContext` (`Database.CreateExecutionStrategy().RetriesOnFailure`) - at
  registration all that is visible is that some strategy factory was supplied, which is equally true
  of a non-retrying custom strategy that works fine. The async-capture dispatcher needs nothing from
  you: it owns its own save, so it now runs the whole begin/stamp/save/commit unit through your
  strategy.
- **The chain is verified in the order it was written, not in an order that merely correlates with
  it.** A chain's real order is *insertion* order - each save chains its rows onto the anchor's
  current head - but the verifier walked `(OccurredOnUtc, Id)`. One timestamp is computed per
  `SaveChanges` and stamped on every row of that save, and MySQL `DATETIME(6)` / PostgreSQL
  `timestamp` truncate further, so two saves on one entity landing on the same stored timestamp is
  routine rather than exotic; the tie then fell to a random `Guid`, which bears no relation to the
  order the rows were chained in. Measured on SQLite: two saves on one entity under a frozen clock,
  40 trials, **23 spurious `BrokenLink` results on a completely intact chain** - a coin flip.

  `AuditLog` now carries `ChainSequence`, a per-stream sequence the writer assigns by continuing the
  anchor's `RowCount` under the anchor lock - which is why this and the lock fix are one change and
  not two - and the verifier, `ChainPruneArchiver` and every retention selection order by it. It is
  deliberately **not** bound into the row's MAC, so no existing chain's hashes change. Nothing is
  made lenient either: a mutated row still fails as `ContentMismatch`, a hand-deleted tail still
  fails as `Truncated`, and a deletion no retention sweep recorded still breaks the walk.

  **Consumer-visible changes:** the `OrionAudit_Log` table gains a nullable `ChainSequence`
  (`bigint`) column, so a consumer using migrations needs a migration for it -
  `dotnet ef migrations add AddOrionAuditChainSequence`, emitted by `AuditLogEntityTypeConfiguration`
  like every other audit column. **No backfill is needed and none should be attempted, and the
  column itself is rolling-safe** — but see the deployment step in Breaking changes above: the
  *column* tolerates an old build appending beside a new one, while the hash chain's **writer** in
  0.11.3 does not, because its anchor lock was never held. Two versions writing one stream at once
  can fork the chain, and ordering cannot repair a fork. A stream whose rows are all sequenced is
  walked purely by `ChainSequence`; a
  stream written entirely before the column existed is walked in exactly the order the database
  returned it; and a stream holding both - a rolling deployment, where an old build keeps appending
  unsequenced rows after new ones - merges the two groups on `OccurredOnUtc`, each side keeping its
  own exact order through the merge. So a row written before the column existed, or written *during*
  a rollout by an instance still on the old build, keeps its place in write order instead of being
  dragged to the front of its stream. (An earlier draft of this change ordered all unsequenced rows
  first, which reported `BrokenLink` on an intact chain for exactly that mixed-version case,
  permanently.) The one tie the merge cannot
  settle is two builds appending to the *same* stream within a single tick of the timestamp column -
  100ns on SQL Server and SQLite, a microsecond on PostgreSQL and MySQL - so a deliberately coarse
  column such as `datetime2(0)` is worth avoiding on a chained audit table. The unsequenced group is
  selected with an explicit sort key rather than relying on `ORDER BY` null placement, which differs
  between SQL Server/SQLite and PostgreSQL. `AuditHashChainStamper.Stamp` gained an optional trailing
  parameter carrying each stream's persisted row count; omitting it leaves `ChainSequence` unassigned,
  so an existing call site compiles and behaves as before.
- **Synchronous `SaveChanges()` is audited again.** `AuditSaveChangesInterceptor` implemented only
  `SavingChangesAsync`, so any caller using the blocking `context.SaveChanges()` overload wrote zero
  audit rows — silently, with no error raised. The capture pipeline is now a single private
  `CaptureAsync` shared by both entry points, with `SavingChanges` added as a thin sync wrapper, so
  the two paths cannot drift apart again. The opt-in legs that are genuinely async (the hash
  chain's anchor lock/read, `IAuditEventPublisher.PublishAsync`, and the periodic snapshot policy's
  cursor read) are awaited on that one pipeline rather than duplicated; with none of them wired the
  pipeline completes synchronously and the sync override never blocks. The sync path also clears
  the ambient `SynchronizationContext` for the
  duration of the capture: a consumer publisher that awaits without `ConfigureAwait(false)` would
  otherwise post its continuation back to the single-threaded context (WPF, WinForms, legacy
  ASP.NET) whose thread is blocked waiting for it, and the save would deadlock.
- **Capture works under `UseLazyLoadingProxies()`.** Capture resolved the audited entity's CLR type
  with `entry.Entity.GetType()`, which under lazy-loading (or change-tracking) proxies is the Castle
  subclass — `OrderProxy`, not `Order` — and is not the key anything is registered under. Every
  lookup missed: `IsAudited` returned false so entities loaded from the database produced no audit
  rows at all, and where a row was produced its field rules resolved to nothing, so
  `[RedactedAudit]` properties were persisted **in plaintext**. All three lookups now go through a
  single `ResolveClrType` helper backed by `entry.Metadata.ClrType`, which is the declared type
  whether or not proxies are in play.
- **OrionAudit's own entity types are no longer `sealed`.** EF Core's proxy plugin rejects *every*
  sealed entity type in the model, so mapping `AuditLog`, `SnapshotCursor`,
  `AuditCaptureQueueEntry`, or `AuditChainAnchor` made `UseLazyLoadingProxies()` throw at model
  build. Unsealing them is source- and binary-compatible for consumers.
- **`CorrelationId` records the caller's trace, not OrionAudit's own span.** The ambient
  `Activity.Current` was read *after* the interceptor had already started its `OrionAudit.Capture`
  span, so every row was stamped with OrionAudit's internal span id and could not be joined back to
  the request that produced it. The read now happens before any OrionAudit span is started. Nothing
  changes when no tracing listener is attached (`StartActivity` returns null and there was no span
  to shadow the caller's), which is why the existing `NoScope_FallsBackToActivityOrNull` test only
  failed intermittently — whenever a listener happened to be live in parallel.
- **Retention no longer makes hash-chain verification report tampering that never happened.** The
  retention sweep deletes the OLDEST rows of a stream, which the tamper-evident chain could not tell
  apart from an attacker deleting them: the surviving prefix no longer started at the genesis (its
  `PreviousHash` pointed at a row that was gone) and the walked row count no longer reached the
  stream's `AuditChainAnchor`. So from the first purge onward, every `VerifyChainAsync` on a pruned
  stream returned `BrokenLink` or `Truncated` - a permanent false positive that made the
  tamper-evidence feature useless, because a report that always cries wolf is a report nobody reads.

  `AuditChainAnchor` gains a retention checkpoint - `PrunedRowCount` and `PrunedThroughHash` - and,
  when hash-chaining is enabled, the sweep now re-anchors each stream it prunes at the oldest
  surviving row. Verification checks `walked + PrunedRowCount == RowCount` and expects the surviving
  genesis to link to `PrunedThroughHash`. Nothing else is relaxed: `RowCount` still records the
  stream's lifetime total, `LatestEntryHash` still pins its tail, a mutated row still fails its keyed
  MAC with `ContentMismatch`, and a deletion no sweep recorded still fails as `Truncated` or
  `BrokenLink`. Both anchor columns default to "never pruned" (`0` / `null`), so an anchor written
  before this verifies exactly as it did.

  The removal and the checkpoint that explains it commit in one transaction, so a cancellation, a
  transient database failure or a process exit between them cannot leave deleted rows paired with a
  stale checkpoint - which would be the same permanent false-tamper state, reached by a crash instead
  of by design. A provider without transaction support runs the work unwrapped, and
  `CopyToTableAuditArchiver` joins the sweep's transaction rather than starting its own.

  When retention empties a stream completely the watermark keeps the last pruned hash rather than
  being cleared. The anchor deliberately retains the deleted tail in `LatestEntryHash`, so the next
  change to that entity chains onto it; a cleared watermark would make verification expect that new
  row's `PreviousHash` to be null and report a broken link on a chain nobody touched.

  The pruned total is accumulated from the rows each batch actually removed rather than derived by
  subtracting a survivor count from `RowCount`. `RowCount` belongs to the append path, so a
  read-modify-write against it skewed whenever an append committed between the sweep's two reads -
  one prune went unrecorded and verification then reported truncation on an intact chain. Retention
  now writes only the columns it owns, so the two writers never contend and no lock or
  isolation-level assumption is needed.

  The sweep also selects rows through `AuditChainOrder.OldestFirst` / `NewestFirst` — age first,
  ties settled on `ChainSequence` — rather than by timestamp alone. Rows of one stream sharing a
  timestamp are routine, not exotic: one timestamp is computed per `SaveChanges` and stamped on
  every row of that save, and column precision truncates further, so under a bare timestamp ordering
  the provider is free to break those ties any way it likes. `Id` is a random `Guid` and orders tied
  rows in a way unrelated to how they were chained, which is why the tie-break is the sequence.
  Ordering alone cannot make the prune contiguous, though, and is not asked to — age and chain order
  can disagree outright. Re-anchoring at the oldest survivor repairs a pruned head; it cannot close a
  hole in the middle, and what keeps holes from opening is `ChainPruneArchiver` narrowing each batch
  to the contiguous run it can safely remove (see the entry above).

  **Consumer-visible changes:** the `OrionAudit_Chain_Anchor` table gains two columns, so a consumer
  using migrations needs a migration for them. With hash-chaining enabled the sweep also stops using
  its `ExecuteDelete` fast path and materialises each batch instead - the chain repair has to know
  which streams lost rows, which a bare `ExecuteDelete` never reveals. The batch is already bounded by
  `MaxRowsPerSweep`, and consumers without hash-chaining keep the fast path unchanged. Dry-run still
  deletes nothing and writes no checkpoint.
- **`ChannelAuditEventPublisher.DisposeAsync` no longer disposes a `CancellationTokenSource` the
  reader still holds.** The final `shutdownCts.Dispose()` ran unconditionally, including on the
  path where the post-cancel `readerTask.WaitAsync(drainTimeout)` timed out and the timeout was
  swallowed. A reader still sitting inside `ReadAllAsync(shutdownCts.Token)` then hit
  `ObjectDisposedException` on its next read; `ReadLoopAsync` catches only
  `OperationCanceledException`, so it resurfaced as an unobserved task exception during host
  shutdown. The source is now disposed only once the reader has actually finished — immediately
  when it completed within the drain budget, otherwise from a continuation on the reader task.
- **Retrying `AuditImportBuilder.SaveAsync` after a partial flush no longer drops records and
  reports them as `Skipped`.** Flushing happens per `BatchSize`, but the buffer was cleared only
  after every flush had succeeded. When a later flush threw, the earlier batches were already
  committed and the buffer still held every record; the retry rebuilt its already-present set from
  the rows those batches wrote, and because records added without a `SourceId` all share the single
  `import:{ImportBatch}` correlation, the check matched *every* remaining record. They were counted
  as `Skipped` and never written — the README promised `Skipped` means "already present", and here
  it was returned for rows that were not. Two changes fix it: the already-present check now applies
  only to records that carry a `SourceId` (the only per-record identity there is), and only the
  records that actually reached the database leave the buffer, so a retry re-processes exactly the
  records that did not. A failed flush also detaches its batch so the retry cannot insert those
  rows twice.
- **A nested `[OrionAuditModule]` type no longer emits at the wrong nesting level.** The generator
  ignored `ContainingType`, so `[OrionAuditModule] partial class Registry` nested inside
  `class Startup` produced a *top-level* `partial class Registry` in the namespace: the consumer's
  `Startup.Registry.RegisterAuditedTypes(builder)` did not exist (CS0117) and an unrelated
  `Registry` type was quietly declared next to it. The whole containing chain is now re-declared
  around the emitted members, with each link's own accessibility. A module whose chain is not
  `partial` all the way out is reported as **OA0001** at the module's declaration instead of
  emitting a second declaration that cannot merge.
- **A generic `[OrionAuditModule]` type no longer loses its type parameters.** `partial class
  Module<T>` emitted `partial class Module` — an unrelated arity-0 type rather than a part of
  `Module<T>`, so `Module<T>.RegisterAuditedTypes` did not exist. The type parameter list and any
  constraint clauses are now emitted as declared, for the module and for every generic type it is
  nested in. Constraints are resolved from the type parameter symbols and written `global::`-
  qualified, with the declared nullable annotation: the generated file carries none of the
  consumer's `using` directives, so a constraint such as `where T : IMarker` — or one written
  through a `using` alias — would not resolve there if it were copied verbatim from the
  declaration (CS0246, then CS0265 against the part that does resolve it).
- **A `record` module or `[Auditable]` record is no longer skipped in silence.** The syntax
  predicate was `node is ClassDeclarationSyntax`; a record is a `RecordDeclarationSyntax`, so the
  declaration was dropped entirely and the consumer's only signal was a missing method at the call
  site with nothing pointing at the cause. The predicate now matches `TypeDeclarationSyntax` and
  the emitted part repeats the declaration's own keyword (`class`, `record`, `record class`, ...)
  rather than hard-coding `class`.
- **Two modules can no longer collide on a generated file's hint name and fail the build.** The
  hint was `{namespace with '.'→'_'}_{Name}`, so namespace `A.B` + class `C_D` and namespace
  `A.B.C` + class `D` both produced `A_B_C_D.OrionAuditModule.g.cs`; `AddSource` then threw
  `ArgumentException` with an opaque message and dropped every file the run had already produced.
  Nested modules (which took only their namespace) and `Module` vs `Module<T>` collided the same
  way. The hint now comes from the type's full metadata name — namespace, every enclosing type,
  generic arity — through an injective escape, in a new `HintNames` helper. Every `_` in the stem
  starts a self-delimiting escape: `__` is a literal underscore, `_n` nesting, `_g` generic arity,
  and `_u` plus four hex digits is any other character *by value*. Encoding the value is what makes
  it injective — a C# identifier may legitimately contain a combining mark or connector
  punctuation, so a single shared marker for every non-alphanumeric character maps two valid,
  distinct modules back onto one stem.
- **An `[Auditable]` type the generator cannot register is now reported, not dropped in silence.**
  A type whose accessibility chain was not public/internal, and an abstract one, were filtered out
  of the pipeline with nothing said: the consumer believed the type was audited and could only find
  out when no audit row was ever written for it. Both now report at the type's own declaration —
  **OA0003** for the accessibility chain (naming the container that fails) and **OA0002** for
  abstract. Neither fires when the compilation declares no `[OrionAuditModule]` at all, since
  nothing is generated then and the consumer is still on the reflective path.

  Reachability answers every `Accessibility` member deliberately. `public`, `internal` and
  `protected internal` are reachable — `protected internal` is protected *or* internal, and the
  internal half alone lets any type in the compilation name it. `private protected` (protected
  *and* internal), `protected` and `private` are not: same assembly is not enough, the caller must
  also derive from the container, and a generated module never does.

  The three diagnostics are `Usage` warnings and follow OrionGuard's `OG00xx` numbering. A fixture
  that is deliberately unregisterable (private nested, abstract) suppresses them at the
  declaration with `#pragma warning disable`.

### Changed

- **Import idempotency is documented as per-record, which requires `SourceId(...)`.** A record
  added without one is stamped with the batch-wide `import:{ImportBatch}` correlation, which
  identifies the batch and not the record, so it is never reported as `Skipped` and re-adding it
  to a fresh builder writes a second row. Retrying `SaveAsync` on the *same* builder after a
  failed flush is safe either way. README and the `AuditImportBuilder` / `AuditImportOptions` docs
  now say this instead of promising blanket re-run safety.

#### Build and CI

Nothing here reaches a shipped package, but it is why the rest of this release exists.

- **The CI test step actually runs the tests.** All five test projects are xunit.v3 on Microsoft
  Testing Platform with no VSTest adapter, so plain `dotnet test` discovered nothing, ran nothing,
  and exited 0 — and said so nowhere in the log. Every pull request since the suite was written had
  merged on a green check from a suite that never executed, which is how a `SaveChanges()` path
  capturing no audit rows reached a published package. A `global.json` selects the Microsoft Testing
  Platform runner, and the CI step now passes `-- --minimum-expected-tests 1`, so a run reporting
  zero tests fails instead of passing. The `--` is a gate on the gate as well: plain `dotnet test`
  rejects that argument rather than ignoring it, so silently dropping the runner opt-in breaks the
  build loudly. 0 tests running became 579.
- **`SnapshotPolicyCaptureTests.SnapshotEveryDuration_WritesOnFirstThenAfterElapsed` no longer races
  the wall clock.** With the suites actually running, its assumption that two consecutive
  `SaveChangesAsync` calls land inside a 100 ms window failed under load. It now drives the
  interceptor's existing `TimeProvider` seam, so it is deterministic and no longer sleeps. The
  snapshot policy itself was correct and is unchanged.
- **The benchmarks project builds warning-free, and its warnings are errors again.** It was the one
  project with `TreatWarningsAsErrors=false`, which is why three warnings sat there unnoticed.
  `CA1305` is fixed (`ToString(CultureInfo.InvariantCulture)`); `CA1707` is suppressed with a
  reason, because a benchmark method name is a label in the results table and the underscore
  separates the scenario from what is measured. The whole solution now builds with zero warnings,
  so a new one is a signal rather than noise.

#### Dependency refresh

- **EF Core 9.0.0 → 10.0.12 on `net10.0`, 9.0.20 on `net8.0` / `net9.0`.** EF Core 10 ships a
  `net10.0` asset only, so `OrionAudit` and `OrionAudit.MySql` now split
  `Microsoft.EntityFrameworkCore` / `.Relational` per target framework rather than dropping the
  older targets. **Consumers will notice a raised floor:** a `net10.0` consumer can no longer
  resolve OrionAudit against EF Core 9, and `net8.0` / `net9.0` consumers move from EF Core 9.0.0
  to 9.0.20. No public API or behaviour changed, and EF Core 10 required no source change on our
  side. Same shape and same comment as the sibling `OrionGuard.EntityFrameworkCore`.
- **`Microsoft.EntityFrameworkCore.Proxies` 9.0.0 → 10.0.12** in the integration tests, in lockstep
  with the EF Core version that project resolves. Proxies plugs into EF Core through internal
  extension points rather than its public API, so a 9.0.x Proxies on an EF Core 10 runtime is the
  mismatch that does *not* announce itself: restore succeeds (its `net8.0` asset is
  TFM-compatible with `net10.0`, and EF Core resolves to 10.0.12 by max-wins with no downgrade to
  report) and the failure surfaces later, at materialization. Test-only.
- **`Microsoft.Extensions.DependencyInjection.Abstractions`, `.Hosting.Abstractions` and
  `.Logging.Abstractions` 9.0.0 → 10.0.12, unconditionally.** Unlike EF Core, these still ship
  `net8.0` / `net9.0` / `netstandard2.0` assets at 10.0.12, so one reference covers every target.
  **This is also a raised floor on all three targets** — and it is not optional: EF Core 10.0.12
  pulls `Microsoft.Extensions.Caching.Memory` 10.0.12, which lifts the transitive minimum on these
  packages above a direct 9.0.0 reference and fails restore with `NU1605`.
- **`Orion.Abstractions` 1.0.0 → 1.2.0.** Additive only, on the frozen 1.x spine: 1.1.0 extended
  the separate `Orion.Abstractions.Testing` package, 1.2.0 added `OrionDeadline`. The one
  primitive OrionAudit uses (`SafeObserverInvoker.Resolve`) is unchanged, and 1.2.0 declares the
  same dependency minimums as 1.0.0, so the transitive graph is unchanged.
- **Test and benchmark tooling** (nothing here reaches a shipped package): `xunit.v3` 3.2.2 →
  4.0.1, `coverlet.collector` 6.0.2 → 10.0.1, `Microsoft.AspNetCore.TestHost` 10.0.0 → 10.0.12,
  `BenchmarkDotNet` 0.14.0 → 0.15.8.
- **The `global.json` MTP opt-in is now load-bearing, not just an improvement.** `xunit.v3` 4.0
  drops Microsoft Testing Platform v1 and ships v2. Under v1, `dotnet test` on the .NET 10 SDK
  printed nothing and exited 0; under v2 the same command hard-errors unless the Microsoft Testing
  Platform runner is selected. The `global.json` that selects it landed separately, so nothing
  changes here — but without it this bump would break the build rather than merely under-report.
- **`SQLitePCLRaw.bundle_e_sqlite3` stays pinned at 2.1.12** (not 3.0.5). 3.0.x is a restructured
  major — no `lib/` folder, and a new `SQLitePCLRaw.config.e_sqlite3` + `SQLite` native package
  pair — that `Microsoft.Data.Sqlite` does not reference, so forcing it would override what EF
  Core is built and tested against. The pin's original job is now done by EF Core itself
  (`Microsoft.EntityFrameworkCore.Sqlite` 10.0.12 depends on 2.1.12 directly), so it is kept as
  an explicit floor and its comment updated to say so. Test and sample projects only, as before.
- **`Microsoft.CodeAnalysis.CSharp` stays at 4.10.0**, in the generator project and in the
  generator tests that drive it, and the generator csproj now says why. A source generator's
  compile-time Roslyn version is the minimum Roslyn that can load it: building against 5.9 would
  require SDK 10.0.400+ from every consumer, while a generator built against 4.10 loads fine on
  newer Roslyn. This is a consumer-support floor, not a stale reference.

### Performance

- **The periodic snapshot policy no longer blocks a thread-pool thread on every audited save.**
  `SnapshotPolicyEvaluator` read the entity's `SnapshotCursor` with a synchronous
  `ctx.Set<SnapshotCursor>().Find(...)` from inside `SavingChangesAsync`, so `SnapshotEvery(...)`
  cost a blocking database round-trip on the async hot path for every audited update. It now uses
  `FindAsync`, which makes both call sites — the interceptor's capture pipeline and the async
  dispatcher's `BuildAuditLogAsync` — async through. Once the cursor is tracked, later saves on the
  same context resolve it from the change tracker without touching the database at all. The
  synchronous `SaveChanges()` entry point blocks on exactly the round-trip it always blocked on.

### Documentation

- **`SnapshotEvery(n)` is documented as approximate under concurrent writes to one entity, with no
  guaranteed interval in either direction.** `SnapshotCursor.UpdatesSinceLast` is read, incremented
  in memory, and written back as an absolute value, with no lock and no concurrency token. With no
  concurrent writes to the same entity that is exact — every save reads what the previous one
  wrote, and every n-th update snapshots. Under concurrency it is approximate both ways: two saves
  that both read `n - 1` both snapshot, so snapshots can land a single update apart; and because
  the write is a blind overwrite rather than an increment, a stale writer can lower the count (A
  reads 0, B commits 1, C commits 2, A commits its stale 1), which sustained contention can repeat,
  deferring a snapshot without bound. The value stays sane regardless — only ever a reader's value
  plus one, or 0 after a snapshot — so it never goes negative and never persists at or above `n`.

  Nothing about the trail itself is affected: no audit row is lost, delayed, or mis-stamped, the
  diff chain stays complete, and `AuditReconstructor` can always rebuild any state. `Snapshot` is
  purely a replay-cost optimisation, so a drifting cadence costs replay time and nothing else.

  **`SnapshotEvery(TimeSpan)` is the bounded variant.** It keys off `LastSnapshotUtc`, which is only
  ever written as some save's own timestamp and so can never be set further ahead than a save that
  really happened; a stale write moves it *earlier*, expiring the window sooner. Concurrency shows
  up there as an extra snapshot, never a late one, so the interval is not exceeded (absent clock
  skew between capture hosts).

  Guarding the counter was considered and **rejected**. A concurrency token makes the losing writer
  raise `DbUpdateConcurrencyException` out of the consumer's business transaction, naming a table
  they never asked for, and contradicts how the rest of capture isolates audit-side faults (a diff
  failure annotates the row, a custom-column provider failure annotates the row, an observer fault
  is swallowed); it would also have added a non-null `Version` column. Making the write monotonic
  is not reachable either: EF emits absolute values, no EF transaction exists yet at interceptor
  time (so relative SQL we issue ourselves would commit separately and count rolled-back saves),
  and EF's only conditional shape is the token again, because it checks rows-affected and throws
  when the stale writer matches none. Monotonicity alone would not restore a lower bound anyway —
  two savers reading `n - 1` both snapshot however the write is performed. There is **no schema
  change**.

## [0.11.3] - 2026-07-28

### Fixed

- **`OrionAudit.MySql` now ships its package icon and README.** The sub-package was missing the
  `PackageIcon` / `PackageReadmeFile` metadata (and had no `docs/` folder), so it displayed the
  blank NuGet placeholder while every other OrionAudit package carried the family logo. Added the
  logo, a package README, and the pack wiring to match `OrionAudit.AspNetCore`.
- **Re-delivers the sub-package icon fix that the `v0.11.2` tag never published.** `v0.11.2` was
  tagged and released on GitHub but `<Version>` was left at `0.11.1`, so `dotnet nuget push
  --skip-duplicate` treated every package as an already-published duplicate and shipped nothing.
  NuGet's latest stayed `0.11.1`. Bumping to `0.11.3` publishes a genuinely new version so the
  icon/README changes actually reach consumers.

### Changed

- **Converged onto the frozen `Orion.Abstractions` 1.0 spine.** Lifted the internal
  `Orion.Abstractions` reference from the pre-freeze `0.3.0` to `1.0.0`. It remains a dependency
  only — no OrionAudit public type exposes an `Orion.Abstractions` type — so this is transparent to
  consumers, and it stops the shipped package from pinning siblings to an unfrozen pre-release of
  the spine.

### Security

- **Pinned `SQLitePCLRaw.bundle_e_sqlite3` to 2.1.12** to clear [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) (High), a vulnerability in the bundled SQLite native library. `Microsoft.EntityFrameworkCore.Sqlite` 9.0.0 resolved `SQLitePCLRaw.lib.e_sqlite3` 2.1.10 transitively via `Microsoft.Data.Sqlite` -> `SQLitePCLRaw.bundle_e_sqlite3`; pinning the bundle lifts `core`, `lib.e_sqlite3`, and `provider.e_sqlite3` together to the patched 2.1.12.

  **No shipped package is affected, and no released version of OrionAudit needs to be upgraded.** The vulnerable native library reached only test and sample projects - `Moongazing.OrionAudit.Tests`, `Moongazing.OrionAudit.IntegrationTests`, `Moongazing.OrionAudit.Viewer.Tests`, and `Moongazing.OrionAudit.Sample.Console` - none of which are packable or published. No `src/` package referenced it, directly or transitively, so nothing consumers download from NuGet ever carried the vulnerable binary. The pin is a build-hygiene fix that keeps the repository's vulnerability scan clean; it can be dropped once the EF Core reference resolves 2.1.12 on its own.

## [0.11.1] - 2026-07-01

### Changed

#### Convergence onto Orion.Abstractions (pilot)

OrionAudit now takes an internal-use dependency on the shared `Orion.Abstractions` family package (0.3.0) and re-uses its primitives instead of a private copy. This is a convergence pilot: **no public API changes and no behavior changes** - the same fault-swallowing, the same emitted metrics and spans with the same names and tags, the same audit semantics. The full test suite passes unchanged.

- **Observer invocation.** The interceptor's fault-safe invocation of `IAuditCaptureObserver.OnCaptured(...)` now runs through `SafeObserverInvoker.Resolve` from `Orion.Abstractions` rather than a bespoke try/catch. This is the exact shared primitive that the v0.7.26 fix (resolve the observer *inside* the swallow guard, so a registered observer whose constructor or DI dependency throws cannot abort `SaveChangesAsync`) was generalised into. Behavior is identical: `null` and `NullAuditCaptureObserver` are both treated as 'no observer', an observer fault is swallowed so an observability outage cannot break the consumer's transaction, and the `IAuditCaptureObserver` public interface is unchanged.

### Deferred

- **Instrumentation re-base.** Re-basing `OrionAuditTelemetry` onto `OrionInstrumentation` was evaluated and **deferred**. `OrionAuditTelemetry` is a `static` class whose `ActivitySource` / `Meter` / instruments are static field initializers, while `OrionInstrumentation` is an `abstract` instance base; re-basing would force the static class to hold a derived singleton and would add an unused `SetStaticTags` / per-measurement tag surface (OrionAudit emits no static tags today) for the marginal benefit of two `new` calls, with no preservation upside. The Meter name (`OrionAudit`), the ActivitySource name (`OrionAudit`), every instrument name, and the public surface of `OrionAuditTelemetry` are unchanged; the observer convergence alone is the pilot result. A new `OrionAuditTelemetryNamingTests` guard freezes the source / meter / instrument names so any future re-base must preserve them byte-for-byte.

### Tests

- `AuditCaptureObserverFaultSafetyTests` (real interceptor, in-memory provider): a throwing `IAuditCaptureObserver` does not abort the capture or the consumer's `SaveChangesAsync` (the entity is saved and the audit row is written); an observer whose DI resolution throws also does not abort the save; a well-behaved observer still receives the audited-entity count and the inline / async-capture flag; `NullAuditCaptureObserver` and an absent observer are both silent no-ops.
- `OrionAuditTelemetryNamingTests`: the ActivitySource and Meter names stay `OrionAudit`, and the full set of instruments published under the `OrionAudit` meter matches the frozen expected list - a regression guard locking the observability contract that the deferred instrumentation re-base must preserve.

## [0.11.0] - 2026-06-28

### Added

#### Richer audit-history query filters

The backend-agnostic `IAuditHistoryStore` read surface gains additional filter dimensions, all optional and composing with the existing entity / subject / action / time filters and paging. Fully additive: a default `AuditHistoryQuery` (no new fields set) returns the same rows, in the same order, as before.

- **Set filters.** `AuditHistoryQuery.EntityTypes` and `AuditHistoryQuery.Actions` match a *set* of values (a row matches when its column is any value in the collection), translated to a server-side `IN (...)`. They compose with the single-value `EntityType` / `Action` as additional AND constraints. An empty (non-null) set leaves the dimension unfiltered rather than excluding everything.
- **Correlation / classification scope.** `AuditHistoryQuery.CorrelationId` pulls every row written under one logical operation (an HTTP request, a job run); `AuditHistoryQuery.UserType` filters by the acting subject's classification (`"user"` / `"system"` / `"job"` / ...).
- **Value-change predicate.** `AuditHistoryQuery.ChangedPath` keeps only rows whose change touched a given JSON Pointer in the row's `Diff`: it matches the exact path (`"path":"/p"`) and any nested descendant (`"path":"/p/..."`), so a query for `/address` also matches a change to `/address/city`. Both candidate tokens are closed by a quote or a slash, so a sibling whose name merely shares a textual prefix (`/postalCode` for `/post`) does not match. Implemented as a closed-token `Contains` / `LIKE` over the stored diff so it runs server-side on every relational backend without parsing JSON in the database. A path without a leading `/` is rejected by `Validate()`.
- **Ordering options.** `AuditHistoryQuery.SortBy` (`OccurredOn` / `EntityType` / `Action` / `UserId`) chooses the primary sort dimension; `Order` still chooses the direction. `OccurredOnUtc` then `Id` are always appended as stable tie-breaks so paging stays deterministic. The default `OccurredOn` collapses to the pre-v0.11 `(OccurredOnUtc, Id)` ordering exactly.

#### Read-side aggregations

A grouped-count surface over audit history so a caller can summarise activity without pulling every row.

- **`IAuditHistoryStore.AggregateAsync(AuditAggregationQuery)`** returns one `AuditAggregateBucket` (key + count) per distinct group. `AuditAggregationQuery` carries the same filter surface as a query (by composition: its `Filter` is an `AuditHistoryQuery`, paging / ordering ignored) plus a `GroupBy` dimension: `Action`, `EntityType`, `UserId`, `TenantId`, or `TimeBucket` (per-`Hour` / `Day` / `Month`, UTC). A null user / tenant folds into one bucket; a time bucket also surfaces the typed `BucketStartUtc`.
- **Streamed-friendly, not buffered.** The grouping runs server-side as a relational `GROUP BY` on the EF Core store, so the database returns only the bounded set of distinct buckets (cardinality of the grouped column), never the underlying rows. Time bucketing groups by the UTC year/month/day/hour components rather than a provider-specific date function, so it translates across SQLite / SQL Server / PostgreSQL / MySQL.
- **Capability-default.** `AuditHistoryStoreBase.AggregateAsync` throws `NotSupportedException` by default, matching the existing `QueryAsync` / `CompactAsync` pattern, so a backend overrides only what it can honour. `EfCoreAuditHistoryStore` and the in-memory `InMemoryAuditHistoryStore` implement it with identical semantics.

### Tests

- `AuditHistoryRicherQueryTests` (real SQLite, pinned ids + timestamps): each new filter (entity-type set, action set, correlation, user-type, changed-path exact + nested + sibling-prefix-safe) returns exactly the matching rows and composes with the existing filters and paging; `SortBy` orders by the chosen field then chronologically; an existing unfiltered query is byte-for-byte unchanged; aggregation by action / entity-type (filtered) / day-bucket returns correct grouped counts; an empty aggregation returns no buckets; a 250-row dataset pages with a 40-row page size without ever returning more than a page.
- `InMemoryAuditHistoryRicherQueryTests`: the in-memory test double mirrors the EF Core store's filter / aggregation semantics (including null user/tenant folding and empty-set-means-unfiltered), and `Validate()` rejects a `ChangedPath` without a leading `/`.
- `AuditHistoryStoreBaseTests`: the capability-default `AggregateAsync` throws `NotSupportedException`.

### Deferred

- **Separate-database audit storage (`o.UseSeparateAuditDb(...)`)** remains planned but is **not** in this release: moving the audit table to its own connection / schema / DB with an outbox-dispatched audit-side write needs a new package or provider abstraction beyond the in-package read surface. It stays on the roadmap for a later milestone.
- **CLI diff renderer (`dotnet orionaudit diff`)** remains planned but is **not** in this release: it requires a new `dotnet tool` package. It stays on the roadmap.

## [0.10.0] - 2026-06-27

### Added

#### Background snapshot compaction

A hosted-service variant of the v0.8.0 `IAuditHistoryStore.CompactAsync` operation, so operators get bounded reconstruction cost and storage without invoking compaction by hand. Off by default and fully additive: existing diff / compaction / hash-chain / read behaviour is unchanged.

- **`o.CompactInBackground(...)` opt-in.** Registers `AuditCompactionHostedService<TDbContext>`, a `BackgroundService` that runs a compaction sweep on a configurable `PeriodicTimer`. Each cycle discovers the entity streams (`EntityType`, `EntityId`, `TenantId`) whose history has grown past `MinRowsBeforeCompaction`, folds the oldest rows of each into a single in-place snapshot row past a retained tail (reusing the existing `AuditHistoryCompactor` engine through `CompactAsync`), and leaves the latest state fully reconstructable. The latest state replays identically before and after a fold.
- **Bounded and cancellation-aware.** A cycle folds at most `MaxStreamsPerSweep` streams (default 100), ordered most-rows-first so the cap spends the budget on the highest-value streams, and stops starting new folds once the optional `MaxSweepDuration` wall-clock budget elapses. The background loop swallows unexpected per-cycle failures (emitting `orionaudit.compaction.errors`) and keeps ticking; shutdown cancels an in-flight cycle promptly. `SweepOnceAsync` is exposed for tests and operator-triggered runs.
- **Safe alongside live writes.** Each per-stream fold runs through the existing single-`SaveChanges`-transaction compaction path, so a concurrent capture (which appends a new, later-timestamped row the in-flight fold never selected) does not contend with the fold; the appended row simply joins the retained tail.
- **Hash-chain-aware.** Snapshot compaction rewrites the boundary row's content and removes the folded rows, both of which the v0.9.0 tamper-evident chain is designed to detect (`ContentMismatch` / `Truncated`). Compaction and an unforgeable chain are therefore mutually exclusive on the same stream, so when `o.UseHashChain()` is enabled the background sweep **skips every stream that carries a hashed row** and only folds unchained streams. The chain stays verifiable; the candidate query excludes chained streams server-side so a chained table is never materialised. Self-defeating configurations (for example `MinRowsBeforeCompaction` below `RetainTail + 2`, a non-positive interval, or a zero stream cap) are rejected at startup.
- **Telemetry.** New instruments on the `OrionAudit` meter: `orionaudit.compaction.cycles`, `orionaudit.compaction.streams_compacted`, `orionaudit.compaction.rows_folded`, `orionaudit.compaction.errors`, and the `orionaudit.compaction.sweep.duration` histogram, plus an `OrionAudit.Compaction.Sweep` activity per cycle.

#### NDJSON audit-history export / streaming

A bulk, paged export off `IAuditHistoryStore` for feeding a warehouse or SIEM, built on the v0.8.0 paged query and sized for large result sets.

- **`AuditHistoryExporter`** (registered automatically, scoped). `StreamRowsAsync(filter, pageSize)` yields the rows matching an `AuditHistoryQuery` filter (entity / subject / action / time-range) as an `IAsyncEnumerable<AuditLog>`, walking the store one bounded page at a time (forced oldest-first for a deterministic, resumable order) so no more than one page is ever held in memory. `ExportNdjsonAsync(stream, filter, pageSize)` writes the result as NDJSON straight to a destination stream, flushing per page, and returns the row count. An empty result writes nothing.
- **Reflection-free NDJSON writer.** `AuditNdjsonWriter.WriteRow` serializes one `AuditLog` row to a compact JSON object over `Utf8JsonWriter` (no reflection, Native-AOT clean). The already-JSON `Diff` and `Snapshot` columns are embedded as **raw JSON** (not re-escaped into a string), with a JSON-string fallback for any legacy row whose column text does not parse, so a single malformed row never aborts the export.

### Tests

- `BackgroundCompactionTests` (real SQLite, frozen clock, pinned timestamps): the background sweep folds a long history and leaves it reconstructable through the production `IAuditReconstructor`; a short history is a no-op; with hash-chaining enabled the sweep skips the chained stream and the chain still verifies (`VerifyChainAsync` valid, full row count); `MaxStreamsPerSweep` bounds a cycle to one stream and a second cycle folds the rest.
- `BackgroundCompactionWiringTests`: `CompactInBackground` registers the hosted service and options; without it neither is registered; the exporter is registered even without background compaction; invalid sweep options (`MinRowsBeforeCompaction` below `RetainTail + 2`, non-positive interval, zero stream cap) are rejected at `AddOrionAudit` time.
- `AuditHistoryExporterTests` (real SQLite + a recording store double): a filtered dataset exports as valid NDJSON, oldest-first, with `Diff` / `Snapshot` embedded as raw JSON and the filter honoured; an empty result writes zero bytes; a 250-row dataset exported with a 40-row page size streams every row across multiple pages; the recording store proves the exporter never requests more than `pageSize` rows per call and advances `Skip` page-by-page.

### Deferred

- **Cold-store archival of the audit log itself** (age-tiered move to S3 / Parquet / archive table) remains planned but is **not** in this release: it would require a new package or a provider abstraction beyond the existing `IAuditArchiver` retention hook. It stays on the roadmap for a later milestone; this release keeps the audit-log-lifecycle work to in-package background compaction and export.

## [0.9.0] - 2026-06-22

### Added

#### Tamper-evident hash-chaining of audit entries

An opt-in integrity layer that chains each persisted `AuditLog` row to the one before it with a **keyed MAC**, so a later edit, deletion (including tail/whole-stream truncation), reordering, or out-of-band insertion of any row is detectable - even by an attacker who can write audit rows, as long as they do not hold the MAC key. Off by default and fully additive: existing behaviour, schema reads, and the public query / compaction APIs are unchanged.

- **Keyed per-row MAC (not a bare hash).** Each row gains an HMAC-SHA256 `EntryHash` computed as `HMAC(key, canonical(row fields, including registered custom columns) || PreviousHash)`, plus a `PreviousHash` column carrying the predecessor's MAC and a `HashKeyId` column recording which key version signed the row. The key is supplied by an `IAuditChainKeyProvider` that lives **outside** the audit database, so a bare-SHA-256 chain's weakness - anyone who can write rows can recompute the hashes and forge a valid-looking chain - is closed: without the key, a forged or altered row cannot be given a valid MAC. Canonicalization is deterministic and round-trip-stable: a fixed field order, length-prefixed fields (so content cannot migrate across field boundaries undetected), registered custom-column values folded in with deterministic name ordering (sorted, invariant, UTF-8), invariant culture, and a Kind- and precision-stable timestamp (integer epoch milliseconds) so a row's MAC survives a database round-trip identically across SQLite / SQL Server / PostgreSQL / MySQL. The hash columns are nullable, fixed-length(64).
- **Per-stream anchor (`OrionAudit_Chain_Anchor`).** A persisted head row per stream - keyed by (`EntityType`, `EntityId`, `TenantId`) - stores that stream's latest `EntryHash`, hashed-row count, and key id. It solves two problems the per-row chain alone cannot: **(1) concurrency** - two transactions appending to the same stream both read the head before either commits; the writer takes a pessimistic row lock on the anchor inside the consumer's `SaveChanges` transaction, stamps `PreviousHash` from it, then advances it in the same transaction, so same-stream appends serialize on the anchor while different streams stay parallel (SQL Server `UPDLOCK, HOLDLOCK`; PostgreSQL / MySQL `FOR UPDATE`; SQLite already serializes writes DB-wide). **(2) truncation** - a consistent prefix would otherwise verify, hiding a deleted tail or an entire deleted stream; verification compares the walked tail hash + count against the anchor and reports `Truncated` when they disagree (or when a stream's rows are gone but its anchor remains).
- **Chain scope is per entity stream, per tenant.** The "preceding entry" is the prior row sharing an (`EntityType`, `EntityId`, `TenantId`) key, ordered by (`OccurredOnUtc`, `Id`). Including the tenant in the chain key (previously only in the hashed content) means tenant-scoped verification walks a self-consistent chain: the first row of a second tenant is its own genesis, not a broken link onto the first tenant's head.
- **`IAuditIntegrityVerifier.VerifyChainAsync`.** Walks rows in chain order, recomputes each row's keyed MAC (looking up the key by `HashKeyId`, binding the registered custom-column values), and checks each stream against its anchor. Returns an `AuditChainVerificationResult`: either valid (with the count of hashed rows verified) or the first broken row's `Id` + `EntityType` / `EntityId` and an `AuditChainBreakReason` (`ContentMismatch`, `BrokenLink`, `MissingHashAfterChainStart`, `Truncated` for a deleted tail / stream, `UnknownKey` for a row whose key id is not registered). Verify one entity stream (`AuditChainVerificationRequest.ForEntity`) or the whole table (`.All`), optionally tenant-scoped. Read-only and idempotent - safe to run on a live table or a replica. `EfCoreAuditIntegrityVerifier` is the default implementation, registered automatically when hash-chaining is enabled.
- **Opt-in via `o.UseHashChain(h => h.UseKey(keyId, base64Key))`.** Enables stamping in both the synchronous capture interceptor and the asynchronous dispatcher (the MAC and anchor are written inside the same transaction that writes the row). A key is **required**: enabling hash-chaining without configuring a key (and without registering a custom `IAuditChainKeyProvider`) throws a clear `OrionAuditConfigurationException` - an unkeyed chain would be forgeable. The key id is stored per row so keys can be rotated later without invalidating rows written under an older (still-registered) key. When not called, the hash columns stay null and capture is byte-for-byte unchanged.
- **Backward compatible.** Rows written before hash-chaining was enabled keep a null `EntryHash` and form an *unverified prefix* that the verifier skips; verification begins at each stream's first hashed (genesis) row, whose `PreviousHash` is null. Enabling the feature on an existing table therefore never invalidates the history already there - it starts a fresh chain from the next captured row.
- **Security boundary.** Integrity holds against an adversary who can modify the audit database but cannot obtain the MAC key; the key MUST be stored outside the audit database (a secret manager / KMS / environment secret). Verification with the wrong key fails for every row, which is the property that makes the chain unforgeable.
- **Schema / migration.** `UseHashChain()` requires the consumer to add an EF Core migration for the new `EntryHash` / `PreviousHash` / `HashKeyId` columns and the `OrionAudit_Chain_Anchor` table (`dotnet ef migrations add AddOrionAuditHashChain`); both are emitted by the OrionAudit entity configurations (`AuditLogEntityTypeConfiguration` and `AuditChainAnchorEntityTypeConfiguration`) like every other audit table. As a library OrionAudit ships the schema via its entity configuration, not a bundled migration, so the migration lands in the consumer's own migrations assembly.

### Tests

- `AuditEntryHasherTests`: the MAC is deterministic; output is 64-char lowercase hex; it changes when any content field, the previous hash, the key, or a custom-column value changes; it is independent of custom-column input order; canonicalization distinguishes null from empty string, distinguishes a present-but-empty custom column from an absent one, is injective across field boundaries, and ignores the row's own (mutable) hash columns.
- `AuditChainVerifierTests` (pure engine): a clean chain verifies (with and without a matching anchor); the genesis row has a null previous hash; stamped rows carry the active key id; a mutated row fails with `ContentMismatch`; the wrong key fails with `ContentMismatch` and an unregistered key id fails with `UnknownKey`; a deleted middle row fails at its successor with `BrokenLink`; a deleted tail row and a whole-stream deletion both fail with `Truncated` via the anchor; reordered rows fail with `BrokenLink`; an appended row continues the chain; an unhashed prefix then hashed suffix verifies only the hashed tail; an unhashed row inside the hashed region fails with `MissingHashAfterChainStart`; `KeyFor` throws on an unknown scope and puts the tenant in the key.
- `EfCoreAuditIntegrityVerifierTests` (real SQLite): clean persisted chains verify and write an anchor with the right tail hash/count; mutating a stored row, a custom-column value, deleting a middle row, deleting the tail row, and deleting a whole stream are each detected (`ContentMismatch` / `BrokenLink` / `Truncated`); the wrong key fails verification; appending in a later save continues the persisted chain and advances the anchor; a whole-table walk pinpoints the broken stream; tenant-scoped verification isolates each tenant's chain (one anchor per tenant) and the second tenant's first row is its own genesis; whitespace-only request keys are rejected. Ids and timestamps are pinned (deterministic `SeqId` Guids, fixed UTC timestamps) so ordering never depends on random Guid tie-breaks.
- `HashChainCaptureTests` (end-to-end through the interceptor + dispatcher): disabled mode leaves the hash columns null and writes no anchor; enabling without a key fails clearly; enabled mode stamps and verifies across multiple saves (the cross-save anchor-read seam) and advances the anchor; post-capture tampering is caught with a fixed deterministic tampered `Diff`; deleting the tail row is caught as `Truncated`; **concurrent same-stream appends** (two `SaveChanges` on separate connections against a shared-cache SQLite database) do not corrupt the chain - no two rows share a `PreviousHash` and the chain still verifies; multiple entities in one save each start their own genesis (and anchor); the async-capture dispatcher path chains and verifies.

## [0.8.1] - 2026-06-20

### Performance

#### Diff hot path: remove redundant deep-equal pass per container node

`Json6902.Diff` (reached on every audited change via `DiffEngine.Compute`) opened with a full recursive `JsonNode.DeepEquals(before, after)` short-circuit before falling through to the structural object/array walk. For the common case where `before` and `after` are JSON objects, that upfront deep-equal walked the entire snapshot tree, and then `DiffObject` re-walked the same tree to emit ops, comparing every property twice.

The deep-equal guard now runs only on the leaf (scalar / kind-mismatch) branch, where it actually suppresses a spurious `replace` for two equal values. Containers go straight to the structural diff, which already emits no ops when nothing changed. This removes one full O(N) `DeepEquals` pass over the entity snapshot on every captured insert/update/delete, with byte-identical patch output. A throwaway micro-benchmark over a 20-property entity with a single changed field measured roughly 7-19% less time in `Compute`; the win scales with the number of audited properties. No public API or wire-format change; the full diff/snapshot/reconstruct round-trip and integration suites pass unchanged.

## [0.8.0] - 2026-06-19

### Added

#### Queryable audit-history read API (`IAuditHistoryStore`)

A storage-agnostic read/maintenance surface over recorded `AuditLog` rows, in the new `Moongazing.OrionAudit.Store` namespace. Lets a consumer query audit history by common dimensions without binding to a specific persistence backend.

- `IAuditHistoryStore.QueryAsync(AuditHistoryQuery, ...)` returns a paged `AuditHistoryPage` (rows + `TotalCount` + `HasMore`). `AuditHistoryQuery` filters by entity type, polymorphic base type, entity id (subject), `AuditAction`, user id, tenant id, and an inclusive `FromUtc`..`ToUtc` time range, with `Skip`/`Take` paging and newest-first / oldest-first ordering. Every filter is optional; an unfiltered query is bounded by `AuditHistoryQuery.DefaultPageSize` (100). `AuditHistoryQuery.Validate()` rejects negative skip, non-positive take, and an inverted time range consistently across backends.
- `AuditHistoryStoreBase` supplies a capability-default that throws `NotSupportedException` for each operation, so a backend overrides only what it can honour (mirrors the family's `DeleteAuditArchiver`-as-default pattern).
- `EfCoreAuditHistoryStore` is the default implementation, registered by `AddOrionAudit`; it translates the filters into a server-side query over the consumer's `DbContext`.
- `InMemoryAuditHistoryStore` (in OrionAudit.Testing) implements the full surface over an in-memory row list with no persistence dependency, for tests and prototyping against the abstraction.

#### Snapshot compaction

An explicit operation that collapses a long change-history for one entity into a compacted snapshot (latest reconstructable state) plus a bounded retained tail, to bound storage growth.

- `IAuditHistoryStore.CompactAsync(AuditCompactionRequest, ...)` folds the rows older than `RetainTail` into a single snapshot row carrying the entity's reconstructed state at the compaction boundary, removes the folded rows, and keeps the most-recent `RetainTail` rows verbatim. A folded `Deleted` / `SoftDeleted` boundary stays a terminal state. Optional `TenantId` scopes the compaction to one tenant's rows for a shared entity id. Returns `AuditCompactionResult` (rows before / removed / after, snapshot-written). A no-op when the history is too short to gain anything.
- `AuditHistoryCompactor` is the pure, backend-agnostic folding engine. It replays the folded history over `AuditLog` JSON via `DiffEngine` (no reflection, no CLR entity type), so it is trim-safe / Native-AOT clean and shared by every store. The EF Core store applies the plan as one insert + delete inside a single `SaveChanges` transaction, so a failure leaves the history untouched.

### Fixed

- Flaky `CaptureEntriesPerSaveHistogramTests`: the tests listened on the process-global `OrionAudit` meter with no per-test discriminator, so under xUnit parallel execution an audited save in another test class could leak a sample into these assertions (intermittently breaking the strict "no emission for a no-op save" check). The tests now gate sample capture on an `AsyncLocal` flag that flows across the awaited `SaveChangesAsync` into the interceptor's `Record` call, isolating each test to its own emissions. Test-only change; no production behaviour affected.

### Tests

- `InMemoryAuditHistoryStoreTests`: query filters (entity type, id, action, user, tenant) return correct subsets; time-range bounds are inclusive on both ends; paging returns disjoint subsets and reports `HasMore`; ordering ascending/descending; invalid paging and inverted ranges throw. Compaction reduces history while preserving the latest reconstructable state, respects the retained tail, handles `RetainTail` 0, no-ops a short history, touches only the requested entity, and keeps a folded delete terminal.
- `EfCoreAuditHistoryStoreTests`: query filtering/paging/time-range against a real SQLite provider; compaction removes the folded rows, preserves the latest state on a fresh read, and stays tenant-scoped.
- `AuditHistoryStoreBaseTests`: the default base throws `NotSupportedException`; a store overriding only `QueryAsync` still throws from `CompactAsync`.

## [0.7.32] - 2026-06-17

### Changed
- Set the NuGet package icon to the navy Moongazing mark across every sub-package, and the README logo to the white Moongazing mark.

## [0.7.31] - 2026-06-17

### Changed
- Fixed the NuGet package icon: the per-project icon assets now carry the new Moongazing mark (v0.7.30 only updated the repo-root copy, which the packages do not embed). The README logo uses the white mark.

## [0.7.30] - 2026-06-17

### Changed
- Updated the package icon and README logo to the new Moongazing mark.

## [0.7.29] - 2026-06-15

### Added

#### `orionaudit.reconstruct.events_replayed` histogram

`Histogram<int>` records how many audit diff rows a reconstruction had to replay AFTER the latest applicable snapshot (rows scanned past the snapshot, or all rows when none applies).

- This is the snapshot-effectiveness signal: compared against the total `audit_row_count`, a low `events_replayed` means snapshots are covering most of the history and reconstruction is cheap, while a high value (approaching the row count) means a full replay - a sign the `SnapshotPolicy` should snapshot more frequently, or that an entity has a deep history with no usable snapshot.
- Distinct from `reconstruct.duration` (wall-clock) and the `audit_row_count` activity tag (total rows fetched).
- Recorded once per reconstructed entity (the early null returns for absent/deleted entities do not emit). Negatives clamp to 0.
- Public `OrionAuditTelemetry.RecordReconstructEventsReplayed(int)` helper.

### Tests

- `ReconstructEventsReplayedHistogramTests`: the helper records the value and clamps negatives; a real reconstruction over an entity with history emits a positive replayed-row sample.

## [0.7.28] - 2026-06-15

### Added

#### `orionaudit.dispatch.poll.idle` counter

`Counter<long>` increments on each dispatcher cycle that claims an empty batch (the capture queue had no dispatchable rows). Operators graph the idle-poll rate against the total poll rate to right-size the polling cadence: a high idle fraction is a cost-of-poll signal, while a low fraction means the dispatcher is busy and `BatchSize` may need raising.

- Distinct from the queue/DLQ depth gauges (current backlog) and the v0.7.18 `batch_size` histogram (which deliberately skips these zero-row cycles).
- Counted only after the empty-claim cycle fully completes (after the depth-gauge snapshots), so a failing depth query does not count an idle poll for a cycle that then errored out (CodeRabbit).
- Public `OrionAuditTelemetry.RecordDispatchIdlePoll()` helper.
- Mirrors the Guard v6.5.17 / Patch v0.2.28 `poll.idle` counters on the Audit side.

### Tests

- `DispatchIdlePollCounterTests`: `RecordDispatchIdlePoll` increments the counter.

## [0.7.27] - 2026-06-15

### Added

#### `orionaudit.dispatch.retries_before_success` histogram

`Histogram<int>` records how many attempts a capture-queue row had already failed when it was finally turned into an audit row (its `Attempts` on the success path: 0 = succeeded on the first attempt). It measures the **successful** side of the dispatch loop, complementing the v0.7.17 `dispatch.errors` counter (failures) and `rows_deadlettered` (terminal only), so operators can answer "are retries quietly papering over a flaky capture-to-audit path?".

- Healthy systems sit at p50 = 0; a rising upper percentile means rows are increasingly succeeding only after transient failures.
- Unlike the batch-shape histograms, the zero sample IS recorded: the fraction of first-try successes is exactly the signal.
- Emitted post-persist (after `SaveChangesAsync`), alongside the v0.7.20 `entry_size_bytes` samples, so a publish or commit failure that re-dispatches the row does not double-count.
- Public `OrionAuditTelemetry.RecordRetriesBeforeSuccess(int)` helper (negatives clamp to 0).
- Mirrors the Guard v6.5.27 `retries_before_success` shape on the Audit side.

### Tests

- `DispatchRetriesBeforeSuccessHistogramTests`: first-try zero is recorded, the retry count is emitted, negatives clamp to 0.

## [0.7.26] - 2026-06-13

### Added

#### `IAuditCaptureObserver` extensibility

Consumer-supplied observer invoked when the interceptor captures audited entities during `SaveChangesAsync`. Useful for application-side metrics, security alerting (e.g. "N rows of a sensitive entity modified in one save"), or feeding a separate change-tracking pipeline without coupling that logic to the load-bearing capture path.

- `IAuditCaptureObserver` interface with `OnCaptured(auditedEntityCount, isAsyncCapture)`.
- `NullAuditCaptureObserver` default.
- Fires on BOTH capture paths (async staging-capture AND inline AuditLog) with the total audited count.
- Resolved per-call from the scoped provider (same pattern as `IAuditEventPublisher`); throwing observer is swallowed so an observability fault cannot abort the consumer's transaction.

### Tests

2 facts; 257 total.

### Migration from v0.7.25

Source-compatible.

```csharp
services.AddSingleton<IAuditCaptureObserver, MyObserver>();
```

## [0.7.25] - 2026-06-12

### Added

#### `orionaudit.dispatch.claim_duration_ms` histogram

`Histogram<double>` measuring the claim round-trip (atomic UPDATE + SELECT of claimed rows) per dispatcher cycle. Isolates capture-table lock contention / index degradation from the v0.7.24 `flush_duration` (write side) and v0.7.21 `publish.duration` (broker side). The dispatcher cycle is now fully decomposed: claim + per-row work + publish + flush.

- ALL cycles emit including zero-row claims (claim latency is itself the signal).
- try/finally so a failing claim (deadlock, timeout) still emits.
- Negative values clamped to 0.
- Public `OrionAuditTelemetry.RecordDispatchClaimDuration(double)` helper.

### Tests

2 facts; 255 total.

### Migration from v0.7.24

Source-compatible.

## [0.7.24] - 2026-06-12

### Added

#### `orionaudit.dispatch.flush_duration_ms` histogram

`Histogram<double>` measuring `SaveChangesAsync` wall-clock at dispatch time. Isolates the EF write piece (AuditLog inserts + queue row deletes + commit) from the per-cycle `dispatch.batch.duration` which covers everything.

- try/finally so a commit failure (deadlock, transient backend pressure) still emits the sample.
- Negative values clamped to 0.
- Public `OrionAuditTelemetry.RecordDispatchFlushDuration(double)` helper.

### Tests

2 facts; 253 total.

### Migration from v0.7.23

Source-compatible.

## [0.7.23] - 2026-06-12

### Added

#### `orionaudit.capture.dlq_depth` ObservableGauge

`ObservableGauge<long>` reports the count of capture-queue rows in dead-letter state (`Error != null`). Distinct from the v0.7.x `dispatch.rows_deadlettered` counter (a rate of NEW dead-letters); this gauge exposes the LIVE table state so operators can spot a growing dead-letter backlog that needs triage even when the dispatch rate looks stable.

- Snapshotted by the dispatcher each cycle alongside the existing `queue_depth` (pending rows) gauge.
- 0 until the first dispatch cycle completes.

### Tests

1 fact; 251 total.

### Migration from v0.7.22

Source-compatible.

## [0.7.22] - 2026-06-11

### Added

#### `orionaudit.dispatch.events_per_publish` histogram

`Histogram<int>` of events sent per `IAuditEventPublisher.PublishAsync` call. Operators graph p99 to see whether publishes are sized appropriately for the broker - small batches under-utilise broker throughput while over-large batches risk timeouts and partial-success ambiguity.

- Pairs with v0.7.21 `publish.duration_ms`: duration alone cannot distinguish a slow publisher from a publisher carrying a large batch.
- Recorded immediately before the `PublishAsync` call (BEFORE the duration stopwatch); zero-event branches do not reach the recording site.
- Public `OrionAuditTelemetry.RecordEventsPerPublish(int)` helper.

### Tests

2 facts; 250 total.

### Migration from v0.7.21

Source-compatible.

## [0.7.21] - 2026-06-11

### Added

#### `orionaudit.dispatch.publish.duration_ms` histogram

`Histogram<double>` measuring `IAuditEventPublisher.PublishAsync` wall-clock per dispatcher cycle. Operators graph p99 to spot a downstream broker (Kafka, RabbitMQ) whose tail has regressed independently of the database side. Emits ONLY when a publisher is configured and there is at least one event to publish.

- Recorded BEFORE `SaveChangesAsync` so a commit failure does not mask the publish-side latency picture.
- Negative values clamped to 0 (clock-skew safety).
- Public `OrionAuditTelemetry.RecordPublishDuration(double)` helper.

### Tests

2 facts; 248 total.

### Migration from v0.7.20

Source-compatible.

## [0.7.20] - 2026-06-11

### Added

#### `orionaudit.capture.entry_size_bytes` histogram

`Histogram<int>` of capture queue entry payload size in bytes (BeforeJson + AfterJson combined). Operators graph p99 to size storage column types, spot a tenant whose audited entities suddenly grew, and right-size the capture-queue poll cadence against actual byte throughput.

- Recorded inside the dispatcher success path so failed rows do not skew the histogram tail.
- Zero/negative inputs ignored.
- Public `OrionAuditTelemetry.RecordCaptureEntrySize(int)` helper.

### Tests

2 facts; 246 total.

### Migration from v0.7.19

Source-compatible.

## [0.7.19] - 2026-06-11

### Added

#### `orionaudit.dispatch.lag.violations` SLO threshold counter

`Counter<long>` that increments each time a per-row dispatch lag exceeds the consumer-supplied `AsyncCaptureOptions.DispatchLagViolationThreshold`. Operators alert on the rate without needing a p99 calculation in their monitoring stack - the threshold IS the SLO.

- `AsyncCaptureOptions.DispatchLagViolationThreshold` nullable; default `null` = no threshold = back-compat no-op.
- Public `OrionAuditTelemetry.RecordDispatchLagViolation()` helper.
- Pairs with the existing `orionaudit.dispatch.lag` histogram: histogram for trend, counter for SLO-driven alerting.

### Tests

2 facts; 244 total.

### Migration from v0.7.18

Source-compatible.

```csharp
o.UseAsyncCapture(q => q.DispatchLagViolationThreshold(TimeSpan.FromSeconds(30)));
```

## [0.7.18] - 2026-06-11

### Added

#### `orionaudit.dispatch.batch_size` histogram

`Histogram<int>` exposing rows claimed per dispatcher cycle. Operators graph p99 to spot a dispatcher that consistently maxes out BatchSize (raise batch) or stays near 0 (over-sized polling cadence).

- Zero-row cycles do NOT emit (idle polling tracked by separate gauge).
- Recorded inside `AuditDispatcher.DispatchOnceAsync` right after the storage claim.
- Public `OrionAuditTelemetry.RecordDispatchBatchSize(int)` helper.

### Tests

2 facts; 242 total.

### Migration from v0.7.17

Source-compatible.

## [0.7.17] - 2026-06-11

### Added

#### `orionaudit.dispatch.errors` counter

`Counter<long>` that increments for EVERY swallowed per-row failure in `AuditDispatcher.DispatchOnceAsync` (transient + terminal). Distinct from `orionaudit.dispatch.rows_deadlettered` which only counts rows that exhausted `MaxAttempts`; this counter fires on the full failure surface so operators see the upstream pressure that precedes a dead-letter.

- Tag: `exception_type` - short type name (e.g. `JsonException`, `DbUpdateException`).
- Public `OrionAuditTelemetry.RecordDispatchError(string)` helper.
- Pairs with v0.7.16 `orionaudit.retention.errors`: now both retention + dispatch swallowed exceptions emit on a counter per exception type.

### Tests

1 fact; 240 total.

### Migration from v0.7.16

Source-compatible.

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
