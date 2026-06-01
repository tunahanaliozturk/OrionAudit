using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Publishing;

namespace Moongazing.OrionAudit.IntegrationTests;

public class EventPublisherAsyncCaptureTests
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

    private sealed class RecordingPublisher : IAuditEventPublisher
    {
        public readonly ConcurrentBag<AuditLogEvent> Seen = new();
        public int CallCount;
        public ValueTask PublishAsync(IReadOnlyList<AuditLogEvent> events, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            foreach (var e in events)
            {
                Seen.Add(e);
            }
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task AsyncMode_Publisher_FiresFromDispatcher_NotFromConsumerSave()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<AsyncDb>(o => o
            .Audit<Note>()
            .UseAsyncCapture()
            .UseEventPublisher<RecordingPublisher>());
        services.AddSingleton(connection);
        services.AddDbContext<AsyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AsyncDb>().Database.EnsureCreatedAsync();
        }

        var publisher = (RecordingPublisher)sp.GetRequiredService<IAuditEventPublisher>();

        // SaveChanges in async mode writes the queue row; the publisher must NOT have fired yet.
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            ctx.Notes.Add(new Note { Body = "v1" });
            await ctx.SaveChangesAsync();
        }
        Assert.Equal(0, Volatile.Read(ref publisher.CallCount));

        // Dispatcher runs, materialises the AuditLog row, AND fires the publisher.
        var dispatcher = sp.GetRequiredService<IAuditDispatcher>();
        var processed = await dispatcher.FlushPendingAsync();
        Assert.Equal(1, processed);

        Assert.True(Volatile.Read(ref publisher.CallCount) >= 1, "publisher should have fired from dispatcher");
        var evt = publisher.Seen.Single();
        Assert.Equal("Inserted", evt.Action);
        Assert.Equal(typeof(Note).AssemblyQualifiedName, evt.EntityType);

        await connection.DisposeAsync();
    }
}
