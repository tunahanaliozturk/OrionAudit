# OrionAudit

EF Core change audit trail with JSON Patch diffs, multi-tenant support, time-travel reconstruction, and OpenTelemetry instrumentation.

## Install

```bash
dotnet add package OrionAudit
```

## Usage

```csharp
using OrionAudit;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOrionAudit();

builder.Services.AddDbContext<AppDbContext>(options =>
    options
        .UseSqlServer(connectionString)
        .UseOrionAudit());

var app = builder.Build();
app.Run();
```

See the [project repository](https://github.com/tunahanaliozturk/OrionAudit) for full documentation.
