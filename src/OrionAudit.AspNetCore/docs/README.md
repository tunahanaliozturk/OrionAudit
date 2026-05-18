# OrionAudit.AspNetCore

ASP.NET Core integration for [OrionAudit](https://www.nuget.org/packages/OrionAudit). Provides
`HttpContextAuditUserResolver` (pulls the current user from `HttpContext.User` via the
`NameIdentifier` / `sub` claim) and a DI helper that wires it in alongside `IHttpContextAccessor`.

```bash
dotnet add package OrionAudit.AspNetCore
```

## Quick start

```csharp
using Microsoft.EntityFrameworkCore;
using OrionAudit;
using OrionAudit.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOrionAudit<AppDbContext>(o => o
        .Audit<Order>()
        .UserResolver<HttpContextAuditUserResolver>())
    .AddOrionAuditAspNetCore();

builder.Services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseSqlServer(connectionString).UseOrionAudit(sp));

var app = builder.Build();
app.Run();
```

The captured `AuditLog.UserId` / `UserDisplay` columns are populated from the authenticated
user's claims on every request that triggers a `SaveChanges`. Anonymous requests leave the
columns null.

See the [project repository](https://github.com/tunahanaliozturk/OrionAudit) for the full
design spec, sample app, and benchmarks.
