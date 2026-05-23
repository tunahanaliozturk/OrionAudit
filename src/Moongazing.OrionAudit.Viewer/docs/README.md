# OrionAudit.Viewer

Embeddable audit-trail viewer for [OrionAudit](https://www.nuget.org/packages/OrionAudit).

One endpoint registration mounts a JSON API plus a built-in static UI — no Blazor, no
build step, drops into any ASP.NET Core host.

```csharp
app.MapOrionAuditViewer<AppDbContext>("/audit", o => o.RequireAuthorization("AuditViewers"));
```

The viewer is read-only and authorization-required by default. See the OrionAudit
repository README for the full guide.
