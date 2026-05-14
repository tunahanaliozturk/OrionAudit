# OrionAudit.Testing

Testing helpers for [OrionAudit](https://www.nuget.org/packages/OrionAudit). Provides `AuditCapture`, fluent assertions, and in-memory resolvers. Framework-agnostic - no dependency on xUnit, NUnit, or FluentAssertions.

## Install

```bash
dotnet add package OrionAudit.Testing
```

## Usage

```csharp
using OrionAudit.Testing;

// Arrange
using var ctx = new AppDbContext(options);
var capture = AuditCapture.From(ctx);

// Act
ctx.Orders.Add(new Order { Total = 100m });
await ctx.SaveChangesAsync();

// Assert
capture.Entries.ShouldHaveSingleInsertOf<Order>();
```

See the [project repository](https://github.com/tunahanaliozturk/OrionAudit) for full documentation.
