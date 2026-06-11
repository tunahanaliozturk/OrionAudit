namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// Tunables for opt-in async staging-capture. Defaults: poll every 2s, 500 rows per batch,
/// 5 dispatch attempts before dead-lettering, 5-minute claim lease before an abandoned
/// claim is reclaimable.
/// </summary>
public sealed class AsyncCaptureOptions
{
    /// <summary>How often the dispatcher polls the capture queue. Default: 2 seconds.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum queue rows claimed and processed per dispatch cycle. Default: 500.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Dispatch attempts for a single row before it is dead-lettered. Default: 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long a claim is honoured before another dispatcher may reclaim it. Default: 5 minutes.</summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// v0.7.19 alert threshold for the dispatch-lag SLO. When a per-row dispatch lag
    /// exceeds this duration the <c>orionaudit.dispatch.lag.violations</c> counter
    /// increments so operators can alert without setting up a p99 calculation in their
    /// monitoring stack. Default <see langword="null"/> = no threshold (back-compat).
    /// </summary>
    public TimeSpan? DispatchLagViolationThreshold { get; set; }
}

/// <summary>Fluent builder for <see cref="AsyncCaptureOptions"/>, passed to <c>UseAsyncCapture</c>.</summary>
public sealed class AsyncCaptureBuilder
{
    internal AsyncCaptureOptions Options { get; } = new();

    /// <summary>Overrides the dispatcher poll interval. Must be positive.</summary>
    public AsyncCaptureBuilder PollInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Must be positive.");
        }
        Options.PollInterval = interval;
        return this;
    }

    /// <summary>Overrides the per-cycle batch size. Must be >= 1.</summary>
    public AsyncCaptureBuilder BatchSize(int size)
    {
        if (size < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Must be >= 1.");
        }
        Options.BatchSize = size;
        return this;
    }

    /// <summary>Overrides the dead-letter attempt cap. Must be >= 1.</summary>
    /// <summary>
    /// v0.7.19 fluent setter for the dispatch-lag SLO threshold. Pass <see langword="null"/>
    /// (the default) to disable the counter.
    /// </summary>
    public AsyncCaptureBuilder DispatchLagViolationThreshold(TimeSpan? threshold)
    {
        Options.DispatchLagViolationThreshold = threshold;
        return this;
    }

    public AsyncCaptureBuilder MaxAttempts(int attempts)
    {
        if (attempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempts), attempts, "Must be >= 1.");
        }
        Options.MaxAttempts = attempts;
        return this;
    }

    /// <summary>Overrides the claim lease. Must be positive.</summary>
    public AsyncCaptureBuilder ClaimLease(TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease), lease, "Must be positive.");
        }
        Options.ClaimLease = lease;
        return this;
    }
}
