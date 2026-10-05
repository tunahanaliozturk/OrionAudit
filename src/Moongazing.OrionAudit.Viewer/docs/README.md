# OrionAudit.Viewer

Embeddable, read-only audit-trail viewer for OrionAudit. One endpoint registration mounts a JSON API
plus a built-in UI — no Blazor, no build step, drops into any ASP.NET Core host.

![OrionAudit packages and where they plug in](https://raw.githubusercontent.com/tunahanaliozturk/OrionAudit/master/docs/diagrams/overview.png)

## Install

```bash
dotnet add package OrionAudit.Viewer
```

Plugs into the core `OrionAudit` package, which it references; capture must already be registered
with `AddOrionAudit<TDbContext>`.

## Quick start

```csharp
using Moongazing.OrionAudit.Viewer;

app.MapOrionAuditViewer<AppDbContext>("/audit", o => o.RequireAuthorization("AuditViewers"));
```

That mounts `GET /audit/api/log`, `GET /audit/api/{entityType}/{key}`, `GET /audit/api/meta` and the
UI at `/audit`.

## Access decision is mandatory

The viewer exposes every recorded change of every audited entity, so `MapOrionAuditViewer` throws
`InvalidOperationException` at startup unless the registration states who gets in:

| Call | Who gets in |
| ---- | ----------- |
| `o.RequireAuthorization("AuditViewers")` | a policy you registered with `AddAuthorization` |
| `o.RequireAuthorization(p => p.RequireRole("Auditor"))` | an inline policy |
| `o.AllowAnonymous()` | everyone; local development only |

## Safe rendering and tenant scoping

The page is rendered server-side and every audit value is HTML-encoded on the way out, so a value
written into an audited entity cannot execute as script in the session of whoever reviews the log.
The JSON API returns raw values. Reads go through `db.AuditLog()`, so the registered
`IAuditTenantResolver` scopes them to the current tenant.

## Related packages

- `OrionAudit` - the core capture, read and reconstruction library.
- `OrionAudit.AspNetCore` - `HttpContextAuditUserResolver` for user attribution.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionAudit
- Changelog: https://github.com/tunahanaliozturk/OrionAudit/blob/master/CHANGELOG.md
- License: MIT
