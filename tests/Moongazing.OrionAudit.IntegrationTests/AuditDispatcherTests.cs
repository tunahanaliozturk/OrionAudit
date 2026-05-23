using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.IntegrationTests;

public class AuditDispatcherTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    private sealed class AsyncDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditCaptureQueueEntry> Queue => Set<AuditCaptureQueueEntry>();
        public AsyncDb(DbContextOptions<AsyncDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private static async Task<(ServiceProvider sp, SqliteConnection conn)> BuildAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<AsyncDb>(o => o.Audit<Note>().UseAsyncCapture());
        services.AddSingleton(connection);
        services.AddDbContext<AsyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AsyncDb>().Database.EnsureCreatedAsync();
        }
        return (sp, connection);
    }

    [Fact]
    public async Task DispatchOnce_TurnsQueueRowsIntoAuditLogRows()
    {
        var (sp, conn) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            ctx.Notes.Add(new Note { Body = "v1" });
            await ctx.SaveChangesAsync();
        }
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            var fresh = await ctx.Notes.FirstAsync();
            fresh.Body = "v2";
            await ctx.SaveChangesAsync();
        }

        var dispatcher = sp.GetRequiredService<IAuditDispatcher>();
        var processed = await dispatcher.FlushPendingAsync();
        Assert.Equal(2, processed);

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<AsyncDb>();
        Assert.Equal(0, await vctx.Queue.CountAsync());
        var logs = await vctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.Equal(AuditAction.Inserted, logs[0].Action);
        Assert.Equal(AuditAction.Updated, logs[1].Action);
        Assert.NotEqual("[]", logs[1].Diff);   // the update produced a real diff
    }

    [Fact]
    public async Task GetQueueDepth_CountsUndispatchedRows()
    {
        var (sp, conn) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            ctx.Notes.Add(new Note { Body = "a" });
            ctx.Notes.Add(new Note { Body = "b" });
            await ctx.SaveChangesAsync();
        }

        var dispatcher = sp.GetRequiredService<IAuditDispatcher>();
        Assert.Equal(2, await dispatcher.GetQueueDepthAsync());
        await dispatcher.FlushPendingAsync();
        Assert.Equal(0, await dispatcher.GetQueueDepthAsync());
    }
}
