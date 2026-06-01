namespace Moongazing.OrionAudit.Publishing;

/// <summary>
/// First-class extension point for fanning <see cref="AuditLog"/> rows out to downstream pipelines
/// (message broker, search indexer, webhook) without writing a custom <c>SaveChangesInterceptor</c>.
/// <para>
/// Implementations are invoked from inside the capture transaction (sync-capture) or the
/// dispatcher transaction (async-capture). A publisher exception will propagate and abort that
/// transaction. Do not perform unbounded I/O directly: enqueue, hand off, or use a bounded
/// in-process buffer (see <see cref="ChannelAuditEventPublisher"/>).
/// </para>
/// </summary>
public interface IAuditEventPublisher
{
    /// <summary>
    /// Publishes the supplied audit events. Implementations should not mutate the list.
    /// The list is non-null and non-empty when called from the capture path.
    /// </summary>
    /// <param name="events">The events captured for the current save (or dispatch batch).</param>
    /// <param name="cancellationToken">Cancellation token plumbed from the originating call.</param>
    ValueTask PublishAsync(IReadOnlyList<AuditLogEvent> events, CancellationToken cancellationToken);
}
