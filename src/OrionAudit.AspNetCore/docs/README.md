# OrionAudit.AspNetCore

ASP.NET Core integration for [OrionAudit](https://www.nuget.org/packages/OrionAudit). Provides `HttpContextAuditUserResolver` and DI helpers for resolving the current user from `HttpContext`.

## Install

```bash
dotnet add package OrionAudit.AspNetCore
```

## Usage

```csharp
using OrionAudit;
using OrionAudit.AspNetCore;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOrionAudit()
    .AddOrionAuditAspNetCore();

builder.Services.AddDbContext<AppDbContext>(options =>
    options
        .UseSqlServer(connectionString)
        .UseOrionAudit());

var app = builder.Build();
app.Run();
```

See the [project repository](https://github.com/tunahanaliozturk/OrionAudit) for full documentation.
