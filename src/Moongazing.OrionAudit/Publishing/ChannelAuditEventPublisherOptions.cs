namespace Moongazing.OrionAudit.Publishing;

/// <summary>
/// Tunables for <see cref="ChannelAuditEventPublisher"/>.
/// </summary>
public sealed class ChannelAuditEventPublisherOptions
{
    /// <summary>
    /// Maximum number of events that may sit in the in-process channel before
    /// <see cref="IAuditEventPublisher.PublishAsync"/> blocks on the writer. Defaults to 4096.
    /// Backpressure is intentional: a stuck reader applies pressure to capture rather than
    /// growing the buffer unboundedly.
    /// </summary>
    public int Capacity { get; set; } = 4096;

    /// <summary>
    /// Maximum time the publisher's <c>DisposeAsync</c> will wait for the background reader to
    /// drain the channel on shutdown. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
