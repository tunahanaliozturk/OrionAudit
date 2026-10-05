# OrionAudit.MySql

MySQL / MariaDB provider integration for OrionAudit. Adds a `ModelBuilder` extension that applies
the OrionAudit entity configurations with MySQL-aware column types — a native `JSON` column
(MySQL 5.7+ / MariaDB 10.2+) for the diff and snapshot payloads by default, or `LONGTEXT` for
legacy builds without native JSON validation.

![OrionAudit packages and where they plug in](https://raw.githubusercontent.com/tunahanaliozturk/OrionAudit/master/docs/diagrams/overview.png)

## Install

```bash
dotnet add package OrionAudit.MySql
```

Plugs into the core `OrionAudit` package, which it references. Use it with your MySQL EF Core
provider; register capture with `AddOrionAudit` and `UseOrionAudit` as usual.

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

Use `ApplyOrionAuditMySqlConfigurations` instead of the core `ApplyOrionAuditConfigurations`.

## Options

| Parameter | Default | Effect |
| --------- | ------- | ------ |
| `useLongText` | `false` | `LONGTEXT` instead of native `JSON` for payload columns |
| `auditLogTableName` | `OrionAudit_Log` | audit-log table name |
| `captureQueueTableName` | `OrionAudit_Capture_Queue` | async-capture queue table name |
| `snapshotCursorTableName` | `OrionAudit_Snapshot_Cursors` | snapshot-cursor table name |

The `JSON` column keeps shape validation and lets you query the diff with `JSON_EXTRACT`.

## Related packages

- `OrionAudit` - the core capture, read and reconstruction library.
- `OrionAudit.Viewer` - embeddable read-only audit-trail UI.
- `OrionAudit.Testing` - `AuditCapture` assertions and in-memory test doubles.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionAudit
- Changelog: https://github.com/tunahanaliozturk/OrionAudit/blob/master/CHANGELOG.md
- License: MIT
