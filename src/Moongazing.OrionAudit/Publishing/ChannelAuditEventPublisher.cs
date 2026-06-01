using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Moongazing.OrionAudit.Publishing;

/// <summary>
/// In-process default <see cref="IAuditEventPublisher"/>. Buffers events in a bounded
/// <see cref="Channel{T}"/> and invokes a user-supplied delegate per event on a single dedicated
/// reader task.
/// <para>
/// This default is intentionally toy-grade. It exists so consumers without a broker get a working
/// publisher out of the box - suitable for monoliths and tests. Production deployments that need
/// at-least-once delivery to a real downstream pipeline should write their own
/// <see cref="IAuditEventPublisher"/> against RabbitMQ, Azure Service Bus, Kafka, or whatever the
/// consumer already runs. The publisher hook is the extension point; OrionAudit deliberately does
/// not ship broker bindings.
/// </para>
/// <para>
/// Semantics:
/// <list type="bullet">
/// <item>Writes block when the channel is full (<see cref="BoundedChannelFullMode.Wait"/>) so
/// capture applies natural backpressure to the consumer's <c>SaveChanges</c> call.</item>
/// <item>Reader exceptions are logged and counted via <c>orionaudit.events.dropped</c>; the
/// reader continues so a single bad event does not stop the publisher.</item>
/// <item><see cref="DisposeAsync"/> completes the writer and awaits the reader up to the
/// configured drain timeout so in-flight events have a chance to flush at shutdown.</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class ChannelAuditEventPublisher : IAuditEventPublisher, IAsyncDisposable
{
    [LoggerMessage(EventId = 12, Level = LogLevel.Error,
        Message = "OrionAudit ChannelAuditEventPublisher handler failed; event dropped.")]
    private partial void LogPublishFailed(Exception ex);

    private readonly Channel<AuditLogEvent> channel;
    private readonly Func<AuditLogEvent, CancellationToken, ValueTask> handler;
    private readonly TimeSpan drainTimeout;
    private readonly ILogger<ChannelAuditEventPublisher> logger;
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly Task readerTask;
    private int disposed;

    /// <summary>Initializes a publisher with explicit options.</summary>
    /// <param name="handler">The per-event delegate the reader invokes.</param>
    /// <param name="options">Channel capacity and drain timeout.</param>
    /// <param name="logger">Logger for reader-side failures (optional).</param>
    public ChannelAuditEventPublisher(
        Func<AuditLogEvent, CancellationToken, ValueTask> handler,
        ChannelAuditEventPublisherOptions options,
        ILogger<ChannelAuditEventPublisher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Capacity, "Capacity must be >= 1.");
        }
        if (options.DrainTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.DrainTimeout, "DrainTimeout must be >= TimeSpan.Zero.");
        }

        this.handler = handler;
        this.drainTimeout = options.DrainTimeout;
        this.logger = logger ?? NullLogger<ChannelAuditEventPublisher>.Instance;
        this.channel = Channel.CreateBounded<AuditLogEvent>(new BoundedChannelOptions(options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        this.readerTask = Task.Run(ReadLoopAsync);
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(IReadOnlyList<AuditLogEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        // Single channel write per event keeps the reader's exception-isolation contract simple:
        // a malformed event does not poison its neighbours.
        for (var i = 0; i < events.Count; i++)
        {
            await channel.Writer.WriteAsync(events[i], cancellationToken).ConfigureAwait(false);
        }
        OrionAuditTelemetry.EventsPublished.Add(events.Count);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(shutdownCts.Token).ConfigureAwait(false))
            {
                using var activity = OrionAuditTelemetry.ActivitySource.StartActivity(
                    "OrionAudit.Publish", ActivityKind.Producer);
                try
                {
                    await handler(evt, shutdownCts.Token).ConfigureAwait(false);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
#pragma warning disable CA1031 // a single bad event must not stop the reader
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    OrionAuditTelemetry.EventsDropped.Add(1);
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    LogPublishFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown via cts cancellation. Expected.
        }
    }

    /// <summary>
    /// Completes the writer and awaits the reader up to <c>DrainTimeout</c>. Any events still
    /// sitting in the channel after the timeout are abandoned and counted as dropped.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        channel.Writer.TryComplete();

        try
        {
            await readerTask.WaitAsync(drainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Force-cancel the reader and account for whatever was left behind.
            var leftover = 0;
            while (channel.Reader.TryRead(out _))
            {
                leftover++;
            }
            if (leftover > 0)
            {
                OrionAuditTelemetry.EventsDropped.Add(leftover);
            }
            shutdownCts.Cancel();
            try
            {
                await readerTask.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // we are tearing down; do not let reader teardown rethrow
            catch
#pragma warning restore CA1031
            {
            }
        }

        shutdownCts.Dispose();
    }
}
