using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.IntegrationTests;

public class AuditDispatcherDeadLetterTests
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
        services.AddOrionAudit<AsyncDb>(o => o.Audit<Note>().UseAsyncCapture(q => q.MaxAttempts(2)));
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
    public async Task MalformedQueueRow_IsDeadLettered_AfterMaxAttempts()
    {
        var (sp, conn) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        // Insert a deliberately malformed queue row directly.
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            ctx.Queue.Add(new AuditCaptureQueueEntry
            {
                EntityType = typeof(Note).AssemblyQualifiedName!,
                EntityId = Guid.NewGuid().ToString(),
                Action = AuditAction.Inserted,
                BeforeJson = "{}",
                AfterJson = "this-is-not-json",   // forces JsonNode.Parse to throw
                OccurredOnUtc = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var dispatcher = (AuditDispatcher<AsyncDb>)sp.GetRequiredService<IAuditDispatcher>();

        // MaxAttempts = 2 → two failing cycles dead-letter the row.
        await dispatcher.DispatchOnceAsync();
        await dispatcher.DispatchOnceAsync();

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<AsyncDb>();
        var row = await vctx.Queue.SingleAsync();
        Assert.Equal(2, row.Attempts);
        Assert.NotNull(row.Error);
        Assert.Equal(0, await vctx.AuditLogs.CountAsync());
        Assert.Equal(0, await dispatcher.GetQueueDepthAsync());   // dead-lettered rows excluded
    }

    [Fact]
    public async Task AsyncDispatch_ProducesSameDiff_AsSyncCapture()
    {
        // --- async provider ---
        var (asyncSp, asyncConn) = await BuildAsync();
        await using var _ac = asyncConn;
        await using var _as = asyncSp;
        var fixedId = Guid.NewGuid();
        await ApplyInsertThenUpdate(asyncSp, fixedId);
        await ((IAuditDispatcher)asyncSp.GetRequiredService<IAuditDispatcher>()).FlushPendingAsync();

        // --- sync provider (no UseAsyncCapture) ---
        var syncConn = new SqliteConnection("DataSource=:memory:");
        await syncConn.OpenAsync();
        await using var _sc = syncConn;
        var syncServices = new ServiceCollection();
        syncServices.AddLogging();
        syncServices.AddOrionAudit<AsyncDb>(o => o.Audit<Note>());
        syncServices.AddSingleton(syncConn);
        syncServices.AddDbContext<AsyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var syncSp = syncServices.BuildServiceProvider();
        await using (var scope = syncSp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AsyncDb>().Database.EnsureCreatedAsync();
        }
        await ApplyInsertThenUpdate(syncSp, fixedId);

        // --- compare the Update row's diff ---
        var asyncDiff = await ReadUpdateDiff(asyncSp);
        var syncDiff = await ReadUpdateDiff(syncSp);
        Assert.Equal(syncDiff, asyncDiff);
    }

    private static async Task ApplyInsertThenUpdate(IServiceProvider sp, Guid id)
    {
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            ctx.Notes.Add(new Note { Id = id, Body = "v1" });
            await ctx.SaveChangesAsync();
        }
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            var fresh = await ctx.Notes.FirstAsync();
            fresh.Body = "v2";
            await ctx.SaveChangesAsync();
        }
    }

    private static async Task<string> ReadUpdateDiff(IServiceProvider sp)
    {
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
        var row = await ctx.AuditLogs.SingleAsync(a => a.Action == AuditAction.Updated);
        return row.Diff;
    }
}
