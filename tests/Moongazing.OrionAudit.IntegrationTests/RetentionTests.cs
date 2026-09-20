using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    /// <summary>Records the SQL the sweep actually issues, so its ORDER BY can be asserted.</summary>
    private sealed class SqlRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task<(ServiceProvider sp, SqliteConnection conn, FrozenClock clock)> BuildAsync(
        Action<OrionAuditOptions> configure,
        DateTimeOffset clockStart,
        SqlRecorder? recorder = null)
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
        {
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp);
            if (recorder is not null)
            {
                o.AddInterceptors(recorder);
            }
        });
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

    /// <summary>
    /// Rows of one stream sharing a timestamp are routine - one timestamp is computed per SaveChanges
    /// and stamped on every row of that save, and column precision truncates further. Ordering the
    /// sweep by timestamp alone lets the provider break those ties however it likes, so the batch can
    /// take an INTERIOR row of a stream instead of a contiguous head. The hash-chain repair can only
    /// re-anchor at the oldest survivor; it cannot close a hole in the middle. So retention has to
    /// select in the chain's canonical (OccurredOnUtc, Id) order.
    /// </summary>
    [Fact]
    public async Task RetainCount_PrunesTheCanonicalHead_WhenTimestampsTie()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var recorder = new SqlRecorder();
        var (sp, connection, clock) = await BuildAsync(o => o.Audit<Note>().RetainCount(3), start, recorder);
        await using var _conn = connection;
        await using var _sp = sp;

        // Eight rows of one stream on ONE timestamp, with ids chosen so the canonical order is known.
        // They are inserted in a scrambled order so that storage order carries no information about
        // canonical order: with no tie-break the provider is free to keep whichever three it reaches
        // first (SQLite walks the timeline index, so that is storage order, forwards or backwards),
        // and neither end of that walk is the canonical newest three. The rows it deletes then land
        // scattered through the stream instead of at its head - which is what the chain repair cannot
        // survive.
        var sharedTimestamp = clock.GetUtcNow().UtcDateTime;
        var scrambled = new[] { 3, 7, 1, 8, 5, 2, 6, 4 };
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<RetentionDb>();
            foreach (var ordinal in scrambled)
            {
                ctx.AuditLogs.Add(new AuditLog
                {
                    Id = CanonicalId(ordinal),
                    EntityType = "T",
                    EntityId = "e1",
                    Diff = "[]",
                    OccurredOnUtc = sharedTimestamp,
                });
            }
            await ctx.SaveChangesAsync();
        }

        var sweep = ActivatorUtilities.CreateInstance<AuditRetentionHostedService<RetentionDb>>(sp);
        Assert.Equal(5, await sweep.SweepOnceAsync());   // 8 rows, keep 3

        await using var verifyScope = sp.CreateAsyncScope();
        var verifyCtx = verifyScope.ServiceProvider.GetRequiredService<RetentionDb>();
        var survivors = await verifyCtx.AuditLogs.AsNoTracking()
            .OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id)
            .Select(a => a.Id)
            .ToListAsync();

        // The survivors must be the TAIL of the canonical order, i.e. a contiguous head was pruned.
        // The survivors must be the TAIL of the canonical order, i.e. a contiguous head was pruned.
        Assert.Equal(new[] { CanonicalId(6), CanonicalId(7), CanonicalId(8) }, survivors);

        // ...and that must hold because the sweep ASKED for canonical order, not because this
        // provider's planner happened to return the ties that way. Without the tie-break the result
        // is unspecified - SQLite's plan here is stable, SQL Server's uniqueidentifier collation and
        // any parallel or index-driven plan are not - so pin the contract on the SQL itself.
        var orderedSelects = recorder.Commands
            .Where(c => c.Contains("OrionAudit_Log", StringComparison.Ordinal)
                        && c.Contains("ORDER BY", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(orderedSelects);
        Assert.All(orderedSelects, sql =>
        {
            var orderBy = sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..];
            Assert.Contains("OccurredOnUtc", orderBy, StringComparison.Ordinal);
            Assert.Contains("\"Id\"", orderBy, StringComparison.Ordinal);
        });
    }

    private static Guid CanonicalId(int ordinal)
        => new($"{ordinal:D8}-0000-0000-0000-000000000000");

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
