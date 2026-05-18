# OrionAudit

EF Core change-audit trail with JSON Patch diffs, multi-tenant support, time-travel reconstruction,
ASP.NET Core integration, framework-agnostic test helpers, and OpenTelemetry instrumentation.

Part of the Orion family of standalone .NET libraries.

## Install

```bash
dotnet add package OrionAudit
dotnet add package OrionAudit.AspNetCore   # optional, for HttpContext-based user attribution
dotnet add package OrionAudit.Testing      # optional, fluent assertions for tests
```

## Quick start

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.AspNetCore;

services.AddOrionAudit<AppDbContext>(o => o
    .Audit<Order>()
    .Audit<Customer>(b => b.Hash(c => c.Email).Redact(c => c.Token))
    .UserResolver<HttpContextAuditUserResolver>());

services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseSqlServer(connectionString)
     .UseOrionAudit(sp));
```

In your `DbContext`:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyOrionAuditConfigurations();
}
```

## Reading the audit trail

```csharp
// All audit rows for Order, automatically filtered by current tenant.
var rows = await context.AuditFor<Order>().OrderByDescending(a => a.OccurredOnUtc).ToListAsync();

// Cross-tenant query (bypasses the tenant filter).
var globalRows = await context.AuditFor<Order>(crossTenant: true).ToListAsync();
```

## Time-travel reconstruction

```csharp
var reconstructor = serviceProvider.GetRequiredService<IAuditReconstructor>();

var orderAsOfLastMonth = await reconstructor.ReconstructAsync<Order>(
    entityId: order.Id.ToString(),
    asOf: DateTime.UtcNow.AddMonths(-1));
```

## Features

- **Auto-capture** — `SaveChangesInterceptor` writes Insert / Update / Delete rows in the same transaction
- **JSON Patch diffs** — RFC 6902 with `JsonPatch.Net`, replayable for time-travel reconstruction
- **Sensitive-field control** — `[NotAuditable]` / `[HashedAudit]` / `[RedactedAudit]` attributes plus fluent equivalents
- **Multi-tenant** — pluggable `IAuditTenantResolver` with automatic read-side filtering
- **User attribution** — pluggable `IAuditUserResolver`; `HttpContextAuditUserResolver` ships in the AspNetCore package
- **Time travel** — `IAuditReconstructor` replays diffs to reconstruct entity state at any historical timestamp
- **Test helpers** — framework-agnostic `AuditCapture` + fluent assertions in `OrionAudit.Testing`
- **OpenTelemetry** — `OrionAudit` ActivitySource + Meter with capture/reconstruct instrumentation

## Multi-targeting

`OrionAudit`, `OrionAudit.AspNetCore`, and `OrionAudit.Testing` target `net8.0`, `net9.0`, and `net10.0`.

## Documentation

- [Design spec](docs/superpowers/specs/2026-05-13-orionaudit-v0.1.0-design.md)
- [v0.1.0 implementation plan](docs/superpowers/plans/2026-05-13-orionaudit-v0.1.0.md)
- Sample console app: [sample/OrionAudit.Sample.Console](sample/OrionAudit.Sample.Console)

## License

MIT — see [LICENSE.txt](LICENSE.txt).
