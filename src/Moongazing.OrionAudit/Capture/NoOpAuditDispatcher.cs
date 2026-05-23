namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// The <see cref="IAuditDispatcher"/> registered in synchronous mode. Both members are no-ops
/// so call sites — chiefly test code — can depend on <see cref="IAuditDispatcher"/> without
/// branching on whether async capture is enabled.
/// </summary>
public sealed class NoOpAuditDispatcher : IAuditDispatcher
{
    /// <inheritdoc />
    public Task<int> FlushPendingAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    /// <inheritdoc />
    public Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}
