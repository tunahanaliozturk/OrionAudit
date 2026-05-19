namespace Moongazing.OrionAudit;

/// <summary>
/// Ambient correlation-id scope flowed via <see cref="AsyncLocal{T}"/>. Pushed values are
/// preferred over <c>Activity.Current?.Id</c> by the interceptor when stamping
/// <see cref="AuditLog.CorrelationId"/>. Useful for background jobs, console runners, and
/// other contexts where no W3C trace is in flight.
/// </summary>
public static class AuditScope
{
    private static readonly AsyncLocal<string?> currentId = new();

    /// <summary>The correlation id active on the current async-flow, or <c>null</c>.</summary>
    public static string? Current => currentId.Value;

    /// <summary>
    /// Pushes a new ambient correlation id; disposing the returned scope restores the previous
    /// value. Nests safely.
    /// </summary>
    public static IDisposable Push(string correlationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(correlationId);
        var previous = currentId.Value;
        currentId.Value = correlationId;
        return new PopOnDispose(previous);
    }

    private sealed class PopOnDispose : IDisposable
    {
        private readonly string? previous;
        public PopOnDispose(string? previous) => this.previous = previous;
        public void Dispose() => currentId.Value = previous;
    }
}
