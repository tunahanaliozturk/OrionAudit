using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.IntegrationTests;

public class AddColumnSyncTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    // EF Core caches IModel per DbContext-type. Each scenario in this file uses its own
    // DbContext subclass so the cached model reflects the AddColumn registrations under test.

    private sealed class ValuesDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public ValuesDb(DbContextOptions<ValuesDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    private sealed class ProviderThrowsDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public ProviderThrowsDb(DbContextOptions<ProviderThrowsDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    [Fact]
    public async Task AddColumn_Value_Lands_On_AuditLog_ShadowProperty()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _c = conn;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<ValuesDb>(o => o
            .Audit<Note>()
            .AddColumn<string>("Source", ctx => ctx.Action == AuditAction.Inserted ? "import" : "app")
            .AddColumn<int>("Length", ctx => ((Note)ctx.Entity).Body.Length));
        services.AddSingleton(conn);
        services.AddDbContext<ValuesDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ValuesDb>().Database.EnsureCreatedAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ValuesDb>();
            ctx.Notes.Add(new Note { Body = "hello" });
            await ctx.SaveChangesAsync();
        }

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<ValuesDb>();
        var row = await vctx.AuditLogs.Select(a => new
        {
            Source = EF.Property<string?>(a, "Source"),
            Length = EF.Property<int?>(a, "Length"),
        }).SingleAsync();
        Assert.Equal("import", row.Source);
        Assert.Equal(5, row.Length);
    }

    [Fact]
    public async Task AddColumn_ProviderThrows_RowStillWritten_WithErrorAnnotation_AndNullColumn()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _c = conn;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<ProviderThrowsDb>(o => o
            .Audit<Note>()
            .AddColumn<int>("Boom", _ => throw new InvalidOperationException("nope")));
        services.AddSingleton(conn);
        services.AddDbContext<ProviderThrowsDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ProviderThrowsDb>().Database.EnsureCreatedAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ProviderThrowsDb>();
            ctx.Notes.Add(new Note { Body = "x" });
            await ctx.SaveChangesAsync();
        }

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<ProviderThrowsDb>();
        var row = await vctx.AuditLogs.Select(a => new
        {
            a.Error,
            Boom = EF.Property<int?>(a, "Boom"),
        }).SingleAsync();
        Assert.NotNull(row.Error);
        Assert.Contains("Boom", row.Error!, StringComparison.Ordinal);
        Assert.Null(row.Boom);
    }
}
