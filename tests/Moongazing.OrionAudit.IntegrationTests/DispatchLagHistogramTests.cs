namespace Moongazing.OrionAudit.IntegrationTests;

using System.Diagnostics.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Xunit;

public class DispatchLagHistogramTests
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

    [Fact]
    public async Task DispatchLag_records_a_non_negative_value_per_processed_row()
    {
        var samples = new System.Collections.Generic.List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionAudit"
                && instrument.Name == "orionaudit.dispatch.lag")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<AsyncDb>(o => o.Audit<Note>().UseAsyncCapture());
        services.AddSingleton(connection);
        services.AddDbContext<AsyncDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AsyncDb>().Database.EnsureCreatedAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AsyncDb>();
            ctx.Notes.Add(new Note { Body = "first" });
            await ctx.SaveChangesAsync();
        }

        var dispatcher = (AuditDispatcher<AsyncDb>)sp.GetRequiredService<IAuditDispatcher>();
        await dispatcher.DispatchOnceAsync();

        lock (samples)
        {
            Assert.NotEmpty(samples);
            Assert.All(samples, s => Assert.True(s >= 0, "DispatchLag must be non-negative even under clock skew"));
        }
    }
}
