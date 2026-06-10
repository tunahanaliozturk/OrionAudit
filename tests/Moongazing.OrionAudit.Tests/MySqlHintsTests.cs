namespace Moongazing.OrionAudit.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.MySql;
using Xunit;

public sealed class MySqlHintsTests
{
    // EF Core caches the model per DbContext CLR type. Distinct subclasses ensure the
    // useLongText / default-json variants each build their own model and aren't served from
    // the other variant's cached model.
    private sealed class JsonDbContext : DbContext
    {
        public JsonDbContext(DbContextOptions<JsonDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyOrionAuditMySqlConfigurations(this, useLongText: false);
    }

    private sealed class LongTextDbContext : DbContext
    {
        public LongTextDbContext(DbContextOptions<LongTextDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyOrionAuditMySqlConfigurations(this, useLongText: true);
    }

    private static JsonDbContext CreateJsonContext()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<JsonDbContext>(_ => { });
        var sp = services.BuildServiceProvider();
        var opts = new DbContextOptionsBuilder<JsonDbContext>()
            .UseInMemoryDatabase("mysql-json-" + Guid.NewGuid().ToString("N"))
            .UseApplicationServiceProvider(sp)
            .Options;
        return new JsonDbContext(opts);
    }

    private static LongTextDbContext CreateLongTextContext()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<LongTextDbContext>(_ => { });
        var sp = services.BuildServiceProvider();
        var opts = new DbContextOptionsBuilder<LongTextDbContext>()
            .UseInMemoryDatabase("mysql-longtext-" + Guid.NewGuid().ToString("N"))
            .UseApplicationServiceProvider(sp)
            .Options;
        return new LongTextDbContext(opts);
    }

    [Fact]
    public void Default_overload_uses_MySqlJson_for_diff_column()
    {
        using var db = CreateJsonContext();
        var diff = db.Model.FindEntityType(typeof(AuditLog))!.FindProperty(nameof(AuditLog.Diff))!;
        Assert.Equal("json", diff.FindAnnotation("Relational:ColumnType")?.Value as string, ignoreCase: true);
    }

    [Fact]
    public void Default_overload_uses_MySqlJson_for_snapshot_column()
    {
        using var db = CreateJsonContext();
        var snap = db.Model.FindEntityType(typeof(AuditLog))!.FindProperty(nameof(AuditLog.Snapshot))!;
        Assert.Equal("json", snap.FindAnnotation("Relational:ColumnType")?.Value as string, ignoreCase: true);
    }

    [Fact]
    public void UseLongText_overload_uses_LONGTEXT_for_diff_and_snapshot()
    {
        using var db = CreateLongTextContext();
        var et = db.Model.FindEntityType(typeof(AuditLog))!;
        Assert.Equal("longtext", et.FindProperty(nameof(AuditLog.Diff))!.FindAnnotation("Relational:ColumnType")?.Value as string, ignoreCase: true);
        Assert.Equal("longtext", et.FindProperty(nameof(AuditLog.Snapshot))!.FindAnnotation("Relational:ColumnType")?.Value as string, ignoreCase: true);
    }

    [Fact]
    public void OrionAuditColumnHints_enum_includes_MySql_variants()
    {
        // Ship-time guarantee: the enum must contain both MySql values so consumers using
        // the raw OrionAuditColumnHints overload do not need the MySql package.
        Assert.Equal((int)OrionAuditColumnHints.MySqlJson, 4);
        Assert.Equal((int)OrionAuditColumnHints.MySqlLongText, 5);
    }
}
