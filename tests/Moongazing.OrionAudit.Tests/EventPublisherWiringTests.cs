using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit.Publishing;

namespace Moongazing.OrionAudit.Tests;

public class EventPublisherWiringTests
{
    private sealed class DummyDb : DbContext
    {
        public DummyDb(DbContextOptions<DummyDb> options) : base(options) { }
    }

    private sealed class MyPublisher : IAuditEventPublisher
    {
        public int Calls;
        public ValueTask PublishAsync(IReadOnlyList<AuditLogEvent> events, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void Default_RegistersNullPublisher()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<DummyDb>(_ => { });
        using var sp = services.BuildServiceProvider();

        var publisher = sp.GetRequiredService<IAuditEventPublisher>();
        Assert.IsType<NullAuditEventPublisher>(publisher);
        Assert.Same(NullAuditEventPublisher.Instance, publisher);
    }

    [Fact]
    public void UseEventPublisher_RegistersTypedSingleton()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<DummyDb>(o => o.UseEventPublisher<MyPublisher>());
        using var sp = services.BuildServiceProvider();

        var first = sp.GetRequiredService<IAuditEventPublisher>();
        var second = sp.GetRequiredService<IAuditEventPublisher>();
        Assert.IsType<MyPublisher>(first);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task UseChannelEventPublisher_RegistersChannelPublisher()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<DummyDb>(o => o.UseChannelEventPublisher(
            (_, _) => ValueTask.CompletedTask,
            opts => opts.Capacity = 16));
        // ChannelAuditEventPublisher is IAsyncDisposable; dispose the container async so it
        // gets the draining DisposeAsync path instead of throwing on sync Dispose.
        var sp = services.BuildServiceProvider();
        await using var _sp = sp;

        var publisher = sp.GetRequiredService<IAuditEventPublisher>();
        Assert.IsType<ChannelAuditEventPublisher>(publisher);
    }
}
