using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.IntegrationTests;

public class AuditImportTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    private sealed class ImportDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditCaptureQueueEntry> Queue => Set<AuditCaptureQueueEntry>();
        public ImportDb(DbContextOptions<ImportDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    private sealed class AsyncImportDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<AuditCaptureQueueEntry> Queue => Set<AuditCaptureQueueEntry>();
        public AsyncImportDb(DbContextOptions<AsyncImportDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    private sealed class ParitySyncDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public ParitySyncDb(DbContextOptions<ParitySyncDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    private sealed class ParityImportDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public ParityImportDb(DbContextOptions<ParityImportDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    private sealed class RetryImportDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public RetryImportDb(DbContextOptions<RetryImportDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    /// <summary>Fails the n-th <c>SaveChanges</c> it sees, then steps aside once disarmed.</summary>
    private sealed class FailOnNthSaveInterceptor : SaveChangesInterceptor
    {
        private readonly int failOn;
        private int calls;
        public FailOnNthSaveInterceptor(int failOn) => this.failOn = failOn;
        public bool Disarmed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Disarmed && ++calls == failOn)
            {
                throw new InvalidOperationException("flush boom");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static async Task<(ServiceProvider sp, SqliteConnection conn)> BuildAsync<TDb>(
        Action<OrionAuditOptions> configure,
        IInterceptor? extraInterceptor = null) where TDb : DbContext
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<TDb>(configure);
        services.AddSingleton(conn);
        services.AddDbContext<TDb>((sp, o) =>
        {
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp);
            if (extraInterceptor is not null)
            {
                o.AddInterceptors(extraInterceptor);
            }
        });
        var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TDb>().Database.EnsureCreatedAsync();
        }
        return (sp, conn);
    }

    [Fact]
    public async Task SaveAsync_Writes_RowPerRecord_With_NonEmptyDiff()
    {
        var (sp, conn) = await BuildAsync<ImportDb>(o => o.Audit<Note>());
        await using var _c = conn;
        await using var _s = sp;

        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ImportDb>();

        var id = Guid.NewGuid();
        var importer = ctx.CreateAuditImport(o => o.ImportBatch = "legacy-1");
        importer.Add<Note>(e => e.Key(id).Action(AuditAction.Inserted).After(new Note { Id = id, Body = "v1" }).At(DateTime.UtcNow));
        importer.Add<Note>(e => e.Key(id).Action(AuditAction.Updated).Before(new Note { Id = id, Body = "v1" }).After(new Note { Id = id, Body = "v2" }).At(DateTime.UtcNow.AddSeconds(1)));
        var result = await importer.SaveAsync();

        Assert.Equal(2, result.Written);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.DeadLettered);

        var logs = await ctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.StartsWith("import:legacy-1", logs[0].CorrelationId);
        Assert.NotEqual("[]", logs[1].Diff);
    }

    [Fact]
    public async Task SaveAsync_Twice_With_SameBatch_IsIdempotent()
    {
        var (sp, conn) = await BuildAsync<ImportDb>(o => o.Audit<Note>());
        await using var _c = conn;
        await using var _s = sp;

        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ImportDb>();

        var id = Guid.NewGuid();
        var importer1 = ctx.CreateAuditImport(o => o.ImportBatch = "legacy-2");
        importer1.Add<Note>(e => e.Key(id).Action(AuditAction.Inserted).After(new Note { Id = id, Body = "x" }).SourceId(1));
        Assert.Equal(1, (await importer1.SaveAsync()).Written);

        var importer2 = ctx.CreateAuditImport(o => o.ImportBatch = "legacy-2");
        importer2.Add<Note>(e => e.Key(id).Action(AuditAction.Inserted).After(new Note { Id = id, Body = "x" }).SourceId(1));
        var r2 = await importer2.SaveAsync();
        Assert.Equal(0, r2.Written);
        Assert.Equal(1, r2.Skipped);

        Assert.Equal(1, await ctx.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_Retried_After_PartialFlush_Writes_The_Records_That_Never_Landed()
    {
        // Two batches of two. The second flush throws, so batch #1 is committed and batch #2 is not.
        var flushFailure = new FailOnNthSaveInterceptor(failOn: 2);
        var (sp, conn) = await BuildAsync<RetryImportDb>(o => o.Audit<Note>(), flushFailure);
        await using var _c = conn;
        await using var _s = sp;

        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<RetryImportDb>();

        var importer = ctx.CreateAuditImport(o =>
        {
            o.ImportBatch = "legacy-retry";
            o.BatchSize = 2;
        });
        // No SourceId: every one of these records carries the same `import:legacy-retry`
        // correlation, which is what used to make the retry mistake all of them for rows batch #1
        // had already written.
        for (var i = 0; i < 4; i++)
        {
            var id = Guid.NewGuid();
            importer.Add<Note>(e => e
                .Key(id)
                .Action(AuditAction.Inserted)
                .After(new Note { Id = id, Body = $"v{i}" }));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.SaveAsync());
        Assert.Equal(2, await ctx.AuditLogs.CountAsync());

        flushFailure.Disarmed = true;
        var retry = await importer.SaveAsync();

        // The two records the failed flush never wrote must land now, not be waved through as
        // Skipped rows that are "already present" when nothing of the sort is in the table.
        Assert.Equal(2, retry.Written);
        Assert.Equal(0, retry.Skipped);
        Assert.Equal(0, retry.DeadLettered);
        Assert.Equal(4, await ctx.AuditLogs.CountAsync());
        Assert.Equal(4, (await ctx.AuditLogs.Select(a => a.EntityId).ToListAsync()).Distinct().Count());
    }

    [Fact]
    public async Task SaveAsync_WithoutImportBatch_Throws()
    {
        var (sp, conn) = await BuildAsync<ImportDb>(o => o.Audit<Note>());
        await using var _c = conn;
        await using var _s = sp;
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ImportDb>();

        var importer = ctx.CreateAuditImport();   // no ImportBatch
        importer.Add<Note>(e => e.Key(Guid.NewGuid()).Action(AuditAction.Inserted).After(new Note { Body = "x" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.SaveAsync());
    }

    [Fact]
    public async Task SaveAsync_Bypasses_CaptureQueue_When_AsyncMode_On()
    {
        var (sp, conn) = await BuildAsync<AsyncImportDb>(o => o.Audit<Note>().UseAsyncCapture());
        await using var _c = conn;
        await using var _s = sp;

        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AsyncImportDb>();

        var importer = ctx.CreateAuditImport(o => o.ImportBatch = "legacy-3");
        importer.Add<Note>(e => e.Key(Guid.NewGuid()).Action(AuditAction.Inserted).After(new Note { Body = "x" }));
        var result = await importer.SaveAsync();

        Assert.Equal(1, result.Written);
        Assert.Equal(1, await ctx.AuditLogs.CountAsync());
        Assert.Equal(0, await ctx.Queue.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_Diff_IsByteEqual_With_SyncCapture()
    {
        var id = Guid.NewGuid();

        // Sync: real interceptor capture.
        var (syncSp, syncConn) = await BuildAsync<ParitySyncDb>(o => o.Audit<Note>());
        await using var _c1 = syncConn;
        await using var _s1 = syncSp;
        await using (var scope = syncSp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ParitySyncDb>();
            ctx.Notes.Add(new Note { Id = id, Body = "v1" });
            await ctx.SaveChangesAsync();
        }
        await using (var scope = syncSp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ParitySyncDb>();
            var n = await ctx.Notes.SingleAsync();
            n.Body = "v2";
            await ctx.SaveChangesAsync();
        }
        string syncUpdateDiff;
        await using (var scope = syncSp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ParitySyncDb>();
            syncUpdateDiff = (await ctx.AuditLogs.SingleAsync(a => a.Action == AuditAction.Updated)).Diff;
        }

        // Import: same Update via the importer.
        var (impSp, impConn) = await BuildAsync<ParityImportDb>(o => o.Audit<Note>());
        await using var _c2 = impConn;
        await using var _s2 = impSp;
        await using var impScope = impSp.CreateAsyncScope();
        var impCtx = impScope.ServiceProvider.GetRequiredService<ParityImportDb>();
        var importer = impCtx.CreateAuditImport(o => o.ImportBatch = "parity");
        importer.Add<Note>(e => e
            .Key(id)
            .Action(AuditAction.Updated)
            .Before(new Note { Id = id, Body = "v1" })
            .After(new Note { Id = id, Body = "v2" }));
        await importer.SaveAsync();
        var importUpdateDiff = (await impCtx.AuditLogs.SingleAsync(a => a.Action == AuditAction.Updated)).Diff;

        Assert.Equal(syncUpdateDiff, importUpdateDiff);
    }
}
