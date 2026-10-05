# OrionAudit

EF Core change-audit trail with JSON Patch diffs, multi-tenant support, time-travel reconstruction,
tamper-evident hash chaining and OpenTelemetry instrumentation.

![OrionAudit capture flow on SaveChanges, sync and async modes](https://raw.githubusercontent.com/tunahanaliozturk/OrionAudit/master/docs/diagrams/capture-flow.png)

## Install

```bash
dotnet add package OrionAudit
```

## Quick start

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;

services.AddOrionAudit<AppDbContext>(o => o
    .Audit<Order>()
    .Audit<Customer>(b => b.Hash(c => c.Email).Redact(c => c.Token)));

services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseSqlServer(connectionString)
     .UseOrionAudit(sp));
```

In your `DbContext`:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyOrionAuditConfigurations(this);
}
```

Every `SaveChanges` that adds, modifies or deletes an audited entity now writes an `AuditLog` row
(table `OrionAudit_Log`) in the same transaction as the change.

## Read audit history

```csharp
var rows = await context.AuditFor<Order>()
    .OrderByDescending(a => a.OccurredOnUtc)
    .ToListAsync();
```

## Time-travel reconstruction

```csharp
var reconstructor = serviceProvider.GetRequiredService<IAuditReconstructor>();
var orderAsOfYesterday = await reconstructor.ReconstructAsync<Order>(
    order.Id.ToString(),
    DateTime.UtcNow.AddDays(-1));
```

Reconstruction is tenant-scoped, through the same filter the read DSL uses: it returns `null` for
an id whose rows belong to another tenant. When a registered `IAuditTenantResolver` cannot name a
tenant, the replay is scoped to the no-tenant stream rather than refused outright — rows that were
never tenant-stamped still reconstruct (a single-tenant deployment is unaffected), and only
tenant-stamped entities come back `null`. There is no `crossTenant` escape hatch — a cross-tenant
replay is not a wider read but an incorrect entity.

## Capture modes and failure semantics

- **Synchronous (default):** the diff is computed inside `SaveChanges` and the `AuditLog` row
  commits with your rows. A snapshot or diff failure does not abort the save: the row is written
  with `Diff = []` and the exception in `Error`.
- **Async (`UseAsyncCapture`):** a lightweight `OrionAudit_Capture_Queue` row commits with your
  rows instead, and a hosted dispatcher writes the `AuditLog` row later. Defaults:
  `PollInterval` 2 s, `BatchSize` 500, `MaxAttempts` 5; a row that keeps failing is dead-lettered
  (`Error` set). `IAuditDispatcher.FlushPendingAsync` drains the queue on demand.
- **Hash chain (`UseHashChain`, off by default):** each row gets a keyed HMAC-SHA256 `EntryHash`
  chained to the previous row of its stream; `IAuditIntegrityVerifier.VerifyChainAsync` reports
  the first broken row.

## Related packages

- `OrionAudit.AspNetCore` - `HttpContextAuditUserResolver` for user attribution in ASP.NET Core.
- `OrionAudit.MySql` - MySQL / MariaDB mapping with `JSON` or `LONGTEXT` payload columns.
- `OrionAudit.Viewer` - embeddable read-only audit-trail UI (`MapOrionAuditViewer`).
- `OrionAudit.Testing` - `AuditCapture` assertions and in-memory test doubles.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionAudit
- Changelog: https://github.com/tunahanaliozturk/OrionAudit/blob/master/CHANGELOG.md
- License: MIT
