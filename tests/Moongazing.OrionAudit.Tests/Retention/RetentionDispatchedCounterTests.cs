namespace Moongazing.OrionAudit.Tests.Retention;

using System.Diagnostics.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Retention;
using Xunit;

public sealed class RetentionDispatchedCounterTests : IAsyncLifetime
{
    private sealed class Db : DbContext
    {
        public Db(DbContextOptions<Db> options) : base(options) { }
        public DbSet<AuditLog> Logs => Set<AuditLog>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
    }

    private SqliteConnection connection = default!;
    private ServiceProvider services = default!;

    public async ValueTask InitializeAsync()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var collection = new ServiceCollection();
        collection.AddDbContext<Db>(o => o.UseSqlite(connection));
        services = collection.BuildServiceProvider();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        await connection.DisposeAsync();
    }

    private AuditRetentionHostedService<Db> NewSvc(RetentionPolicy policy)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(),
            policy,
            new RetentionSweepOptions(),
            TimeProvider.System,
            NullLogger<AuditRetentionHostedService<Db>>.Instance);

    [Theory]
    [InlineData("retain_for")]
    [InlineData("retain_count")]
    [InlineData("per_tenant")]
    [InlineData("per_entity_type")]
    [InlineData("none")]
    public async Task Dispatched_counter_emits_with_the_expected_policy_tag(string expected)
    {
        var samples = new System.Collections.Generic.List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.retention.dispatched")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var t in tags)
            {
                if (t.Key == "policy" && t.Value is string s)
                {
                    lock (samples) { samples.Add(s); }
                }
            }
        });
        listener.Start();

        RetentionPolicy policy = expected switch
        {
            "retain_for" => RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
            "retain_count" => RetentionPolicy.RetainCount(rows: 100),
            "per_tenant" => RetentionPolicy.PerTenant(
                byTenantId: new Dictionary<string, RetentionPolicy>
                {
                    ["tenant-a"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
                },
                fallback: RetentionPolicy.None),
            "per_entity_type" => RetentionPolicy.PerEntityType(
                byEntityType: new Dictionary<string, RetentionPolicy>
                {
                    ["Demo.User"] = RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
                },
                fallback: RetentionPolicy.None),
            _ => RetentionPolicy.None,
        };

        var sut = NewSvc(policy);
        await sut.SweepOnceAsync(CancellationToken.None);

        lock (samples) { Assert.Contains(expected, samples); }
    }
}
