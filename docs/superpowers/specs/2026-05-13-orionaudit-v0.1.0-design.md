# OrionAudit v0.1.0 — Design Spec

**Date:** 2026-05-13
**Status:** Approved (design); pending implementation plan
**Authors:** Tunahan Ali Ozturk
**Family:** Orion (sibling of OrionGuard)

## 1. Goal

Ship an EF Core-native change-audit library that answers compliance, support, and forensics questions: *"Who changed what, when, and what did it look like before?"* — with structured diffs and time-travel reconstruction.

OrionAudit is **standalone** within the Orion family. It does not depend on OrionGuard. It may coexist (a consumer using both will get domain events and audit history independently) but the libraries do not know about each other.

## 2. Scope

### In scope (v0.1.0)

1. EF Core `SaveChangesInterceptor` that captures every Insert / Update / Delete against `[Auditable]`-marked (or fluently-configured) entities.
2. JSON Patch (RFC 6902) diff format.
3. Field-level controls: `[NotAuditable]`, `[HashedAudit]`, `[RedactedAudit]` attributes + fluent equivalents.
4. Pluggable `IAuditUserResolver` and `IAuditTenantResolver`.
5. Multi-tenant transparent filtering on reads.
6. `ReconstructAsync<T>(id, asOf)` and `ReconstructManyAsync<T>(ids, asOf)` for time-travel.
7. Direct LINQ access via `AuditFor<T>()` and `AuditLog()` extension methods.
8. Synchronous audit write (same transaction as entity changes).
9. ASP.NET Core integration package with `HttpContextAuditUserResolver`.
10. Testing helpers package with framework-agnostic assertions.
11. OpenTelemetry instrumentation (ActivitySource + counters + histograms).
12. Multi-target `net8.0;net9.0;net10.0`.
13. NativeAOT-aware annotations where reflection is used.

### Out of scope (deferred)

- **v0.2.0:** Snapshot interval opt-in for fast time-travel on long histories; IQueryable `AsOf(date)` extension; PII GDPR helpers (`Audit.Forget(id)`); MongoDB storage provider.
- **v0.3.0:** Archival job; natural-language audit query DSL; gRPC remote audit reader.
- **v1.0.0:** API surface freeze + backward-compat guarantees.

## 3. Architecture

### 3.1 Component diagram

```
Consumer's SaveChangesAsync
        │
        ▼
┌──────────────────────────────────┐
│ AuditSaveChangesInterceptor      │
│  - reads ChangeTracker            │
│  - resolves User + Tenant         │
│  - computes JSON Patch diff       │
│  - adds AuditLog rows to DbContext│
└──────────┬───────────────────────┘
           │
           ▼
   SaveChangesAsync continues
           │
           ▼
   AuditLog rows persisted
   in same transaction as
   the original entity changes
```

### 3.2 Read API

```
ReconstructAsync<Order>(id, asOf)
        │
        ▼
1. Find creation audit (Inserted) for (typeof(Order), id)
2. Hydrate initial state from creation diff
3. Apply each subsequent Updated diff up to asOf
4. If a Deleted record exists at or before asOf → return null
5. Otherwise return reconstructed Order instance
```

`ReconstructManyAsync<T>(ids, asOf)` issues a single audit query grouped by entity id and replays each in parallel.

`AuditFor<T>()` returns `IQueryable<AuditLog>` filtered to `EntityType == typeof(T).AssemblyQualifiedName`, optionally tenant-scoped.

### 3.3 Lifetimes

- `AuditSaveChangesInterceptor` — Scoped (per request / per DbContext)
- `IAuditUserResolver` — Scoped
- `IAuditTenantResolver` — Scoped
- Audit configuration (attributes + fluent rules) — Singleton, frozen at startup

## 4. Storage Model

### 4.1 `AuditLog` table

| Column | Type | Notes |
|---|---|---|
| `Id` | `Guid` | Primary key, auto-generated |
| `EntityType` | `nvarchar(512)` | Assembly-qualified type name |
| `EntityId` | `nvarchar(128)` | Serialized primary key |
| `Action` | `tinyint` | enum `AuditAction { Inserted = 0, Updated = 1, Deleted = 2 }` |
| `OccurredOnUtc` | `datetime2` | UTC timestamp captured in interceptor |
| `UserId` | `nvarchar(128)?` | Nullable; populated by resolver if registered |
| `UserDisplay` | `nvarchar(256)?` | Nullable; resolver may include display name |
| `UserType` | `nvarchar(32)?` | Nullable; `"user"`, `"system"`, `"job"`, etc. |
| `TenantId` | `nvarchar(128)?` | Nullable; null = single-tenant |
| `CorrelationId` | `nvarchar(64)?` | W3C TraceParent or custom |
| `Diff` | `nvarchar(max)` | JSON Patch operations array, never null (empty `[]` for no-change rows is forbidden) |
| `Snapshot` | `nvarchar(max)?` | **Populated** with the last-known full entity JSON for `Deleted` actions in v0.1.0 (enables reconstruction of deleted entities). **Null** for `Inserted` and `Updated` actions in v0.1.0; v0.2 may populate at snapshot intervals to accelerate time-travel on long histories. |
| `Error` | `nvarchar(max)?` | Non-null if diff computation failed; row still written for chain continuity |

### 4.2 Indexes

- `IX_OrionAudit_EntityLookup` on `(EntityType, EntityId, OccurredOnUtc)` — primary lookup pattern for ReconstructAsync and per-entity history reads.
- `IX_OrionAudit_TenantTimeline` on `(TenantId, OccurredOnUtc)` — tenant-wide audit timelines.
- `IX_OrionAudit_UserActivity` on `(UserId, OccurredOnUtc)` — per-user activity queries.

### 4.3 Configuration via `AuditLogEntityTypeConfiguration`

Applied to the consumer's `DbContext.OnModelCreating` via `modelBuilder.ApplyOrionAuditConfigurations()`. The configuration accepts a table name (default `OrionAudit_Log`) and applies the indexes listed above.

### 4.4 Primary key support

v0.1.0 supports only **single-column primary keys**. The PK value is serialized to a string via `key.ToString()` (Guid, int, long, string, and other types whose `ToString()` produces stable identifiers). Composite primary keys (`HasKey(e => new { e.X, e.Y })`) are **not supported** in v0.1.0 — `Audit<T>()` against a composite-key entity throws `OrionAuditConfigurationException` at startup. v0.2 may extend to composite keys via JSON serialization of the key components.

Strongly-typed IDs that override `ToString()` (e.g. OrionGuard's generated `[StronglyTypedId<TValue>]` structs) work out of the box because their `ToString()` is stable and round-trippable. Consumers using strongly-typed IDs from other libraries should verify the same property.

## 5. Capture Mechanism

### 5.1 Interceptor lifecycle

```csharp
public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
    DbContextEventData eventData,
    InterceptionResult<int> result,
    CancellationToken cancellationToken = default)
{
    var ctx = eventData.Context!;
    var auditedEntries = ctx.ChangeTracker.Entries()
        .Where(e => auditConfiguration.IsAudited(e.Entity.GetType()))
        .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
        .ToList();

    if (auditedEntries.Count == 0)
        return await base.SavingChangesAsync(eventData, result, cancellationToken);

    var user = userResolver?.Resolve(serviceProvider);
    var tenantId = tenantResolver?.Resolve(serviceProvider);
    var correlationId = Activity.Current?.Id;
    var occurredOn = clock.UtcNow;

    foreach (var entry in auditedEntries)
    {
        var auditLog = BuildAuditLog(entry, user, tenantId, correlationId, occurredOn);
        ctx.Add(auditLog);
    }

    return await base.SavingChangesAsync(eventData, result, cancellationToken);
}
```

### 5.2 Building the audit log row

```
For each entry:
  1. Determine action: Added → Inserted, Modified → Updated, Deleted → Deleted
  2. Extract before/after snapshots:
     - Inserted: before = empty, after = entry.CurrentValues
     - Updated: before = entry.OriginalValues, after = entry.CurrentValues
     - Deleted: before = entry.OriginalValues, after = empty
  3. Apply field filtering per Auditable configuration:
     - NotAuditable fields removed from both snapshots
     - HashedAudit fields replaced with SHA-256 hex
     - RedactedAudit fields replaced with literal "<redacted>"
  4. Compute JSON Patch diff (before → after)
  5. For Deleted: serialize the post-filter before-snapshot to Snapshot column
  6. Serialize PK to string for EntityId column
  7. Construct AuditLog row
```

### 5.3 Diff computation failure handling

Diff failures (circular reference, custom `JsonConverter` throws, unsupported types) MUST NOT break `SaveChangesAsync`. The interceptor catches diff exceptions and writes the row with `Diff = "[]"` and `Error = <exception text>`. The audit chain remains intact; operators see the error and fix the entity configuration without losing subsequent audits.

The interceptor does NOT catch exceptions from `IAuditUserResolver` or `IAuditTenantResolver` — those propagate (consumer-supplied components, their job to be reliable).

### 5.4 DbContext pooling safety

The interceptor holds only the root `IServiceProvider` (captured at DbContext construction time via the `(sp, o) => o.AddInterceptors(...)` overload). All resolver calls happen per `SavingChangesAsync` invocation through that provider, which is the correct scope at the time of save. No per-request state is captured in fields.

## 6. Diff Format

### 6.1 JSON Patch (RFC 6902)

Stored as a JSON array of operations:

```json
[
  { "op": "replace", "path": "/Status", "value": "Shipped" },
  { "op": "replace", "path": "/UpdatedOnUtc", "value": "2026-05-13T10:15:00Z" }
]
```

**Inserted** action produces `add` operations for every audited property:

```json
[
  { "op": "add", "path": "/OrderNumber", "value": "ORD-1001" },
  { "op": "add", "path": "/Status", "value": "Pending" }
]
```

**Deleted** action produces `remove` operations for every audited property AND populates the `Snapshot` column:

```json
// Diff column
[
  { "op": "remove", "path": "/OrderNumber" },
  { "op": "remove", "path": "/Status" }
]
// Snapshot column
{ "OrderNumber": "ORD-1001", "Status": "Shipped" }
```

### 6.2 Implementation

JSON Patch generation uses [JsonPatch.Net](https://docs.json-everything.net/patch/basics/) (NuGet: `JsonPatch.Net`). The library is MIT-licensed, AOT-friendly, and produces compliant patches from before/after `JsonNode` pairs.

Snapshots are produced via `System.Text.Json.JsonSerializer.SerializeToNode(...)` with a `JsonSerializerOptions` cached per-process. Source-generated `JsonSerializerContext` integration is on the v0.2 backlog for full AOT support.

### 6.3 Sensitive field handling

| Attribute | Behaviour |
|---|---|
| `[NotAuditable]` | Field is removed from both before and after snapshots. Diff never contains it. |
| `[HashedAudit]` | Field value replaced with `SHA256(value).ToHexLowerInvariant()` in both snapshots before diff. Hash is deterministic — same input produces same hash, so equality detection still works. |
| `[RedactedAudit]` | Field value replaced with literal `"<redacted>"` in both snapshots. Hash equality is broken (always equal). Use for fields where even existence of change is sensitive. |

Fluent equivalents:

```csharp
o.Audit<User>(b => b
    .Exclude(u => u.InternalNotes)
    .Hash(u => u.SSN)
    .Redact(u => u.Password));
```

Fluent rules take precedence over attribute rules when both are present (escape hatch for legacy entities you can't decorate).

## 7. Read API

### 7.1 Direct audit query

```csharp
var history = await db.AuditFor<Order>()
    .Where(a => a.EntityId == orderId.ToString())
    .OrderByDescending(a => a.OccurredOnUtc)
    .ToListAsync();

var deletions = await db.AuditFor<Order>()
    .Where(a => a.Action == AuditAction.Deleted)
    .Where(a => a.OccurredOnUtc >= DateTime.UtcNow.AddDays(-7))
    .ToListAsync();

var userActivity = await db.AuditLog()
    .Where(a => a.UserId == "user-123")
    .ToListAsync();
```

`AuditFor<T>()` is `IQueryable<AuditLog>` filtered to `EntityType == typeof(T).AssemblyQualifiedName`.

`AuditLog()` is `IQueryable<AuditLog>` over the full table.

Both methods automatically apply `WHERE TenantId = currentTenant` if `IAuditTenantResolver` is registered. Cross-tenant queries opt-in via `AuditFor<T>(crossTenant: true)` and `AuditLog(crossTenant: true)`.

### 7.2 Reconstruction

```csharp
public interface IAuditReconstructor
{
    Task<T?> ReconstructAsync<T>(string entityId, DateTime asOf, CancellationToken ct = default)
        where T : class, new();

    Task<IReadOnlyDictionary<string, T?>> ReconstructManyAsync<T>(
        IEnumerable<string> entityIds, DateTime asOf, CancellationToken ct = default)
        where T : class, new();

    Task<T?> GetSnapshotBeforeAsync<T>(Guid auditId, CancellationToken ct = default) where T : class, new();
    Task<T?> GetSnapshotAfterAsync<T>(Guid auditId, CancellationToken ct = default) where T : class, new();
}
```

**Reconstruction algorithm** (single entity):

1. Load all `AuditLog` rows for `(typeof(T), entityId)` with `OccurredOnUtc <= asOf`, ordered ascending.
2. If empty → return `null` (entity did not exist at `asOf`).
3. Check terminal action: if last loaded row is `Deleted` → return `null`.
4. Start with empty `JsonObject`.
5. Apply each row's `Diff` (JSON Patch) in order.
6. Deserialize the resulting `JsonObject` to `T` using `JsonSerializer.Deserialize<T>(node)`.

**Batch reconstruction** (`ReconstructManyAsync`):

1. Single `WHERE EntityType = X AND EntityId IN (...) AND OccurredOnUtc <= asOf` query.
2. Group results in memory by `EntityId`.
3. Reconstruct each group in parallel (bounded `Parallel.ForEachAsync`).
4. Return `Dictionary<string, T?>` mapping each requested id to the reconstructed instance (or null if not present).

**Performance notes (documented in XML doc):**

- Reconstruction is **O(N)** in the number of audit rows for that entity. For entities with >1000 historical changes, expect noticeable latency. v0.2's snapshot interval feature addresses this.
- Single-entity `ReconstructAsync` is fine for most ad-hoc compliance queries. Bulk historical reads should use `AuditFor<T>()` raw and process diffs at the application layer if performance matters.

### 7.3 Cross-cutting query helpers

```csharp
// Convenience: all audit entries that touched a specific user
db.AuditLog().Where(a => a.UserId == "user-123")

// Convenience: all audit entries within a tenant timeline window
db.AuditLog().Where(a => a.OccurredOnUtc.Between(start, end))   // extension method
```

## 8. User & Tenant Resolution

### 8.1 Contracts

```csharp
public interface IAuditUserResolver
{
    AuditUser? Resolve(IServiceProvider serviceProvider);
}

public interface IAuditTenantResolver
{
    string? Resolve(IServiceProvider serviceProvider);
}

public sealed record AuditUser(string Id, string? DisplayName = null, string Type = "user");
```

Both resolvers are **synchronous** and **may return null**. Null means "no attributable user/tenant" — the corresponding columns stay null on the audit row.

### 8.2 Built-in resolvers (in `OrionAudit.AspNetCore`)

```csharp
public sealed class HttpContextAuditUserResolver : IAuditUserResolver
{
    public AuditUser? Resolve(IServiceProvider sp)
    {
        var httpCtx = sp.GetService<IHttpContextAccessor>()?.HttpContext;
        var user = httpCtx?.User;
        if (user?.Identity?.IsAuthenticated != true) return null;

        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
              ?? user.FindFirst("sub")?.Value;
        if (id is null) return null;

        var display = user.FindFirst(ClaimTypes.Name)?.Value;
        return new AuditUser(id, display, "user");
    }
}
```

ASP.NET package also provides a tenant resolver convenience pulling from `HttpContext.Items["TenantId"]` — left as an extension example; consumers usually have their own tenant strategy.

### 8.3 DI registration

```csharp
services.AddOrionAudit<AppDbContext>(o =>
{
    o.UserResolver<HttpContextAuditUserResolver>();
    o.TenantResolver<MyTenantResolver>();
});

services.AddOrionAuditAspNetCore();
```

`AddOrionAuditAspNetCore()` is a convenience that calls `AddHttpContextAccessor()` and registers `HttpContextAuditUserResolver` as the default `IAuditUserResolver`. It's optional — consumers can wire the resolver manually.

## 9. Configuration

### 9.1 Attribute-based opt-in

```csharp
[Auditable]
public class Order
{
    public Guid Id { get; set; }
    public string Status { get; set; }

    [NotAuditable] public string InternalNotes { get; set; }
    [HashedAudit]  public string CustomerEmail { get; set; }
    [RedactedAudit] public string PaymentToken { get; set; }
}
```

Only entities marked `[Auditable]` are tracked. Properties without sensitive-field attributes are audited normally.

### 9.2 Fluent opt-in

```csharp
services.AddOrionAudit<AppDbContext>(o =>
{
    o.Audit<Order>(b => b
        .Exclude(o => o.InternalNotes)
        .Hash(o => o.CustomerEmail)
        .Redact(o => o.PaymentToken));

    o.Audit<Customer>();   // no field overrides
});
```

Fluent rules supersede attribute rules — escape hatch for entities you cannot annotate (third-party types, legacy code).

### 9.3 Type discovery

At startup, `AddOrionAudit<TDbContext>` scans:

1. All types in the `TDbContext` assembly + assemblies passed via `o.ScanAssembly(...)`.
2. Looks for `[Auditable]` attribute on classes.
3. Loads fluent rules from the configuration callback.
4. Merges into a frozen `IAuditConfiguration` singleton.

Discovery is `O(types in scanned assemblies)` once at startup. Hot-path lookup (`IsAudited(type)`) is `O(1)` via `FrozenDictionary`.

### 9.4 Required EF Core model wiring

```csharp
public class AppDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyOrionAuditConfigurations();   // adds AuditLog entity + indexes
    }
}
```

The interceptor is wired via `DbContextOptionsBuilderExtensions.UseOrionAudit(sp)`:

```csharp
services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseSqlServer(...)
     .UseOrionAudit(sp));
```

Mirror of OrionGuard's `UseOrionGuardDomainEvents(sp)` pattern. Strict XML doc requires `(sp, o) => ...` overload.

## 10. Packages (v0.1.0)

| Package | PackageId | Targets | Description |
|---|---|---|---|
| Core | `OrionAudit` | net8/9/10 | Interceptor, AuditLog entity, IUserResolver, IReconstructor, attributes, fluent config |
| ASP.NET | `OrionAudit.AspNetCore` | net8/9/10 | HttpContextAuditUserResolver + DI helper |
| Testing | `OrionAudit.Testing` | net8/9/10 | AuditCapture, AssertionException, InMemoryAuditUserResolver |

Each package follows the OrionGuard model: `TreatWarningsAsErrors=true`, `GenerateDocumentationFile=true`, `docs/README.md` + `docs/logo.png` packed, MIT license, AOT-aware.

External dependencies:
- `OrionAudit` core: `Microsoft.EntityFrameworkCore 9.0.0`, `Microsoft.EntityFrameworkCore.Relational 9.0.0`, `Microsoft.Extensions.DependencyInjection.Abstractions 9.0.0`, `Microsoft.Extensions.Logging.Abstractions 9.0.0`, `JsonPatch.Net (latest)`.
- `OrionAudit.AspNetCore`: above + `Microsoft.AspNetCore.Http.Abstractions`.
- `OrionAudit.Testing`: only `OrionAudit` (no test-framework dependencies — same framework-agnostic promise as OrionGuard.Testing).

## 11. Observability

### 11.1 ActivitySource + Meter

```csharp
public static class OrionAuditTelemetry
{
    public const string ActivitySourceName = "OrionAudit";
    public const string MeterName = "OrionAudit";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.1.0");
    internal static readonly Meter Meter = new(MeterName, "0.1.0");

    internal static readonly Counter<long> EntriesWritten =
        Meter.CreateCounter<long>("orionaudit.entries.written", "entries", "Audit entries successfully written.");
    internal static readonly Counter<long> EntriesFailed =
        Meter.CreateCounter<long>("orionaudit.entries.failed", "entries", "Audit entries written with diff errors.");
    internal static readonly Histogram<double> CaptureDuration =
        Meter.CreateHistogram<double>("orionaudit.capture.duration", "ms", "Interceptor capture duration per save.");
    internal static readonly Histogram<double> ReconstructDuration =
        Meter.CreateHistogram<double>("orionaudit.reconstruct.duration", "ms", "Time-travel reconstruction duration.");
}
```

### 11.2 Spans

The interceptor opens `OrionAudit.Capture` span per `SavingChangesAsync` invocation when at least one audited entity is in the change tracker. Tags include `entity_count`, `tenant_id` (if resolved), `user_type` (if resolved).

`ReconstructAsync` opens `OrionAudit.Reconstruct` span per call. Tags: `entity_type`, `audit_row_count`, `as_of`.

No decorator pattern (unlike OrionGuard's `InstrumentedDomainEventDispatcher`) — observability is inline in the interceptor and reconstructor. Reason: audit is a single passage of control (interceptor), no consumer-supplied dispatcher to decorate.

### 11.3 DI registration

```csharp
services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(OrionAuditTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(OrionAuditTelemetry.MeterName));
```

OrionAudit does not register OpenTelemetry collectors itself — consumer wires their own SDK and adds the source/meter names.

## 12. Error Handling

### 12.1 Failure modes

| Failure | Behaviour | Surfaced to |
|---|---|---|
| Diff computation throws (circular ref, custom JsonConverter throws) | Row written with `Diff = "[]"`, `Error = <message>`, `OrionAuditTelemetry.EntriesFailed++` | Telemetry + audit row itself |
| `IAuditUserResolver` throws | Exception propagates, save fails | Consumer code |
| `IAuditTenantResolver` throws | Exception propagates, save fails | Consumer code |
| `JsonPatch` library throws | Treated as diff failure (same as above) | Telemetry + audit row |
| `AuditLog` insertion fails due to constraint | EF Core exception propagates, save fails | Consumer code (genuine bug or DB issue) |
| Reconstruction encounters corrupted diff JSON | `OrionAuditException` thrown with `EntityType + EntityId + AuditId` | Caller of `ReconstructAsync` |

### 12.2 Reconstruction edge cases

- **No audit rows at all** for `(EntityType, entityId)` and `asOf >= now`: return `null` (entity never existed).
- **`asOf` before first Inserted**: return `null` (entity did not exist at that time).
- **Audit chain starts with Updated, not Inserted**: malformed history. Return `OrionAuditException` with diagnostic ("entity exists but no creation record"). Operator must investigate.
- **Audit chain contains Deleted then Inserted (resurrection)**: treat the latest Inserted as a new lifecycle. Reconstruct from there. Document this behaviour in XML doc.

### 12.3 Configuration errors

- `AddOrionAudit` called without `ApplyOrionAuditConfigurations()` in `OnModelCreating`: first SaveChanges with an `[Auditable]` entity throws `OrionAuditConfigurationException` with clear message.
- `AddOrionAudit` called with `Audit<T>()` where `T` has no PK detectable in EF model: throws at startup.

## 13. Migration (Consumer Side)

Adding OrionAudit to an existing application requires one EF Core migration: the `OrionAudit_Log` table + three indexes. The migration is generated by EF Core normally because the entity is part of the consumer's `DbContext` (via `ApplyOrionAuditConfigurations`).

```bash
dotnet ef migrations add AddOrionAudit
dotnet ef database update
```

No data migration is required — existing entities have no audit history, and that's correct (their pre-OrionAudit history is genuinely unknown).

## 14. Testing Strategy

### 14.1 Test projects

| Project | Targets | Notes |
|---|---|---|
| `OrionAudit.Tests` | net10.0 | Core interceptor, diff, configuration, attributes, fluent rules |
| `OrionAudit.AspNetCore.Tests` | net10.0 | HttpContextAuditUserResolver behaviour |
| `OrionAudit.Testing.Tests` | net10.0 | Test helpers' own behaviour |
| `OrionAudit.IntegrationTests` | net10.0 | Sqlite-in-memory + EF Core retry strategies + tenant filtering |

### 14.2 Coverage targets

- **Core interceptor:** 30+ tests covering Insert / Update / Delete, sensitive-field filtering, deletion snapshot, tenant population, user population, diff failure path.
- **Reconstruction:** 15+ tests covering single, batch, pre-creation date, post-deletion date, resurrection chain, malformed history.
- **Resolvers:** 8+ tests (null, populated, exception, ASP.NET HttpContext variants).
- **Configuration:** 5+ tests (attribute + fluent merge, fluent override, missing PK error).
- **Integration:** 12+ tests (full DbContext lifecycle, tenant isolation, EF Core retry strategy compatibility).

**Target total:** 70+ tests.

### 14.3 Testing helpers (`OrionAudit.Testing`)

```csharp
public sealed class AuditCapture
{
    public static AuditCapture From(DbContext ctx);
    public IReadOnlyList<AuditLog> All { get; }
    public AuditAssertions Should();
}

public sealed class AuditAssertions
{
    public AuditAssertions HaveLogged<T>(AuditAction action);
    public AuditAssertions HaveLogged<T>(AuditAction action, Func<AuditLog, bool> predicate);
    public AuditAssertions NotHaveLogged<T>();
    public CountAssertion HaveLoggedExactly(int n);
}

public sealed class InMemoryAuditUserResolver : IAuditUserResolver
{
    public InMemoryAuditUserResolver(AuditUser? user = null);
    public AuditUser? Resolve(IServiceProvider sp) => User;
    public AuditUser? User { get; set; }
}

public sealed class InMemoryAuditTenantResolver : IAuditTenantResolver
{
    public InMemoryAuditTenantResolver(string? tenantId = null);
    public string? Resolve(IServiceProvider sp) => TenantId;
    public string? TenantId { get; set; }
}
```

Throws `OrionAuditAssertionException` (not framework-specific) on assertion failure. Matches OrionGuard.Testing's pattern.

## 15. Public API Summary

### 15.1 Core types (`OrionAudit`)

```
OrionAudit.AuditableAttribute
OrionAudit.NotAuditableAttribute
OrionAudit.HashedAuditAttribute
OrionAudit.RedactedAuditAttribute
OrionAudit.AuditAction (enum)
OrionAudit.AuditUser (record)
OrionAudit.AuditLog (entity)
OrionAudit.OrionAuditException
OrionAudit.OrionAuditConfigurationException
OrionAudit.IAuditUserResolver
OrionAudit.IAuditTenantResolver
OrionAudit.IAuditReconstructor
OrionAudit.OrionAuditOptions (DI options)
OrionAudit.AuditSaveChangesInterceptor
OrionAudit.AuditLogEntityTypeConfiguration
OrionAudit.AuditQueryExtensions   (AuditFor<T>, AuditLog())
OrionAudit.AuditModelBuilderExtensions   (ApplyOrionAuditConfigurations)
OrionAudit.DbContextOptionsBuilderExtensions   (UseOrionAudit)
OrionAudit.AuditServiceCollectionExtensions   (AddOrionAudit<TContext>)
OrionAudit.AuditTelemetry   (ActivitySource + Meter)
```

### 15.2 ASP.NET types (`OrionAudit.AspNetCore`)

```
OrionAudit.AspNetCore.HttpContextAuditUserResolver
OrionAudit.AspNetCore.AuditAspNetCoreServiceCollectionExtensions  (AddOrionAuditAspNetCore)
```

### 15.3 Testing types (`OrionAudit.Testing`)

```
OrionAudit.Testing.AuditCapture
OrionAudit.Testing.AuditAssertions
OrionAudit.Testing.OrionAuditAssertionException
OrionAudit.Testing.InMemoryAuditUserResolver
OrionAudit.Testing.InMemoryAuditTenantResolver
```

## 16. Definition of Done (v0.1.0)

- All listed types implemented.
- 70+ tests passing across 4 test projects.
- Integration tests covering Sqlite-in-memory + EF Core retry strategy.
- ASP.NET Core sample app demonstrating end-to-end audit + reconstruction.
- README per package (root README + per-package docs/README.md).
- CHANGELOG with v0.1.0 entry.
- Multi-target build clean: `net8.0;net9.0;net10.0`.
- AOT trim analysis clean (annotations on reflection paths).
- NuGet packages produced for all 3.
- CI/CD workflow (matrix build + release-triggered publish) in place.

## 17. Roadmap Beyond v0.1.0

- **v0.2.0:** Snapshot interval (faster time-travel on long histories); `IQueryable.AsOf(date)` extension with documented client-side materialization warning; PII GDPR helpers (`Audit.Forget(entityId)` pseudonymization); System.Text.Json source-gen integration.
- **v0.3.0:** Archival job (move audit rows older than N to cold storage); natural-language audit query DSL; gRPC remote audit reader for cross-service audit queries.
- **v1.0.0:** Public API freeze; semantic-versioning guarantees.
