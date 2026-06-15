namespace Moongazing.OrionAudit.Tests;

using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Read;
using Xunit;

public class ReconstructEventsReplayedHistogramTests
{
    private const string InstrumentName = "orionaudit.reconstruct.events_replayed";

    [Auditable]
    public sealed class Order
    {
        public System.Guid Id { get; set; } = System.Guid.NewGuid();
        public string Status { get; set; } = "New";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().HasKey(o => o.Id);
            modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
        }
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AuditConfigurationBuilder().Audit<Order>().Build());
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(System.Guid.NewGuid().ToString())
             .AddInterceptors(new AuditSaveChangesInterceptor(sp)));
        services.AddScoped<IAuditReconstructor>(sp => new AuditReconstructor(sp.GetRequiredService<TestContext>()));
        return services.BuildServiceProvider();
    }

    private static System.Collections.Generic.List<int> Capture(System.Func<System.Threading.Tasks.Task> act)
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit" && instrument.Name == InstrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        act().GetAwaiter().GetResult();

        lock (samples) { return new System.Collections.Generic.List<int>(samples); }
    }

    [Fact]
    public void RecordReconstructEventsReplayed_records_the_value_and_clamps_negatives()
    {
        var positive = Capture(() => { OrionAuditTelemetry.RecordReconstructEventsReplayed(5); return System.Threading.Tasks.Task.CompletedTask; });
        Assert.Contains(5, positive);

        var negative = Capture(() => { OrionAuditTelemetry.RecordReconstructEventsReplayed(-3); return System.Threading.Tasks.Task.CompletedTask; });
        Assert.Contains(0, negative);
        Assert.DoesNotContain(-3, negative);
    }

    [Fact]
    public void A_reconstruction_with_history_emits_a_positive_replayed_count()
    {
        // Assert >= 1 (not an exact count) so the process-global OrionAudit meter receiving
        // emissions from parallel reconstruction tests cannot flake this; the point is that a real
        // reconstruction over an entity with history records a positive replayed-row sample.
        var samples = Capture(async () =>
        {
            await using var sp = Build();
            await using var scope = sp.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();

            var order = new Order { Status = "Pending" };
            ctx.Orders.Add(order);
            await ctx.SaveChangesAsync();
            order.Status = "Shipped";
            await ctx.SaveChangesAsync();

            await reconstructor.ReconstructAsync<Order>(order.Id.ToString(), System.DateTime.UtcNow.AddMinutes(1));
        });

        Assert.Contains(samples, s => s >= 1);
    }
}
