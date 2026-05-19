using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Retention;

namespace Moongazing.OrionAudit.IntegrationTests;

public class RetentionTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    private sealed class RetentionDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public RetentionDb(DbContextOptions<RetentionDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private sealed class FrozenClock : TimeProvider
    {
        private DateTimeOffset now;
        public FrozenClock(DateTimeOffset start) => now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now = now.Add(by);
    }

    private static async Task<(ServiceProvider sp, SqliteConnection conn, FrozenClock clock)> BuildAsync(
        Action<OrionAuditOptions> configure,
        DateTimeOffset clockStart)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var clock = new FrozenClock(clockStart);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLogging();
        services.AddOrionAudit<RetentionDb>(configure);
        services.AddSingleton(connection);
        services.AddDbContext<RetentionDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RetentionDb>().Database.EnsureCreatedAsync();
        }
        return (sp, connection, clock);
    }

    [Fact]
    public async Task RetainFor_DeletesRowsOlderThanCutoff()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (sp, connection, clock) = await BuildAsync(
            o => o.Audit<Note>().RetainFor(TimeSpan.FromDays(7)),
            start);
        await using var _conn = connection;
        await using var _sp = sp;

        // Seed 5 audit rows over the past 14 days.
        for (var dayOffset = -14; dayOffset <= -1; dayOffset += 3)
        {
            clock.Advance(TimeSpan.FromDays(3));
            await using var scope = sp.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<RetentionDb>();
            ctx.Notes.Add(new Note { Body = $"day{dayOffset}" });
            await ctx.SaveChangesAsync();
        }

        // Set clock to "today" (start + 14 days). Anything older than 7 days should be swept.
        clock.Advance(TimeSpan.FromDays(0));
        var sweep = ActivatorUtilities.CreateInstance<AuditRetentionHostedService<RetentionDb>>(sp);
        var deleted = await sweep.SweepOnceAsync();
        Assert.True(deleted >= 1, $"expected at least 1 row to be deleted, got {deleted}");

        await using var verifyScope = sp.CreateAsyncScope();
        var verifyCtx = verifyScope.ServiceProvider.GetRequiredService<RetentionDb>();
        var remaining = await verifyCtx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        var cutoff = clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(7);
        Assert.All(remaining, r => Assert.True(r.OccurredOnUtc >= cutoff,
            $"row OccurredOnUtc={r.OccurredOnUtc:O} predates cutoff={cutoff:O}"));
    }

    [Fact]
    public async Task RetainCount_KeepsLatestNPerEntity()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (sp, connection, clock) = await BuildAsync(
            o => o.Audit<Note>().RetainCount(3),
            start);
        await using var _conn = connection;
        await using var _sp = sp;

        // 5 updates on one entity → 1 Inserted + 5 Updated = 6 audit rows; RetainCount(3) keeps the latest 3.
        Note? n = null;
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<RetentionDb>();
            n = new Note { Body = "init" };
            ctx.Notes.Add(n);
            await ctx.SaveChangesAsync();
        }
        for (var i = 1; i <= 5; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await using var scope = sp.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<RetentionDb>();
            var fresh = await ctx.Notes.FirstAsync();
            fresh.Body = $"v{i}";
            await ctx.SaveChangesAsync();
        }

        var sweep = ActivatorUtilities.CreateInstance<AuditRetentionHostedService<RetentionDb>>(sp);
        var deleted = await sweep.SweepOnceAsync();
        Assert.Equal(3, deleted);   // 6 rows total, keep 3, delete 3

        await using var verifyScope = sp.CreateAsyncScope();
        var verifyCtx = verifyScope.ServiceProvider.GetRequiredService<RetentionDb>();
        var remaining = await verifyCtx.AuditLogs.Where(a => a.EntityId == n!.Id.ToString()).CountAsync();
        Assert.Equal(3, remaining);
    }

    [Fact]
    public async Task NonePolicy_SkipsSweepEntirely()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (sp, connection, _) = await BuildAsync(o => o.Audit<Note>(), start);   // no retention configured
        await using var _conn = connection;
        await using var _sp = sp;

        // The sweep service is not registered, so RetentionPolicy is None in DI:
        var policy = sp.GetRequiredService<RetentionPolicy>();
        Assert.Same(RetentionPolicy.None, policy);
    }
}
