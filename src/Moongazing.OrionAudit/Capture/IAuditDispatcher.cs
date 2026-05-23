namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// Drains the async capture queue into <see cref="AuditLog"/> rows. Registered only when
/// <c>UseAsyncCapture</c> is configured; in synchronous mode a no-op implementation is
/// registered so call sites can depend on it unconditionally.
/// </summary>
public interface IAuditDispatcher
{
    /// <summary>
    /// Synchronously drains the capture queue to completion (repeatedly dispatching batches
    /// until the queue holds no further dispatchable rows). Returns the number of rows turned
    /// into <see cref="AuditLog"/> rows. A no-op returning 0 in synchronous mode.
    /// </summary>
    Task<int> FlushPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts capture-queue rows still awaiting dispatch (excludes dead-lettered rows).</summary>
    Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default);
}
