namespace Moongazing.OrionAudit.IntegrationTests;

using System.Diagnostics.Metrics;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Xunit;

public class CaptureEntriesPerSaveHistogramTests
{
    // The orionaudit.capture.entries_per_save histogram is emitted from the process-global
    // OrionAudit Meter with no distinguishing tag, so a plain MeterListener cannot tell which
    // test produced a sample. Under xUnit's parallel execution, an audited save in ANOTHER test
    // class would otherwise land in this class's listener and break the strict assertions
    // (especially the "no emission" case). We isolate by ExecutionContext: each test marks its
    // own save with an AsyncLocal flag that flows across the awaited SaveChangesAsync into the
    // interceptor's Record call, and the listener only records measurements taken while THIS
    // test's flag is set.
    private static readonly AsyncLocal<bool> CapturingOwnSave = new();
    [Auditable]
    public sealed class Item
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
    }

    private sealed class InlineDb : DbContext
    {
        public DbSet<Item> Items => Set<Item>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public InlineDb(DbContextOptions<InlineDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Item>().HasKey(i => i.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    [Fact]
    public async Task Records_one_histogram_sample_per_SaveChangesAsync_with_audited_count()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.capture.entries_per_save")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            if (!CapturingOwnSave.Value) { return; }
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<InlineDb>(o => o.Audit<Item>());
        services.AddSingleton(connection);
        services.AddDbContext<InlineDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<InlineDb>().Database.EnsureCreatedAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<InlineDb>();
            ctx.Items.AddRange(
                new Item { Name = "a" },
                new Item { Name = "b" },
                new Item { Name = "c" });
            CapturingOwnSave.Value = true;
            try
            {
                await ctx.SaveChangesAsync();
            }
            finally
            {
                CapturingOwnSave.Value = false;
            }
        }

        lock (samples)
        {
            Assert.NotEmpty(samples);
            Assert.Contains(3, samples);
        }
    }

    [Fact]
    public async Task Records_zero_when_no_audited_entries_are_present()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.capture.entries_per_save")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            if (!CapturingOwnSave.Value) { return; }
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<InlineDb>(o => o.Audit<Item>());
        services.AddSingleton(connection);
        services.AddDbContext<InlineDb>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<InlineDb>().Database.EnsureCreatedAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<InlineDb>();
            // No audited entries; a no-op save still completes.
            CapturingOwnSave.Value = true;
            try
            {
                await ctx.SaveChangesAsync();
            }
            finally
            {
                CapturingOwnSave.Value = false;
            }
        }

        lock (samples)
        {
            // The interceptor short-circuits when there is nothing to audit, so no
            // emission is expected. Contract: zero-row saves do NOT pollute the histogram
            // tail with 0 samples. The AsyncLocal gate ensures a concurrent test's audited
            // save cannot leak a sample into this assertion.
            Assert.Empty(samples);
        }
    }
}
