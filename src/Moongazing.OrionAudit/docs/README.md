# OrionAudit

EF Core change-audit trail with JSON Patch diffs, multi-tenant support, time-travel reconstruction,
and OpenTelemetry instrumentation.

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
    modelBuilder.ApplyOrionAuditConfigurations();
}
```

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

## Companion packages

- **OrionAudit.AspNetCore** — `HttpContextAuditUserResolver` for ASP.NET Core apps
- **OrionAudit.Testing** — `AuditCapture` + fluent assertions for tests

See the [project repository](https://github.com/tunahanaliozturk/OrionAudit) for the full design
spec, sample app, and benchmarks.
