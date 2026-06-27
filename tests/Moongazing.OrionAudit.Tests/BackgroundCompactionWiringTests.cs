using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Store;

namespace Moongazing.OrionAudit.Tests;

/// <summary>
/// Wiring and validation coverage for the v0.10.0 background-compaction opt-in and the always-on
/// NDJSON exporter registration. No database is needed: these assert the DI surface and the
/// configuration guards.
/// </summary>
public class BackgroundCompactionWiringTests
{
    [Auditable]
    public sealed class Thing
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }

    private sealed class ThingDb : DbContext
    {
        public DbSet<Thing> Things => Set<Thing>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public ThingDb(DbContextOptions<ThingDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().HasKey(t => t.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    [Fact]
    public void CompactInBackground_RegistersHostedServiceAndOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<ThingDb>(o => { o.Audit<Thing>(); o.CompactInBackground(); });
        services.AddDbContext<ThingDb>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<CompactionSweepOptions>());
        Assert.Contains(sp.GetServices<IHostedService>(), s => s is AuditCompactionHostedService<ThingDb>);
    }

    [Fact]
    public void WithoutCompactInBackground_NoHostedServiceAndNoOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<ThingDb>(o => o.Audit<Thing>());
        services.AddDbContext<ThingDb>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        using var sp = services.BuildServiceProvider();

        Assert.Null(sp.GetService<CompactionSweepOptions>());
        Assert.DoesNotContain(sp.GetServices<IHostedService>(), s => s is AuditCompactionHostedService<ThingDb>);
    }

    [Fact]
    public void Exporter_IsRegistered_EvenWithoutBackgroundCompaction()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<ThingDb>(o => o.Audit<Thing>());
        services.AddDbContext<ThingDb>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<AuditHistoryExporter>());
    }

    [Theory]
    [InlineData(0, 1)]    // MinRowsBeforeCompaction (1) < RetainTail (0) + 2
    [InlineData(10, 11)]  // MinRowsBeforeCompaction (11) < RetainTail (10) + 2
    [InlineData(3, 4)]    // MinRowsBeforeCompaction (4) < RetainTail (3) + 2
    public void CompactInBackground_RejectsThresholdBelowRetainTailPlusTwo(int retainTail, int minRows)
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() =>
            services.AddOrionAudit<ThingDb>(o => o.Audit<Thing>().CompactInBackground(c =>
            {
                c.RetainTail = retainTail;
                c.MinRowsBeforeCompaction = minRows;
            })));
    }

    [Fact]
    public void CompactInBackground_RejectsNonPositiveInterval()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() =>
            services.AddOrionAudit<ThingDb>(o => o.Audit<Thing>().CompactInBackground(c =>
                c.SweepInterval = TimeSpan.Zero)));
    }

    [Fact]
    public void CompactInBackground_RejectsZeroMaxStreams()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() =>
            services.AddOrionAudit<ThingDb>(o => o.Audit<Thing>().CompactInBackground(c =>
                c.MaxStreamsPerSweep = 0)));
    }
}
