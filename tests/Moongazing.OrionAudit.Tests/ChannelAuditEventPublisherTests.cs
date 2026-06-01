using System.Diagnostics.Metrics;
using Moongazing.OrionAudit.Publishing;

namespace Moongazing.OrionAudit.Tests;

public class ChannelAuditEventPublisherTests
{
    private static AuditLogEvent SampleEvent(string id = "k") =>
        new(Guid.NewGuid(), "T", id, "Inserted", DateTimeOffset.UtcNow, null, null, null, null);

    [Fact]
    public async Task PublishAsync_DeliversEvents_ToHandler()
    {
        var seen = new List<string>();
        var done = new TaskCompletionSource();
        await using var publisher = new ChannelAuditEventPublisher(
            (evt, _) =>
            {
                lock (seen)
                {
                    seen.Add(evt.EntityKey);
                    if (seen.Count == 3)
                    {
                        done.TrySetResult();
                    }
                }
                return ValueTask.CompletedTask;
            },
            new ChannelAuditEventPublisherOptions());

        await publisher.PublishAsync(new[] { SampleEvent("a"), SampleEvent("b"), SampleEvent("c") }, CancellationToken.None);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (seen)
        {
            Assert.Equal(new[] { "a", "b", "c" }, seen);
        }
    }

    [Fact]
    public async Task PublishAsync_AppliesBackpressure_WhenChannelFull()
    {
        // Capacity=1, reader blocked on the first event. After:
        //   - event #1 leaves the buffer, reader holds it and blocks on `release`
        //   - event #2 enters the buffer (count=1, at capacity)
        //   - event #3 must wait for the reader to drain #1 before it can land
        // We assert event #3's PublishAsync is still pending after 200ms.
        var release = new TaskCompletionSource();
        await using var publisher = new ChannelAuditEventPublisher(
            async (_, ct) =>
            {
                await release.Task.WaitAsync(ct);
            },
            new ChannelAuditEventPublisherOptions { Capacity = 1 });

        await publisher.PublishAsync(new[] { SampleEvent("1") }, CancellationToken.None);
        // Give the reader a beat to pick up #1 and start blocking on release.
        await Task.Delay(50);
        await publisher.PublishAsync(new[] { SampleEvent("2") }, CancellationToken.None);

        var blocked = publisher.PublishAsync(new[] { SampleEvent("3") }, CancellationToken.None).AsTask();
        var delay = Task.Delay(200);
        var winner = await Task.WhenAny(blocked, delay);
        Assert.Same(delay, winner);

        release.SetResult();
        await blocked;
    }

    [Fact]
    public async Task ReaderException_IncrementsDroppedCounter_AndKeepsReading()
    {
        var droppedDelta = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == OrionAuditTelemetry.MeterName && instr.Name == "orionaudit.events.dropped")
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, m, _, _) => Interlocked.Add(ref droppedDelta, m));
        listener.Start();

        var second = new TaskCompletionSource();
        await using (var publisher = new ChannelAuditEventPublisher(
            (evt, _) =>
            {
                if (evt.EntityKey == "boom")
                {
                    throw new InvalidOperationException("explode");
                }
                second.TrySetResult();
                return ValueTask.CompletedTask;
            },
            new ChannelAuditEventPublisherOptions()))
        {
            await publisher.PublishAsync(new[] { SampleEvent("boom"), SampleEvent("ok") }, CancellationToken.None);
            await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(Interlocked.Read(ref droppedDelta) >= 1, "expected the boom event to count as dropped");
    }

    [Fact]
    public async Task DisposeAsync_Drains_PendingEvents_WithinTimeout()
    {
        var processed = 0;
        var publisher = new ChannelAuditEventPublisher(
            (_, _) =>
            {
                Interlocked.Increment(ref processed);
                return ValueTask.CompletedTask;
            },
            new ChannelAuditEventPublisherOptions { DrainTimeout = TimeSpan.FromSeconds(5) });

        await publisher.PublishAsync(new[] { SampleEvent(), SampleEvent(), SampleEvent() }, CancellationToken.None);
        await publisher.DisposeAsync();

        Assert.Equal(3, Interlocked.CompareExchange(ref processed, 0, 0));
    }

    [Fact]
    public void Ctor_RejectsInvalidOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChannelAuditEventPublisher((_, _) => ValueTask.CompletedTask,
                new ChannelAuditEventPublisherOptions { Capacity = 0 }));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChannelAuditEventPublisher((_, _) => ValueTask.CompletedTask,
                new ChannelAuditEventPublisherOptions { DrainTimeout = TimeSpan.FromMilliseconds(-1) }));
    }

    [Fact]
    public async Task PublishAsync_EmptyList_IsNoOp()
    {
        var calls = 0;
        await using var publisher = new ChannelAuditEventPublisher(
            (_, _) => { Interlocked.Increment(ref calls); return ValueTask.CompletedTask; },
            new ChannelAuditEventPublisherOptions());
        await publisher.PublishAsync(Array.Empty<AuditLogEvent>(), CancellationToken.None);
        Assert.Equal(0, calls);
    }
}
