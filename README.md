<!-- markdownlint-disable MD033 MD041 MD060 -->

<p align="center">
  <img src="src/Moongazing.OrionAudit/docs/logo.png" alt="OrionAudit Logo" width="150" />
</p>

<h1 align="center">OrionAudit</h1>

<p align="center">
  EF Core change-audit trail with JSON Patch diffs, multi-tenant support, and time-travel reconstruction
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/OrionAudit"><img src="https://img.shields.io/nuget/v/OrionAudit?style=flat-square&color=blue" alt="NuGet" /></a>
  <a href="https://www.nuget.org/packages/OrionAudit"><img src="https://img.shields.io/nuget/dt/OrionAudit?style=flat-square&color=green" alt="Downloads" /></a>
  <a href="LICENSE.txt"><img src="https://img.shields.io/badge/license-MIT-yellow?style=flat-square" alt="License" /></a>
  <img src="https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-purple?style=flat-square" alt="Target" />
</p>

---

> **v0.6.0 is here — Developer Experience.** Two opt-in additions that unblock common adoption scenarios. `o.AddColumn<int>("WorkflowStepId", ctx => ...)` registers tipped, indexable EF shadow-property columns on `AuditLog` — write fast LINQ filters instead of scanning JSON. `db.CreateAuditImport(o => o.ImportBatch = "legacy-2026")` bulk-imports hand-rolled change history as idempotent `AuditLog` rows whose diffs are byte-for-byte identical to native capture. On top of v0.5.0 async staging-capture + viewer, v0.4.0 AOT-clean diff, v0.3.0 source-gen, v0.2.0 scale, v0.1.0 capture.
> [See the v0.6.0 changelog](CHANGELOG.md#060---2026-05-24) and [what's next](ROADMAP.md).

---

## Why OrionAudit?

| Feature                                | OrionAudit | Audit.NET | EFCore.Triggered | DIY pattern |
| -------------------------------------- | :--------: | :-------: | :--------------: | :---------: |
| EF Core `SaveChanges` interception     |    Yes     |    Yes    |       Yes        |     Yes     |
| JSON Patch (RFC 6902) diffs            |    Yes     |    Yes    |        -         |      -      |
| Time-travel reconstruction             |    Yes     |     -     |        -         |      -      |
| Sensitive-field attributes (Hash/Redact) |  Yes     |     -     |        -         |      -      |
| Multi-tenant read-side filter          |    Yes     |     -     |        -         |      -      |
| Pluggable user / tenant resolvers      |    Yes     |    Yes    |        -         |     Yes     |
| ASP.NET Core HttpContext resolver      |    Yes     |    Yes    |        -         |      -      |
| OpenTelemetry `ActivitySource` + Meter |    Yes     |     -     |        -         |      -      |
| Framework-agnostic test helpers        |    Yes     |     -     |        -         |      -      |
| Multi-targets net8 / net9 / net10      |    Yes     |    Yes    |       Yes        |    n/a      |
| Source-generated type discovery        |    Yes     |     -     |        -         |      -      |
| NativeAOT clean                        |    Yes     |     -     |        -         |      -      |
| Composite primary key support          |    Yes     |    Yes    |       Yes        |     Yes     |
| Periodic snapshotting (O(K) replay)    |    Yes     |     -     |        -         |      -      |
| Retention policy + background sweep    |    Yes     |     -     |        -         |      -      |
| Soft-delete capture (distinct action)  |    Yes     |     -     |        -         |      -      |
| Provider column hints (jsonb / nvarchar(max)) | Yes  |     -     |        -         |      -      |
| Opt-in async staging-capture (atomic, lossless) | Yes |    -     |        -         |      -      |
| Embedded audit-trail UI (no Blazor, no build step) | Yes |   -     |        -         |      -      |

---

## Quick Start (60 seconds)

```bash
dotnet add package OrionAudit
```

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;

// Register configuration and (optional) resolvers
services.AddOrionAudit<AppDbContext>(o => o
    .Audit<Order>()
    .Audit<Customer>(b => b
        .Hash(c => c.Email)        // store SHA-256 hex, not the plaintext
        .Redact(c => c.ApiKey)));  // store the literal "<redacted>"

// Wire the interceptor into your DbContext
services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseSqlServer(connectionString)
     .UseOrionAudit(sp));
```

```csharp
// AppDbContext.cs
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyOrionAuditConfigurations();
}
```

That's it — every `SaveChanges` on an `[Auditable]` entity now writes an `AuditLog` row in the
same transaction.

---

## Ecosystem Packages

| Package                  | Install                                        | Purpose                                            |
| ------------------------ | ---------------------------------------------- | -------------------------------------------------- |
| `OrionAudit`             | `dotnet add package OrionAudit`                | Core library — interceptor, diff, reconstruction   |
| `OrionAudit.AspNetCore`  | `dotnet add package OrionAudit.AspNetCore`     | `HttpContextAuditUserResolver` + DI helpers        |
| `OrionAudit.Viewer`      | `dotnet add package OrionAudit.Viewer`         | Embedded read-only audit-trail UI (`MapOrionAuditViewer`) |
| `OrionAudit.Testing`     | `dotnet add package OrionAudit.Testing`        | `AuditCapture` + fluent assertions, framework-free |

---

## What's new in v0.6.0

### `AddColumn` — tipped, indexable custom columns

```csharp
services.AddOrionAudit<AppDbContext>(o => o
    .Audit<Order>()
    .AddColumn<int>("WorkflowStepId", ctx => (ctx.Entity as IHasWorkflow)?.StepId)
    .AddColumn<string>("Source", ctx => ctx.Action == AuditAction.Inserted ? "import" : "app"));

// OnModelCreating: pick up registered columns automatically.
protected override void OnModelCreating(ModelBuilder modelBuilder)
    => modelBuilder.ApplyOrionAuditConfigurations(this);

// LINQ filter on a real, indexable column:
var fromStep3 = await db.AuditLog()
    .Where(a => EF.Property<int?>(a, "WorkflowStepId") == 3)
    .ToListAsync();
```

Add a `CreateIndex` in your EF migration for any column you'll filter on. The provider runs
inside the capture transaction with the audited entity in scope; failure annotates
`AuditLog.Error` and leaves the column NULL. In async-capture mode the value rides through
the queue's new `CustomColumnsJson` column and lands on the final `AuditLog` after dispatch.

### `AuditImportBuilder` — bulk historical import, idempotent

```csharp
var import = db.CreateAuditImport(o =>
{
    o.BatchSize = 1000;
    o.ImportBatch = "legacy-orders-2026";   // REQUIRED — drives idempotency
});

import.Add<Order>(e => e
    .Key(legacy.OrderId)
    .Action(AuditAction.Updated)
    .Before(oldState).After(newState)
    .By("u-123", "Legacy User")
    .At(legacy.ChangedAtUtc)
    .SourceId(legacy.RowId));

var result = await import.SaveAsync();
// result.Written / Skipped / DeadLettered
```

`ImportBatch` is mandatory — it stamps `AuditLog.CorrelationId` so re-running `SaveAsync` is
safe (rows already present report as `Skipped`). Imported diffs are byte-for-byte equal to the
diffs the live capture path produces (a parity test enforces this). Import always writes
`AuditLog` directly, bypassing the async-capture queue.

---

## What's new in v0.5.0

### Async staging-capture — atomic, lossless, off the hot path

Synchronous capture (the default since v0.1.0) writes the `AuditLog` row in the same
transaction as the originating change. Under high write load the diff computation and the
extra row become measurable overhead. Opt-in `UseAsyncCapture()` keeps the atomicity guarantee
but defers the heavy work:

```csharp
services.AddOrionAudit<AppDbContext>(o => o
    .Audit<Order>()
    .UseAsyncCapture(q => q
        .PollInterval(TimeSpan.FromSeconds(2))
        .BatchSize(500)
        .MaxAttempts(5)));
```

- The interceptor writes a lightweight `OrionAudit_Capture_Queue` row **in the same
  transaction** as the data change — capture stays atomic and lossless.
- `AuditDispatcherHostedService` polls the queue, computes diffs, and writes the final
  `AuditLog` rows. Inserts and deletes commit together → exactly-once.
- A row that throws is retried up to `MaxAttempts` and then dead-lettered (`Error` column
  set; surfaced via `orionaudit.dispatch.rows_deadlettered` telemetry).
- `IAuditDispatcher.FlushPendingAsync(ct)` force-drains the queue for tests and
  read-after-write call sites. A no-op implementation is registered in synchronous mode so
  the dependency is always resolvable.

**Trade-off to know:** in async mode `AuditFor<T>()` sees only dispatched rows, so audit is
eventually consistent. Use `FlushPendingAsync` where you need read-after-write.

### `OrionAudit.Viewer` — read-only audit UI, one line to embed

```csharp
app.MapOrionAuditViewer<AppDbContext>("/audit", o => o.RequireAuthorization("AuditViewers"));
```

That single registration mounts a JSON API (`GET /audit/api/log`, `/audit/api/{type}/{key}`,
`/audit/api/meta`) plus a built-in vanilla-JS single-page UI served from `/audit`. No Blazor
dependency, no build step — drops into any ASP.NET Core host. Authorization is required by
default; an explicit `AllowAnonymous()` opts out (dev use only).

Tenant filtering is honoured automatically: the API reads through `db.AuditLog()`, which
applies the registered `IAuditTenantResolver`.

### Benchmark — the honest story

`InterceptorBench` (in-memory SQLite, .NET 10 — `bench/Moongazing.OrionAudit.Bench`):

| Scenario                | Batch | Mean (µs) | Ratio | Allocated         |
| ----------------------- | ----- | --------: | :---: | ----------------- |
| `SaveChanges_NoAudit`        | 1     |     277 | 1.00× | 71 KB             |
| `SaveChanges_WithAudit`      | 1     |     769 | 2.82× | 96 KB (1.35×)     |
| `SaveChanges_WithAsyncAudit` | 1     |   1 311 | 4.80× | 95 KB (1.34×)     |
| `SaveChanges_NoAudit`        | 10    |     957 | 1.00× | 141 KB            |
| `SaveChanges_WithAudit`      | 10    |   3 936 | 4.18× | 335 KB (2.37×)    |
| `SaveChanges_WithAsyncAudit` | 10    |   3 414 | 3.62× | 343 KB (2.43×)    |
| `SaveChanges_NoAudit`        | 100   |   6 023 | 1.00× | 819 KB            |
| `SaveChanges_WithAudit`      | 100   |  13 720 | 2.36× | 2.7 MB (3.31×)    |
| `SaveChanges_WithAsyncAudit` | 100   |  14 259 | 2.45× | 2.8 MB (3.45×)    |

In-memory SQLite is unkind to async-mode bookkeeping — the `ExecuteUpdateAsync` claim plus
the queue insert show up as raw cost without the network round-trip latency a real DB has.
On a production SQL Server or Postgres, two things change in async mode's favour: the
baseline `SaveChanges` carries network IO that absorbs sync mode's diff CPU into a much
larger denominator, and the deferred `SnapshotCursor` lookup (a per-update DB query inside
the consumer's transaction) genuinely leaves the hot path. Treat async capture as a
correctness-preserving way to move materialisation off the consumer's transaction; it's a
*throughput* feature, not a microbenchmark win.

The capture-queue depth is exposed as `orionaudit.capture.queue_depth` (observable gauge) so
operators can watch dispatch lag in their dashboards.

---

## Core Features

### Auto-capture on every SaveChanges

`AuditSaveChangesInterceptor` registers itself in EF Core's interceptor pipeline. For every
`[Auditable]` entity in `EntityState.Added | Modified | Deleted`, it writes one `AuditLog` row
inside the same transaction as the originating change.

```csharp
ctx.Orders.Add(new Order { Status = "Pending" });
await ctx.SaveChangesAsync();
// → AuditLog row: Action=Inserted, EntityId=..., Diff=[{"op":"add","path":"/Status","value":"Pending"}]
```

### JSON Patch (RFC 6902) diffs

Diffs are computed by OrionAudit's in-house, reflection-free RFC 6902 engine and stored in the
`Diff` column as compact JSON. They are replayable — that's what makes time-travel
reconstruction possible.

```csharp
order.Status = "Shipped";
await ctx.SaveChangesAsync();
// → Diff = [{"op":"replace","path":"/Status","value":"Shipped"}]
```

### Sensitive-field handling

Attributes and equivalent fluent overrides — both control what lands in the audit table without
touching the entity class beyond the attribute.

```csharp
[Auditable]
public sealed class Customer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    [HashedAudit]   public string Email { get; set; } = "";   // SHA-256 hex
    [RedactedAudit] public string ApiKey { get; set; } = "";  // literal "<redacted>"
    [NotAuditable]  public string Internal { get; set; } = ""; // omitted entirely
}

// Equivalent fluent form:
services.AddOrionAudit<AppDbContext>(o => o
    .Audit<Customer>(b => b
        .Hash(c => c.Email)
        .Redact(c => c.ApiKey)
        .Exclude(c => c.Internal)));
```

`Hash` is deterministic, so equality checks on the audit table still work without leaking
plaintext. `Redact` replaces the value with the literal `"<redacted>"` — change detection breaks
on purpose for fields where even the existence of a change is sensitive.

### Multi-tenant capture and read-side filter

Implement `IAuditTenantResolver` once; every audit row gets the tenant id stamped, and every
`AuditFor<T>()` query auto-filters to the current tenant.

```csharp
public sealed class CurrentTenantResolver : IAuditTenantResolver
{
    private readonly ITenantContext context;
    public CurrentTenantResolver(ITenantContext context) => this.context = context;
    public string? Resolve(IServiceProvider sp) => context.TenantId;
}

services.AddOrionAudit<AppDbContext>(o => o
    .Audit<Order>()
    .TenantResolver<CurrentTenantResolver>());

// Reads automatically scoped to current tenant
var rows = await context.AuditFor<Order>().ToListAsync();

// Need the global view for admin tooling?
var allRows = await context.AuditFor<Order>(crossTenant: true).ToListAsync();
```

### Time-travel reconstruction

`IAuditReconstructor` replays the audit history of an entity up to any timestamp.

```csharp
var reconstructor = serviceProvider.GetRequiredService<IAuditReconstructor>();

// Single entity
var orderAsOfYesterday = await reconstructor.ReconstructAsync<Order>(
    entityId: order.Id.ToString(),
    asOf: DateTime.UtcNow.AddDays(-1));

// Batch — one query, then per-entity replay
var manyAsOf = await reconstructor.ReconstructManyAsync<Order>(
    entityIds: orderIds.Select(id => id.ToString()),
    asOf: DateTime.UtcNow.AddHours(-3));
```

Returns `null` if the entity didn't exist or was deleted at that timestamp.

### User attribution via ASP.NET Core

```bash
dotnet add package OrionAudit.AspNetCore
```

```csharp
builder.Services
    .AddOrionAudit<AppDbContext>(o => o
        .Audit<Order>()
        .UserResolver<HttpContextAuditUserResolver>())
    .AddOrionAuditAspNetCore();
```

`HttpContextAuditUserResolver` pulls the user from `HttpContext.User` via the
`NameIdentifier` / `sub` claim and populates `AuditLog.UserId` / `UserDisplay`. Anonymous
requests leave those columns null without breaking the capture.

### OpenTelemetry instrumentation

Spans and metrics are emitted under the `OrionAudit` `ActivitySource` and `Meter`.

```csharp
builder.Services
    .AddOpenTelemetry()
    .WithTracing(t => t.AddSource(OrionAuditTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(OrionAuditTelemetry.MeterName));
```

| Signal                              | Type      | Description                                       |
| ----------------------------------- | --------- | ------------------------------------------------- |
| `OrionAudit.Capture`                | Activity  | One span per `SaveChanges` that wrote audit rows  |
| `OrionAudit.Reconstruct`            | Activity  | One span per `ReconstructAsync` call              |
| `OrionAudit.ReconstructMany`        | Activity  | One span per `ReconstructManyAsync` call          |
| `orionaudit.entries.written`        | Counter   | Audit rows successfully written                   |
| `orionaudit.entries.failed`         | Counter   | Audit rows written with diff errors               |
| `orionaudit.capture.duration`       | Histogram | Interceptor capture duration in milliseconds      |
| `orionaudit.reconstruct.duration`   | Histogram | Reconstruction duration in milliseconds           |

### Source-generated registration (AOT-aware)

Skip the runtime assembly scan entirely. Decorate a `partial class` with `[OrionAuditModule]`
and the bundled source generator emits a `RegisterAuditedTypes` method that registers every
`[Auditable]` type discovered at compile time.

```csharp
[OrionAuditModule]
public partial class AppAuditModule { }

// A hand-written System.Text.Json context covering your audited entities
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(Customer))]
public partial class AppJsonContext : JsonSerializerContext { }

services.AddOrionAudit<AppDbContext>(o =>
{
    AppAuditModule.RegisterAuditedTypes(o.ConfigurationBuilder);  // generator-emitted, no reflection
    o.UseJsonContext(AppJsonContext.Default);                     // trim-aware snapshot serialisation
});
```

The generator ships *inside* the `OrionAudit` NuGet (`analyzers/dotnet/cs/`) — no extra
package to install. The reflective `ScanAssembly` path still works and now carries
`[RequiresUnreferencedCode]` so trim/AOT publishes flag it. As of v0.4.0 the diff engine is
in-house and fully reflection-free. The snapshot-capture path is Native-AOT clean when wired
through `UseJsonContext`; without a context it falls back to reflective serialization, which
is annotated with `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]` so trim/AOT publishes
flag it. A CI Native-AOT probe publishes the context-wired surface and fails the build on any
trim/AOT warning.

### Framework-agnostic test helpers

```bash
dotnet add package OrionAudit.Testing
```

```csharp
using Moongazing.OrionAudit.Testing;

ctx.Orders.Add(new Order { Status = "Pending" });
await ctx.SaveChangesAsync();

AuditCapture.From(ctx)
    .Should()
    .HaveLogged<Order>(AuditAction.Inserted)
    .HaveLoggedExactly(1).Of<Order>();
```

`OrionAudit.Testing` throws plain exceptions on failure, so it works with xUnit, NUnit, MSTest,
or any other runner — no transitive `FluentAssertions` / `Shouldly` choice forced on you.
`InMemoryAuditUserResolver` and `InMemoryAuditTenantResolver` round out the test-doubles surface.

---

## Performance

OrionAudit is benchmarked with [BenchmarkDotNet](https://benchmarkdotnet.org/) on each release
([`bench/Moongazing.OrionAudit.Bench`](bench/Moongazing.OrionAudit.Bench)). Numbers below come
from `BenchmarkDotNet v0.15.8` on Windows 11, .NET 10.0.5, Intel i7-7820HQ (Kaby Lake, 4C/8T).

### Per-entity snapshot

A 7-property entity, single snapshot build:

| Method                  |    Mean | Ratio | Allocated |
| ----------------------- | ------: | ----: | --------: |
| Build_AttributesOnly    |  677 ns |  1.00 |     984 B |
| Build_WithHashAndRedact | 1.74 μs |  2.57 |     984 B |

`Hash` and `Redact` pay for one SHA-256 invocation per hashed field. The UTF-8 input buffer is
stack-allocated for inputs under 256 bytes and rented from `ArrayPool<byte>` above that, with
stack-allocated SHA-256 output — zero heap allocation for the cryptographic path itself.

### JSON Patch diff (Compute vs. Apply)

| Properties | Compute (Mean / Alloc) | Apply (Mean / Alloc) | Apply ratio |
| ---------: | ---------------------: | -------------------: | ----------: |
|          4 |    25.4 μs / 24.0 KB   |    24.9 μs / 4.4 KB  |        0.18 |
|         16 |    95.6 μs / 88.5 KB   |    35.8 μs / 15.3 KB |        0.17 |
|         64 |   330.0 μs / 351 KB    |   136.5 μs / —       |        0.41 |

`Apply` is consistently 2.5–5× cheaper than `Compute` and allocates 5–6× less — replaying audit
history (reconstruction) is the cheap side.

### EF Core SaveChanges overhead

In-memory Sqlite, vs. a baseline `SaveChanges` with no audit hooked up:

| Batch size | NoAudit (Mean) | WithAudit (Mean) | Slowdown | Alloc ratio |
| ---------: | -------------: | ---------------: | -------: | ----------: |
|          1 |        197 μs  |         679 μs   |     3.5× |        1.5× |
|         10 |        474 μs  |       2.38 ms    |     5.0× |        3.2× |
|        100 |       2.59 ms  |       10.8 ms    |     4.2× |        4.6× |

Sqlite in-memory has near-zero per-row write cost, which makes the audit overhead look large in
ratio. Against a real Postgres or SQL Server deployment the DB round-trip dominates total
latency and the audit overhead drops into the **5–15% range**. Run the bench against your own
provider for the number that matters to you.

### Time-travel reconstruction (replay cost)

| History depth | Mean    | Allocated |
| ------------: | ------: | --------: |
|            10 | 1.09 ms |   126 KB  |
|           100 | 2.76 ms |   506 KB  |
|          1000 | 8.95 ms |   4.3 MB  |

Reconstruction is O(N) in audit-row count: every diff is applied in sequence from the Insert
forward. **Periodic snapshotting** (v0.2 roadmap) turns this into O(K) where K = updates since
the last snapshot — for `SnapshotEvery(100)` against the 1000-depth case, expect a 10× speedup
and proportional allocation drop.

### Design notes

- **Primitive fast path** — `SnapshotBuilder.ConvertToNode` switches on the runtime type and
  calls `JsonValue.Create` directly for primitives, skipping `JsonSerializer.SerializeToNode`'s
  reflection. User-defined types still fall through to the reflective path.
- **FrozenDictionary lookups** — `IAuditConfiguration.IsAudited` is a frozen-dictionary
  `ContainsKey`; the interceptor short-circuits on entity state before doing the type lookup.
- **`FindExtension<CoreOptionsExtension>()`** for the tenant resolver instead of LINQ over
  `IDbContextOptions.Extensions`.

Source-generated `[Auditable]` discovery (v0.3.0) and a reflection-free, Native-AOT-clean
diff engine (v0.4.0) have shipped — see the [roadmap](ROADMAP.md).

---

## Sample Application

```bash
dotnet run --project sample/Moongazing.OrionAudit.Sample.Console
```

The sample walks through all seven v0.1.0 features end-to-end against an in-memory Sqlite DB:
insert / update / delete cycles, sensitive-field masking, multi-tenant filtering, time-travel
reconstruction, and live OpenTelemetry activity capture. Each section prints what just happened
so you can scan the output instead of reading source.

---

## Documentation

- [Roadmap](ROADMAP.md) — twelve-month forward plan through v1.0.0 (Q2 2027): v0.6.0 developer experience, v0.7.0 outbox + polymorphic capture, v0.8.0 separate-DB audit store, v0.9.0 docs site + AOT polish, then API freeze.
- [Contributing guide](CONTRIBUTING.md)
- [Design spec](docs/superpowers/specs/2026-05-13-orionaudit-v0.1.0-design.md)
- [v0.1.0 implementation plan](docs/superpowers/plans/2026-05-13-orionaudit-v0.1.0.md)
- Sample console: [`sample/Moongazing.OrionAudit.Sample.Console`](sample/Moongazing.OrionAudit.Sample.Console)
- Benchmarks: [`bench/Moongazing.OrionAudit.Bench`](bench/Moongazing.OrionAudit.Bench)

---

## More from the Orion family

OrionAudit is one of a set of standalone .NET libraries:

- [OrionGuard](https://github.com/tunahanaliozturk/OrionGuard) - guard clauses, validation, DDD primitives.
- [OrionKey](https://github.com/tunahanaliozturk/OrionKey) - source-generated strongly-typed IDs.
- [OrionLock](https://github.com/tunahanaliozturk/OrionLock) - distributed locking.
- [OrionPatch](https://github.com/tunahanaliozturk/OrionPatch) - transactional outbox for EF Core (enqueue inside SaveChanges, dispatch at-least-once through a pluggable sink).
- [OrionVault](https://github.com/tunahanaliozturk/OrionVault) - column-level transparent data encryption at rest for EF Core.

---

## Contributing

Contributions are welcome. Please read the [Contributing Guide](CONTRIBUTING.md) before
submitting a pull request.

## License

This project is licensed under the [MIT License](LICENSE.txt).

## Author

**Tunahan Ali Ozturk** — [GitHub](https://github.com/tunahanaliozturk) — published on NuGet as **Moongazing**.
