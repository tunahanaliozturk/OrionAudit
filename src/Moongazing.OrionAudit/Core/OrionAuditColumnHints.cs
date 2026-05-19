namespace Moongazing.OrionAudit;

/// <summary>
/// Provider-specific column-type hints for <see cref="AuditLog.Diff"/> and <see cref="AuditLog.Snapshot"/>.
/// Passed to <c>ApplyOrionAuditConfigurations</c> at model-build time. Choosing the right hint
/// matters on Postgres (<c>jsonb</c> is indexable) and on SQL Server (default <c>nvarchar</c> is
/// 450 chars, which truncates non-trivial diffs).
/// </summary>
public enum OrionAuditColumnHints
{
    /// <summary>
    /// Let EF Core pick the column type. Works on every provider but may truncate large diffs on
    /// providers whose default string column has a small length (e.g. SQL Server <c>nvarchar(450)</c>).
    /// </summary>
    Auto = 0,

    /// <summary>Map <c>Diff</c> and <c>Snapshot</c> to <c>nvarchar(max)</c> (SQL Server).</summary>
    SqlServerNvarcharMax = 1,

    /// <summary>Map <c>Diff</c> and <c>Snapshot</c> to <c>jsonb</c> (PostgreSQL via Npgsql).</summary>
    PostgresJsonb = 2,

    /// <summary>Map <c>Diff</c> and <c>Snapshot</c> to <c>TEXT</c> (SQLite; this is the default for strings, kept for clarity).</summary>
    SqliteText = 3,
}
