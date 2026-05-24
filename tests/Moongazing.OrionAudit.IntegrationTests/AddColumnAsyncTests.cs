using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.IntegrationTests;

public class AddColumnAsyncTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    private sealed class AsyncQueueDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditCaptureQueueEntry> Queue => Set<AuditCaptureQueueEntry>();
        public AsyncQueueDb(DbContextOptions<AsyncQueueDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    private sealed class AsyncDispatchDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditCaptureQueueEntry> Queue => Set<AuditCaptureQueueEntry>();
        public AsyncDispatchDb(DbContextOptions<AsyncDispatchDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    [Fact]
    public async Task AsyncMode_Interceptor_Serialises_CustomColumns_Into_QueueRow()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _c = conn;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<AsyncQueueDb>(o => o
            .Audit<Note>()
            .UseAsyncCapture()
            .AddColumn<int>("Length", ctx => ((Note)ctx.Entity).Body.Length)
            .AddColumn<string>("Source", _ => "test"));
        services.AddSingleton(conn);
        services.AddDbContext<AsyncQueueDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AsyncQueueDb>().Database.EnsureCreatedAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncQueueDb>();
            ctx.Notes.Add(new Note { Body = "hi!" });
            await ctx.SaveChangesAsync();
        }

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<AsyncQueueDb>();
        var queued = await vctx.Queue.SingleAsync();
        Assert.NotNull(queued.CustomColumnsJson);
        Assert.Contains("\"Length\":3", queued.CustomColumnsJson!, StringComparison.Ordinal);
        Assert.Contains("\"Source\":\"test\"", queued.CustomColumnsJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatcher_Applies_CustomColumns_From_QueueJson_To_AuditLog()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _c = conn;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<AsyncDispatchDb>(o => o
            .Audit<Note>()
            .UseAsyncCapture()
            .AddColumn<int>("Length", ctx => ((Note)ctx.Entity).Body.Length)
            .AddColumn<string>("Source", _ => "test"));
        services.AddSingleton(conn);
        services.AddDbContext<AsyncDispatchDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AsyncDispatchDb>().Database.EnsureCreatedAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDispatchDb>();
            ctx.Notes.Add(new Note { Body = "hi!" });
            await ctx.SaveChangesAsync();
        }

        await sp.GetRequiredService<IAuditDispatcher>().FlushPendingAsync();

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<AsyncDispatchDb>();
        var row = await vctx.AuditLogs.Select(a => new
        {
            Length = EF.Property<int?>(a, "Length"),
            Source = EF.Property<string?>(a, "Source"),
        }).SingleAsync();
        Assert.Equal(3, row.Length);
        Assert.Equal("test", row.Source);
        Assert.Equal(0, await vctx.Queue.CountAsync());
    }
}
