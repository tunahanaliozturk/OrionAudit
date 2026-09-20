using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Integrity;
using Moongazing.OrionAudit.Publishing;

namespace Moongazing.OrionAudit.IntegrationTests;

/// <summary>
/// Regression cover for the synchronous <c>DbContext.SaveChanges()</c> capture path. The
/// interceptor originally wired only <c>SavingChangesAsync</c>, so every caller that used the
/// blocking overload wrote zero audit rows and raised nothing to say so.
/// </summary>
public class SyncSaveChangesCaptureTests
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

        // Yields before recording so the capture pipeline genuinely suspends: this is what
        // proves the sync override drains the async tail rather than dropping it.
        public async ValueTask PublishAsync(IReadOnlyList<AuditLogEvent> events, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Calls.Add(events);
        }
    }

    // Fixed 32-byte key (base64), matching HashChainCaptureTests.
    private const string ChainKeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    private static async Task<(ServiceProvider Sp, SqliteConnection Conn)> BuildAsync(
        bool withPublisher = false,
        bool withHashChain = false)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<SyncDb>(o =>
        {
            o.Audit<Note>();
            if (withPublisher)
            {
                o.UseEventPublisher<RecordingPublisher>();
            }
            if (withHashChain)
            {
                o.UseHashChain(h => h.UseKey(1, ChainKeyBase64));
            }
        });
        services.AddSingleton(connection);
        services.AddDbContext<SyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SyncDb>().Database.EnsureCreatedAsync();
        }
        return (provider, connection);
    }

    [Fact]
    public async Task SaveChanges_Sync_InsertUpdateDelete_ProducesThreeAuditRows()
    {
        var (provider, connection) = await BuildAsync();
        await using var _p = provider;
        await using var _c = connection;

        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<SyncDb>();
            var note = new Note { Body = "first" };
            ctx.Notes.Add(note);
            ctx.SaveChanges();

            note.Body = "second";
            ctx.SaveChanges();

            ctx.Notes.Remove(note);
            ctx.SaveChanges();
        }

        await using var read = provider.CreateAsyncScope();
        var logs = await read.ServiceProvider.GetRequiredService<SyncDb>().AuditLogs.ToListAsync();

        // The AuditLog PK is a Guid, so assert on the captured set rather than on row order.
        Assert.Equal(3, logs.Count);
        Assert.Equal(
            new[] { AuditAction.Inserted, AuditAction.Updated, AuditAction.Deleted },
            logs.Select(l => l.Action).Order().ToArray());
        Assert.NotNull(Assert.Single(logs, l => l.Action == AuditAction.Deleted).Snapshot);
    }

    [Fact]
    public async Task SaveChanges_Sync_StampsCorrelationFromAuditScope()
    {
        var (provider, connection) = await BuildAsync();
        await using var _p = provider;
        await using var _c = connection;

        const string jobId = "nightly-sync-run";
        using (var scope = provider.CreateScope())
        using (AuditScope.Push(jobId))
        {
            var ctx = scope.ServiceProvider.GetRequiredService<SyncDb>();
            ctx.Notes.Add(new Note { Body = "batch" });
            ctx.SaveChanges();
        }

        await using var read = provider.CreateAsyncScope();
        var entry = Assert.Single(await read.ServiceProvider.GetRequiredService<SyncDb>().AuditLogs.ToListAsync());
        Assert.Equal(jobId, entry.CorrelationId);
    }

    [Fact]
    public async Task SaveChanges_Sync_DrainsTheAsyncPublisherTail()
    {
        var (provider, connection) = await BuildAsync(withPublisher: true);
        await using var _p = provider;
        await using var _c = connection;

        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<SyncDb>();
            ctx.Notes.Add(new Note { Body = "published" });
            ctx.SaveChanges();
        }

        var publisher = (RecordingPublisher)provider.GetRequiredService<IAuditEventPublisher>();
        var call = Assert.Single(publisher.Calls);
        Assert.Single(call);
        Assert.Equal(nameof(AuditAction.Inserted), call[0].Action);
    }

    [Fact]
    public async Task SaveChanges_Sync_DrainsTheAsyncHashChainTail()
    {
        // The hash chain's anchor lock/read is the one leg of the pipeline that genuinely
        // suspends on database I/O. This is the sync override blocking on real async work.
        var (provider, connection) = await BuildAsync(withHashChain: true);
        await using var _p = provider;
        await using var _c = connection;

        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<SyncDb>();
            ctx.Notes.Add(new Note { Body = "chained" });
            ctx.SaveChanges();
        }

        await using var read = provider.CreateAsyncScope();
        var ctx2 = read.ServiceProvider.GetRequiredService<SyncDb>();
        var entry = Assert.Single(await ctx2.AuditLogs.ToListAsync());
        Assert.False(string.IsNullOrEmpty(entry.EntryHash));

        var verifier = read.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>();
        var result = await verifier.VerifyChainAsync(
            AuditChainVerificationRequest.ForEntity(entry.EntityType, entry.EntityId));
        Assert.True(result.IsValid);
        Assert.Equal(1, result.VerifiedRowCount);
    }

    /// <summary>
    /// A single-threaded context, like WPF's, WinForms' or legacy ASP.NET's: work posted to it runs
    /// only when the owning thread pumps the queue. Nothing here ever pumps, so anything posted is
    /// a deadlock made visible.
    /// </summary>
    private sealed class NeverPumpedSynchronizationContext : SynchronizationContext
    {
        public int Posted;

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posted);

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    [Fact]
    public void SaveChanges_Sync_DoesNotDeadlockOnAPublisherThatCapturesTheCallersContext()
    {
        // RecordingPublisher awaits Task.Yield() without ConfigureAwait(false) - consumer code is
        // entitled to. Task.Yield posts the continuation to whatever SynchronizationContext is
        // current at the await, so if the sync override blocks on the caller's context thread, the
        // publisher's continuation is queued behind the very thread waiting for it and the save
        // never returns. Clearing the ambient context for the duration of the capture is what
        // makes that unreachable.
        var context = new NeverPumpedSynchronizationContext();
        Exception? failure = null;

        // On a thread of its own so a regression fails the run on the join timeout instead of
        // hanging the whole suite.
        var caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var (provider, connection) = BuildAsync(withPublisher: true).GetAwaiter().GetResult();
                using (provider)
                using (connection)
                using (var scope = provider.CreateScope())
                {
                    var ctx = scope.ServiceProvider.GetRequiredService<SyncDb>();
                    ctx.Notes.Add(new Note { Body = "posted-back" });
                    ctx.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        { IsBackground = true };

        caller.Start();

        Assert.True(
            caller.Join(TimeSpan.FromSeconds(30)),
            "SaveChanges() deadlocked: the publisher's continuation was posted back to the blocked caller thread.");
        Assert.Null(failure);
        Assert.Equal(0, Volatile.Read(ref context.Posted));
    }
}
