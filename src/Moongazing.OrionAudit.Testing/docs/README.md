# OrionAudit.Testing

Testing helpers for OrionAudit: `AuditCapture`, fluent assertions, in-memory resolvers and an
in-memory history store. Framework-agnostic, with no dependency on xUnit, NUnit or
FluentAssertions; it throws plain exceptions on failure so it works with any test runner.

![OrionAudit packages and where they plug in](https://raw.githubusercontent.com/tunahanaliozturk/OrionAudit/master/docs/diagrams/overview.png)

## Install

```bash
dotnet add package OrionAudit.Testing
```

Plugs into the core `OrionAudit` package, which it references.

## Capture and assert

```csharp
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Testing;

// Act — write something that produces an audit row.
ctx.Orders.Add(new Order { Status = "Pending" });
await ctx.SaveChangesAsync();

// Assert.
AuditCapture.From(ctx)
    .Should()
    .HaveLogged<Order>(AuditAction.Inserted)
    .HaveLoggedExactly(1).Of<Order>();
```

## In-memory user / tenant resolvers

Replace the real resolvers in tests when you need deterministic attribution:

```csharp
services.AddSingleton<IAuditUserResolver>(new InMemoryAuditUserResolver(
    new AuditUser("test-user", "Test User")));

services.AddSingleton<IAuditTenantResolver>(new InMemoryAuditTenantResolver("tenant-A"));
```

Both resolvers expose mutable `User` / `TenantId` properties so the same instance can be
re-pointed mid-test to simulate cross-tenant scenarios.

## In-memory history store

`InMemoryAuditHistoryStore` implements the full `IAuditHistoryStore` surface (query, aggregate,
compact) over an in-memory row list, for tests and prototyping against the abstraction.

## Why no FluentAssertions / xUnit dependency

`OrionAudit.Testing` ships with applications that run their tests on any framework. Pulling in a
specific assertion library would force a choice on consumers. Failures throw
`OrionAuditAssertionException` — every modern test runner treats any thrown exception as a test
failure, so this works everywhere.

## Related packages

- `OrionAudit` - the core capture, read and reconstruction library.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionAudit
- Changelog: https://github.com/tunahanaliozturk/OrionAudit/blob/master/CHANGELOG.md
- License: MIT
