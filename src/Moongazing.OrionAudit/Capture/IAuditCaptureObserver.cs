namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// v0.7.26 consumer-supplied observer invoked when the interceptor captures audited
/// entities during <c>SaveChangesAsync</c>. Useful for application-side metrics,
/// security alerting (e.g. "N rows of a sensitive entity were modified in one save"),
/// or feeding a separate change-tracking pipeline without coupling that logic to the
/// load-bearing capture path.
/// </summary>
/// <remarks>
/// <para>
/// Fires synchronously inside the interceptor AFTER the queue rows / audit rows are
/// staged and the telemetry is recorded, but BEFORE the base SaveChanges proceeds.
/// A throwing observer does NOT abort the save - observer exceptions are caught and
/// swallowed so an observability-side fault cannot break the consumer's transaction.
/// </para>
/// <para>
/// No observer is registered by default. Consumers wire one via
/// <c>services.AddSingleton&lt;IAuditCaptureObserver, MyObserver&gt;()</c>. The default
/// <see cref="NullAuditCaptureObserver"/> and <see langword="null"/> are both treated
/// as 'no observer'.
/// </para>
/// </remarks>
public interface IAuditCaptureObserver
{
    /// <summary>Notify the observer that a save captured audited entities.</summary>
    /// <param name="auditedEntityCount">Number of audited entities staged in this save.</param>
    /// <param name="isAsyncCapture">True when async staging-capture is active (lightweight queue rows); false for inline AuditLog rows.</param>
    void OnCaptured(int auditedEntityCount, bool isAsyncCapture);
}

/// <summary>Default no-op observer used when no consumer-registered observer is present.</summary>
public sealed class NullAuditCaptureObserver : IAuditCaptureObserver
{
    /// <inheritdoc />
    public void OnCaptured(int auditedEntityCount, bool isAsyncCapture)
    {
        // no-op
    }
}
