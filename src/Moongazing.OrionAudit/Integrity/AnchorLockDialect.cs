using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Builds the provider-specific SQL that takes a pessimistic row lock on one stream's
/// <see cref="AuditChainAnchor"/> before the chain writer reads its head. Returns <see langword="null"/>
/// for providers that do not need (or do not support) an explicit lock - notably SQLite, which already
/// serializes write transactions database-wide.
/// </summary>
internal sealed class AnchorLockDialect
{
    /// <summary>The parameterised lock statement (positional <c>{0}/{1}/{2}</c> placeholders).</summary>
    public string Sql { get; private init; } = default!;

    /// <summary>Builds the positional parameter array for a given stream key.</summary>
    public Func<AuditHashChainStamper.ChainKey, object[]> Parameters { get; private init; } = default!;

    /// <summary>
    /// Returns the lock dialect for <paramref name="context"/>'s provider, or <see langword="null"/>
    /// when no explicit lock is issued (SQLite / in-memory / unrecognised providers).
    /// </summary>
    public static AnchorLockDialect? For(DbContext context)
    {
        var table = ResolveAnchorTableName(context);
        var providerName = context.Database.ProviderName ?? string.Empty;

        // SQL Server: a locking SELECT with UPDLOCK + HOLDLOCK takes (and holds, to commit) a lock on
        // the matching key range, which serialises same-stream appends and also blocks a second
        // genesis insert for a not-yet-existent anchor.
        if (providerName.Contains("SqlServer", StringComparison.Ordinal))
        {
            return new AnchorLockDialect
            {
                Sql = $"SELECT TOP 1 1 FROM {QuoteSqlServer(table)} WITH (UPDLOCK, HOLDLOCK) "
                    + "WHERE [EntityType] = {0} AND [EntityId] = {1} AND [TenantId] = {2}",
                Parameters = KeyParameters,
            };
        }

        // PostgreSQL: SELECT ... FOR UPDATE takes a row lock held until commit. ANSI double-quoted
        // identifiers are the Postgres default.
        if (providerName.Contains("Npgsql", StringComparison.Ordinal))
        {
            return new AnchorLockDialect
            {
                Sql = $"SELECT 1 FROM {QuoteAnsi(table)} "
                    + "WHERE \"EntityType\" = {0} AND \"EntityId\" = {1} AND \"TenantId\" = {2} "
                    + "LIMIT 1 FOR UPDATE",
                Parameters = KeyParameters,
            };
        }

        // MySQL / MariaDB: same FOR UPDATE semantics, but identifiers are backtick-quoted (ANSI
        // double quotes require ANSI_QUOTES mode, which is off by default), so quote with backticks.
        if (providerName.Contains("Pomelo", StringComparison.Ordinal)
            || providerName.Contains("MySql", StringComparison.Ordinal))
        {
            return new AnchorLockDialect
            {
                Sql = $"SELECT 1 FROM {QuoteMySql(table)} "
                    + "WHERE `EntityType` = {0} AND `EntityId` = {1} AND `TenantId` = {2} "
                    + "LIMIT 1 FOR UPDATE",
                Parameters = KeyParameters,
            };
        }

        // SQLite serialises writes DB-wide; the EF in-memory provider has no SQL surface. No explicit
        // lock - the surrounding transaction (SQLite) or the anchor PK (genesis races) is sufficient.
        return null;
    }

    private static object[] KeyParameters(AuditHashChainStamper.ChainKey key)
        => new object[] { key.EntityType, key.EntityId, key.TenantId };

    // The mapped table name of AuditChainAnchor (honours a custom table name). Falls back to the
    // default if the model has not mapped it (defensive; ApplyOrionAuditConfigurations always maps it).
    private static string ResolveAnchorTableName(DbContext context)
    {
        var entityType = context.Model.FindEntityType(typeof(AuditChainAnchor));
        return entityType?.GetTableName() ?? AuditChainAnchorEntityTypeConfiguration.DefaultTableName;
    }

    // ANSI double-quote quoting for Postgres identifiers (table name only; the column names are
    // OrionAudit-owned literals embedded in the SQL above).
    private static string QuoteAnsi(string table) => "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // Backtick quoting for the MySQL/MariaDB family (ANSI double quotes need ANSI_QUOTES mode).
    private static string QuoteMySql(string table) => "`" + table.Replace("`", "``", StringComparison.Ordinal) + "`";

    private static string QuoteSqlServer(string table) => "[" + table.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
