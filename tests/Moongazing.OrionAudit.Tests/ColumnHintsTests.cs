using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.Tests;

public class ColumnHintsTests
{
    // EF Core caches the model per DbContext CLR type, so each hint variant gets its own
    // context subclass to bust the cache.
    private abstract class HintContext : DbContext
    {
        protected HintContext(DbContextOptions options) : base(options) { }
        protected abstract OrionAuditColumnHints Hints { get; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyOrionAuditConfigurations(columnHints: Hints);
        }
    }

    private sealed class AutoContext : HintContext
    {
        public AutoContext(DbContextOptions<AutoContext> options) : base(options) { }
        protected override OrionAuditColumnHints Hints => OrionAuditColumnHints.Auto;
    }

    private sealed class SqlServerContext : HintContext
    {
        public SqlServerContext(DbContextOptions<SqlServerContext> options) : base(options) { }
        protected override OrionAuditColumnHints Hints => OrionAuditColumnHints.SqlServerNvarcharMax;
    }

    private sealed class PostgresContext : HintContext
    {
        public PostgresContext(DbContextOptions<PostgresContext> options) : base(options) { }
        protected override OrionAuditColumnHints Hints => OrionAuditColumnHints.PostgresJsonb;
    }

    private sealed class SqliteContext : HintContext
    {
        public SqliteContext(DbContextOptions<SqliteContext> options) : base(options) { }
        protected override OrionAuditColumnHints Hints => OrionAuditColumnHints.SqliteText;
    }

    private static (string? diff, string? snapshot) GetColumnTypes<TContext>(Func<DbContextOptions<TContext>, TContext> factory)
        where TContext : DbContext
    {
        var opts = new DbContextOptionsBuilder<TContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var ctx = factory(opts);
        var entity = ctx.Model.FindEntityType(typeof(AuditLog))!;
        return (
            entity.FindProperty(nameof(AuditLog.Diff))!.FindAnnotation("Relational:ColumnType")?.Value as string,
            entity.FindProperty(nameof(AuditLog.Snapshot))!.FindAnnotation("Relational:ColumnType")?.Value as string);
    }

    [Fact]
    public void Auto_EmitsNoColumnTypeAnnotation()
    {
        var (diff, snapshot) = GetColumnTypes<AutoContext>(o => new AutoContext(o));
        Assert.Null(diff);
        Assert.Null(snapshot);
    }

    [Fact]
    public void SqlServerNvarcharMax_MapsBothColumns()
    {
        var (diff, snapshot) = GetColumnTypes<SqlServerContext>(o => new SqlServerContext(o));
        Assert.Equal("nvarchar(max)", diff);
        Assert.Equal("nvarchar(max)", snapshot);
    }

    [Fact]
    public void PostgresJsonb_MapsBothColumns()
    {
        var (diff, snapshot) = GetColumnTypes<PostgresContext>(o => new PostgresContext(o));
        Assert.Equal("jsonb", diff);
        Assert.Equal("jsonb", snapshot);
    }

    [Fact]
    public void SqliteText_MapsBothColumns()
    {
        var (diff, snapshot) = GetColumnTypes<SqliteContext>(o => new SqliteContext(o));
        Assert.Equal("TEXT", diff);
        Assert.Equal("TEXT", snapshot);
    }
}
