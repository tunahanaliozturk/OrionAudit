using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class OrionAuditTelemetryTests
{
    [Auditable]
    public sealed class Order
    {
        public Guid Id { get; set; } = Guid.NewGuid();
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
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    [Fact]
    public async Task SaveChanges_EmitsCaptureActivity()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == OrionAuditTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Order>());
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        var activity = captured.FirstOrDefault(a =>
            a.OperationName == "OrionAudit.Capture"
            && a.Status == ActivityStatusCode.Ok
            && a.GetTagItem("orionaudit.entry_count") is int count
            && count == 1);
        Assert.NotNull(activity);
    }

    [Fact]
    public void Meter_Exposes_DispatchInstruments()
    {
        // Force the static type initializer so every internal instrument field is constructed
        // before the MeterListener.Start() callback enumerates published instruments. Touching
        // OrionAuditTelemetry.MeterName alone does not trigger initialization — const fields
        // are compiled inline.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(OrionAuditTelemetry).TypeHandle);

        var names = new List<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OrionAuditTelemetry.MeterName)
            {
                names.Add(instrument.Name);
            }
        };
        listener.Start();

        Assert.Contains("orionaudit.dispatch.rows_processed", names);
        Assert.Contains("orionaudit.dispatch.rows_deadlettered", names);
        Assert.Contains("orionaudit.dispatch.batch.duration", names);
        Assert.Contains("orionaudit.capture.queue_depth", names);
    }
}
