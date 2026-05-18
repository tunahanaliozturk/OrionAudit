# OrionAudit.Testing

Testing helpers for [OrionAudit](https://www.nuget.org/packages/OrionAudit). Provides
`AuditCapture`, fluent assertions, and in-memory resolvers — framework-agnostic, with no
dependency on xUnit, NUnit, or FluentAssertions. Throws plain exceptions on failure so it
works with any test runner.

```bash
dotnet add package OrionAudit.Testing
```

## Capture and assert

```csharp
using OrionAudit;
using OrionAudit.Testing;

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

## Why no FluentAssertions / xUnit dependency

`OrionAudit.Testing` ships with applications that run their tests on any framework. Pulling in a
specific assertion library would force a choice on consumers. Failures throw
`OrionAuditAssertionException` — every modern test runner treats any thrown exception as a test
failure, so this works everywhere.

See the [project repository](https://github.com/tunahanaliozturk/OrionAudit) for the full
design spec, sample app, and benchmarks.
