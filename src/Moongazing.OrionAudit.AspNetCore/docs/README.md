# OrionAudit.AspNetCore

ASP.NET Core integration for OrionAudit: `HttpContextAuditUserResolver` stamps the current user
(the `NameIdentifier` / `sub` claim of `HttpContext.User`) on every audit row, and a DI helper wires
it in alongside `IHttpContextAccessor`.

![OrionAudit packages and where they plug in](https://raw.githubusercontent.com/tunahanaliozturk/OrionAudit/master/docs/diagrams/overview.png)

## Install

```bash
dotnet add package OrionAudit.AspNetCore
```

Plugs into the core `OrionAudit` package, which it references.

## Quick start

```csharp
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.AspNetCore;

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
columns null without breaking the capture.

## Custom claim shapes

When your identity provider uses other claim types, `AddOrionAuditClaimResolver` swaps in
`ClaimAuditUserResolver`, which tries ordered claim lists you configure through
`ClaimAuditUserResolverOptions` (`IdClaimTypes` defaults to `sub`, `NameIdentifier`, `oid`,
`preferred_username`):

```csharp
builder.Services.AddOrionAuditClaimResolver(o => o.IdClaimTypes.Insert(0, "employee_id"));
```

## Pooled contexts

With `AddDbContextPool` or a singleton `AddDbContextFactory`, push the request scope so
attribution uses the current request rather than the first one:

```csharp
app.Use(async (http, next) =>
{
    using (AuditScope.PushServices(http.RequestServices))
    {
        await next();
    }
});
```

## Related packages

- `OrionAudit` - the core capture, read and reconstruction library.
- `OrionAudit.Viewer` - embeddable read-only audit-trail UI.
- `OrionAudit.Testing` - `AuditCapture` assertions and in-memory resolvers.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionAudit
- Changelog: https://github.com/tunahanaliozturk/OrionAudit/blob/master/CHANGELOG.md
- License: MIT
