# OrionAudit.Viewer

Embeddable audit-trail viewer for [OrionAudit](https://www.nuget.org/packages/OrionAudit).

One endpoint registration mounts a JSON API plus a built-in UI — no Blazor, no build step, drops
into any ASP.NET Core host. The page is rendered server-side and every audit value is HTML-encoded
on the way out, so a value written into an audited entity cannot execute as script in the session
of whoever reviews the log.

```csharp
app.MapOrionAuditViewer<AppDbContext>("/audit", o => o.RequireAuthorization("AuditViewers"));
```

The viewer is read-only, and the access decision is mandatory: `MapOrionAuditViewer` throws at
startup unless the registration calls `RequireAuthorization(...)` (a named or inline policy) or
`AllowAnonymous()` (local development only). See the OrionAudit repository README for the full
guide.
