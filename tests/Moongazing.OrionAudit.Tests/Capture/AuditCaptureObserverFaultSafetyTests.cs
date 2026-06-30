using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Configuration;
using Xunit;

namespace Moongazing.OrionAudit.Tests.Capture;

/// <summary>
/// v0.11.1 convergence guard: the interceptor's <see cref="IAuditCaptureObserver"/> invocation now
/// runs through <c>SafeObserverInvoker.Resolve</c> from Orion.Abstractions instead of a bespoke
/// try/catch. These tests pin the behavior that must NOT change: a throwing observer (or one whose
/// resolution throws) cannot abort the audit capture or the consumer's save, the Null/absent
/// observer is a silent no-op, and a well-behaved observer still sees the audited-entity count and
/// the async-capture flag. They exercise the real interceptor end-to-end so they fail if the
/// fault-safe contract regresses for any reason.
/// </summary>
public sealed class AuditCaptureObserverFaultSafetyTests
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
            modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
        }
    }

    private sealed class ThrowingObserver : IAuditCaptureObserver
    {
        public int Calls { get; private set; }
        public void OnCaptured(int auditedEntityCount, bool isAsyncCapture)
        {
            Calls++;
            throw new InvalidOperationException("observer blew up");
        }
    }

    private sealed class RecordingObserver : IAuditCaptureObserver
    {
        public int Count { get; private set; } = -1;
        public bool? IsAsync { get; private set; }
        public int Calls { get; private set; }
        public void OnCaptured(int auditedEntityCount, bool isAsyncCapture)
        {
            Calls++;
            Count = auditedEntityCount;
            IsAsync = isAsyncCapture;
        }
    }

    /// <summary>An observer registration whose construction throws (DI factory faults).</summary>
    private sealed class UnconstructableObserver : IAuditCaptureObserver
    {
        public UnconstructableObserver() => throw new InvalidOperationException("ctor blew up");
        public void OnCaptured(int auditedEntityCount, bool isAsyncCapture) { }
    }

    private static ServiceProvider Build(Action<IServiceCollection>? configureObserver = null)
    {
        var services = new ServiceCollection();
        var cfgBuilder = new AuditConfigurationBuilder();
        cfgBuilder.Audit<Order>();
        services.AddSingleton<IAuditConfiguration>(cfgBuilder.Build());
        services.AddSingleton(TimeProvider.System);
        configureObserver?.Invoke(services);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString())
             .AddInterceptors(new AuditSaveChangesInterceptor(sp)));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Throwing_observer_does_not_abort_capture_or_save()
    {
        var observer = new ThrowingObserver();
        await using var sp = Build(s => s.AddSingleton<IAuditCaptureObserver>(observer));
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var order = new Order { Status = "Pending" };
        ctx.Orders.Add(order);

        // The throwing observer must be swallowed: SaveChangesAsync completes normally.
        await ctx.SaveChangesAsync();

        Assert.Equal(1, observer.Calls);
        // The entity was saved and the audit row was written despite the observer fault.
        Assert.Single(await ctx.Orders.ToListAsync());
        var auditEntry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.Equal(AuditAction.Inserted, auditEntry.Action);
    }

    [Fact]
    public async Task Observer_whose_resolution_throws_does_not_abort_save()
    {
        // Registered as a transient so the DI factory runs (and throws) at GetService time,
        // inside the interceptor's notify path. The resolve-inside-the-guard contract must hold.
        await using var sp = Build(s => s.AddTransient<IAuditCaptureObserver, UnconstructableObserver>());
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        ctx.Orders.Add(new Order { Status = "Pending" });

        await ctx.SaveChangesAsync();

        Assert.Single(await ctx.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Well_behaved_observer_receives_count_and_inline_flag()
    {
        var observer = new RecordingObserver();
        await using var sp = Build(s => s.AddSingleton<IAuditCaptureObserver>(observer));
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        Assert.Equal(1, observer.Calls);
        Assert.Equal(1, observer.Count);
        // Inline (synchronous) capture path: no AsyncCaptureOptions registered.
        Assert.False(observer.IsAsync);
    }

    [Fact]
    public async Task NullAuditCaptureObserver_is_treated_as_no_observer()
    {
        // Registering the Null implementation must behave exactly like registering nothing:
        // the capture path runs to completion and the audit row is written.
        await using var sp = Build(s => s.AddSingleton<IAuditCaptureObserver, NullAuditCaptureObserver>());
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        Assert.Single(await ctx.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task No_registered_observer_is_a_silent_no_op()
    {
        await using var sp = Build();
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        ctx.Orders.Add(new Order { Status = "Pending" });
        await ctx.SaveChangesAsync();

        Assert.Single(await ctx.AuditLogs.ToListAsync());
    }
}
