using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.IntegrationTests;

public class AsyncCaptureInterceptorTests
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
    public async Task AsyncMode_WritesQueueRow_NotAuditLog()
    {
        var (sp, conn) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            ctx.Notes.Add(new Note { Body = "hello" });
            await ctx.SaveChangesAsync();
        }

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<AsyncDb>();
        Assert.Equal(0, await vctx.AuditLogs.CountAsync());
        var queued = await vctx.Queue.SingleAsync();
        Assert.Equal(AuditAction.Inserted, queued.Action);
        Assert.Equal(typeof(Note).AssemblyQualifiedName, queued.EntityType);
        Assert.Contains("hello", queued.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsyncMode_QueueRow_RolledBackWithTheDataChange()
    {
        var (sp, conn) = await BuildAsync();
        await using var _conn = conn;
        await using var _sp = sp;

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            await using var tx = await ctx.Database.BeginTransactionAsync();
            ctx.Notes.Add(new Note { Body = "doomed" });
            await ctx.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<AsyncDb>();
        Assert.Equal(0, await vctx.Queue.CountAsync());
        Assert.Equal(0, await vctx.Notes.CountAsync());
    }
}
