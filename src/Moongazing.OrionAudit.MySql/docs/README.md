# OrionAudit.MySql

MySQL / MariaDB provider integration for [OrionAudit](https://www.nuget.org/packages/OrionAudit).
Adds a `ModelBuilder` extension that applies the OrionAudit entity configurations with
MySQL-aware column types — a native `JSON` column (MySQL 5.7+ / MariaDB 10.2+) for the diff and
snapshot payloads by default, or `LONGTEXT` for legacy builds without native JSON validation.

```bash
dotnet add package OrionAudit.MySql
```

## Quick start

```csharp
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.MySql;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Native JSON columns (the default). Pass useLongText: true on legacy MySQL builds
        // without native JSON validation.
        modelBuilder.ApplyOrionAuditMySqlConfigurations(this);
    }
}
```

The `JSON` column keeps shape validation and lets you query the diff with `JSON_EXTRACT`. Optional
overrides let you rename the audit-log, capture-queue, and snapshot-cursor tables.

See the [project repository](https://github.com/tunahanaliozturk/OrionAudit) for the full design
spec, sample app, and benchmarks.
