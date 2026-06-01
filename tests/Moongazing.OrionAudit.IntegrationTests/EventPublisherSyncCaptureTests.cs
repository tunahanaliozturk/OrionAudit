using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Publishing;

namespace Moongazing.OrionAudit.IntegrationTests;

public class EventPublisherSyncCaptureTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    private sealed class SyncDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public SyncDb(DbContextOptions<SyncDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private sealed class RecordingPublisher : IAuditEventPublisher
    {
        public readonly ConcurrentBag<IReadOnlyList<AuditLogEvent>> Calls = new();
        public ValueTask PublishAsync(IReadOnlyList<AuditLogEvent> events, CancellationToken cancellationToken)
        {
            Calls.Add(events);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ExplodingPublisher : IAuditEventPublisher
    {
        public ValueTask PublishAsync(IReadOnlyList<AuditLogEvent> events, CancellationToken cancellationToken)
            => throw new InvalidOperationException("downstream is angry");
    }

    private static async Task<(ServiceProvider sp, SqliteConnection conn)> BuildAsync<TPublisher>()
        where TPublisher : class, IAuditEventPublisher
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<SyncDb>(o => o
            .Audit<Note>()
            .UseEventPublisher<TPublisher>());
        services.AddSingleton(connection);
        services.AddDbContext<SyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SyncDb>().Database.EnsureCreatedAsync();
        }
        return (sp, connection);
    }

    [Fact]
    public async Task Publisher_IsCalledOncePerSave_WithMatchingEventCount()
    {
        var (sp, conn) = await BuildAsync<RecordingPublisher>();
        await using var _conn = conn;
        await using var _sp = sp;
        var publisher = (RecordingPublisher)sp.GetRequiredService<IAuditEventPublisher>();

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<SyncDb>();
            ctx.Notes.Add(new Note { Body = "a" });
            ctx.Notes.Add(new Note { Body = "b" });
            await ctx.SaveChangesAsync();
        }
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<SyncDb>();
            ctx.Notes.Add(new Note { Body = "c" });
            await ctx.SaveChangesAsync();
        }

        Assert.Equal(2, publisher.Calls.Count);
        var sizes = publisher.Calls.Select(c => c.Count).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { 1, 2 }, sizes);
        var allEvents = publisher.Calls.SelectMany(c => c).ToList();
        Assert.All(allEvents, e =>
        {
            Assert.Equal("Inserted", e.Action);
            Assert.Equal(typeof(Note).AssemblyQualifiedName, e.EntityType);
            Assert.NotEqual(Guid.Empty, e.AuditLogId);
        });
    }

    [Fact]
    public async Task PublisherException_AbortsConsumerTransaction()
    {
        var (sp, conn) = await BuildAsync<ExplodingPublisher>();
        await using var _conn = conn;
        await using var _sp = sp;

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<SyncDb>();
            ctx.Notes.Add(new Note { Body = "should-be-rolled-back" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());
        }

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<SyncDb>();
        Assert.Equal(0, await vctx.Notes.CountAsync());
        Assert.Equal(0, await vctx.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task NoPublisher_LeavesBehaviourUnchanged()
    {
        // No UseEventPublisher / UseChannelEventPublisher call — falls back to Null publisher.
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<SyncDb>(o => o.Audit<Note>());
        services.AddSingleton(connection);
        services.AddDbContext<SyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SyncDb>().Database.EnsureCreatedAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<SyncDb>();
            ctx.Notes.Add(new Note { Body = "x" });
            await ctx.SaveChangesAsync();
        }

        await using var verify = sp.CreateAsyncScope();
        var vctx = verify.ServiceProvider.GetRequiredService<SyncDb>();
        Assert.Equal(1, await vctx.AuditLogs.CountAsync());
        Assert.IsType<NullAuditEventPublisher>(sp.GetRequiredService<IAuditEventPublisher>());
        await connection.DisposeAsync();
    }
}
