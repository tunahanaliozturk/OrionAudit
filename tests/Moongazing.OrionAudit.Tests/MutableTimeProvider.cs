namespace Moongazing.OrionAudit.Tests;

/// <summary>
/// Hand-wound clock for tests that need a deterministic "now" (and a deterministic elapse)
/// instead of a <c>Task.Delay</c> against the wall clock. Shared so the snapshot, retention and
/// cursor suites all step the same seam the interceptor and hosted services resolve from DI.
/// </summary>
internal sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan delta) => now = now.Add(delta);
}
