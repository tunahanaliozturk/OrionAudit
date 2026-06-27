namespace Moongazing.OrionAudit.Store;

/// <summary>
/// Tunables for the background compaction sweep (<c>AuditCompactionHostedService</c>). The sweep
/// runs the existing <see cref="IAuditHistoryStore.CompactAsync"/> engine on a schedule so operators
/// do not have to invoke compaction by hand. Defaults: hourly, fold a stream once it has more than
/// <see cref="MinRowsBeforeCompaction"/> rows, keep a <see cref="RetainTail"/> of 50, touch at most
/// <see cref="MaxStreamsPerSweep"/> streams per cycle so each cycle stays bounded.
/// </summary>
public sealed class CompactionSweepOptions
{
    /// <summary>How often the hosted service runs a compaction sweep. Default: 1 hour.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Number of most-recent rows to keep verbatim after the compacted snapshot for each stream the
    /// sweep folds (passed straight through to <see cref="AuditCompactionRequest.RetainTail"/>).
    /// Default: 50.
    /// </summary>
    public int RetainTail { get; set; } = 50;

    /// <summary>
    /// A stream is only considered for compaction once it has strictly more than this many audit rows.
    /// Streams shorter than this are skipped so the sweep never thrashes over already-short histories.
    /// Must be at least <c>RetainTail + 1</c> for a fold to be possible: the smallest candidate has
    /// <c>MinRowsBeforeCompaction + 1</c> rows (the filter is strictly greater-than), and a fold needs at
    /// least two rows beyond the retained tail to collapse. Default: 200.
    /// </summary>
    public int MinRowsBeforeCompaction { get; set; } = 200;

    /// <summary>
    /// Upper bound on how many entity streams the sweep folds per cycle, so a single cycle does a
    /// bounded amount of work regardless of how many streams are eligible. The next cycle picks up the
    /// rest. Default: 100.
    /// </summary>
    public int MaxStreamsPerSweep { get; set; } = 100;

    /// <summary>
    /// Optional wall-clock budget per sweep cycle. When set, the sweep stops starting new
    /// per-stream compactions once this elapses since the cycle began, returning whatever it has
    /// folded so far. Mirrors the retention sweep's <c>MaxSweepDuration</c>. Default
    /// <see langword="null"/> = unbounded.
    /// </summary>
    public TimeSpan? MaxSweepDuration { get; set; }
}
