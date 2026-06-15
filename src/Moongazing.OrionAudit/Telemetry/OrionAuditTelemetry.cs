using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Moongazing.OrionAudit;

/// <summary>Activity source, meter, and instrument constants for OrionAudit telemetry.</summary>
public static class OrionAuditTelemetry
{
    /// <summary>The ActivitySource name registered for audit spans.</summary>
    public const string ActivitySourceName = "OrionAudit";

    /// <summary>The Meter name registered for audit metrics.</summary>
    public const string MeterName = "OrionAudit";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.7.0");
    internal static readonly Meter Meter = new(MeterName, "0.7.0");

    internal static readonly Counter<long> EntriesWritten = Meter.CreateCounter<long>(
        "orionaudit.entries.written", unit: "entries", description: "Audit entries successfully written.");

    internal static readonly Counter<long> EntriesFailed = Meter.CreateCounter<long>(
        "orionaudit.entries.failed", unit: "entries", description: "Audit entries written with diff errors.");

    internal static readonly Histogram<double> CaptureDuration = Meter.CreateHistogram<double>(
        "orionaudit.capture.duration", unit: "ms", description: "Interceptor capture duration per save.");

    internal static readonly Histogram<double> ReconstructDuration = Meter.CreateHistogram<double>(
        "orionaudit.reconstruct.duration", unit: "ms", description: "Time-travel reconstruction duration.");

    internal static readonly Counter<long> SnapshotsWritten = Meter.CreateCounter<long>(
        "orionaudit.snapshots.written", unit: "snapshots", description: "Periodic snapshots written by the SnapshotPolicy.");

    internal static readonly Counter<long> RetentionRowsDeleted = Meter.CreateCounter<long>(
        "orionaudit.retention.rows_deleted", unit: "rows", description: "Audit rows hard-deleted by the retention sweep.");

    internal static readonly Counter<long> RetentionDryRunRows = Meter.CreateCounter<long>(
        "orionaudit.retention.dry_run_rows", unit: "rows", description: "Audit rows the retention sweep WOULD have removed but did not (dry-run mode).");

    internal static readonly Histogram<double> RetentionSweepDuration = Meter.CreateHistogram<double>(
        "orionaudit.retention.sweep.duration", unit: "ms", description: "Retention sweep duration per cycle.");

    /// <summary>
    /// v0.7.13 dispatch latency: time between an event's <c>OccurredOnUtc</c> and the
    /// moment the dispatcher turns its queue entry into an <c>AuditLog</c> row. Operators
    /// graph p50/p99 to spot capture-queue backlog or dispatcher slowdown long before
    /// rows pile up beyond <c>orionaudit.dispatch.rows_processed</c>'s steady-state rate.
    /// </summary>
    internal static readonly Histogram<double> DispatchLag = Meter.CreateHistogram<double>(
        "orionaudit.dispatch.lag", unit: "ms",
        description: "Per-row dispatch latency (queue entry OccurredOnUtc -> AuditLog write).");

    /// <summary>
    /// v0.7.14 distribution of audited rows produced per SaveChangesAsync. Operators graph
    /// p99 to spot outlier saves (e.g. a bulk import path that should have been audited in
    /// smaller chunks) and right-size capture-queue partitioning.
    /// </summary>
    internal static readonly Histogram<int> CaptureEntriesPerSave = Meter.CreateHistogram<int>(
        "orionaudit.capture.entries_per_save", unit: "{rows}",
        description: "Audited rows produced per SaveChangesAsync call.");

    /// <summary>
    /// v0.7.15 retention dispatch counter. Increments once per <c>SweepOnceAsync</c> cycle
    /// with the policy branch the dispatcher took (<c>retain_for</c>, <c>retain_count</c>,
    /// <c>per_tenant</c>, <c>per_entity_type</c>, <c>none</c>). Operators graph the rate to
    /// confirm the live policy matches the configured one across a rolling deployment.
    /// </summary>
    internal static readonly Counter<long> RetentionDispatched = Meter.CreateCounter<long>(
        "orionaudit.retention.dispatched", unit: "{cycles}",
        description: "Retention sweep cycles dispatched, tagged with the policy branch taken.");

    internal static void RecordRetentionDispatched(string policyBranch)
        => RetentionDispatched.Add(1, new KeyValuePair<string, object?>("policy", policyBranch));

    /// <summary>
    /// v0.7.16 retention sweep failure counter. Increments when the background loop
    /// swallows an unexpected exception from `SweepOnceAsync` (cancellation does NOT
    /// emit; only real failures do). Operators page on
    /// rate(orionaudit_retention_errors_total[5m]) to catch a stuck or thrashing sweep
    /// long before retention SLAs slip.
    /// </summary>
    internal static readonly Counter<long> RetentionErrors = Meter.CreateCounter<long>(
        "orionaudit.retention.errors", unit: "{errors}",
        description: "Unexpected exceptions swallowed by the retention sweep loop.");

    /// <summary>
    /// Record a swallowed exception from the retention sweep loop. Public so consumer-
    /// owned retention drivers can opt in to the same metric shape.
    /// </summary>
    /// <param name="exceptionType">Short type name (e.g. <c>TimeoutException</c>).</param>
    public static void RecordRetentionError(string exceptionType)
        => RetentionErrors.Add(1, new KeyValuePair<string, object?>("exception_type", exceptionType));

    /// <summary>
    /// v0.7.17 dispatcher row failure counter. Increments when <c>AuditDispatcher</c>
    /// swallows a per-row exception. Distinct from
    /// <see cref="DispatchRowsDeadLettered"/> which only counts rows that exhausted
    /// MaxAttempts; this counter fires on EVERY failure including the transient ones
    /// that the dispatcher retries on the next cycle. Operators page on
    /// <c>rate(orionaudit_dispatch_errors_total[5m])</c> to spot a flaky dispatch
    /// pipeline long before rows reach the dead-letter threshold.
    /// </summary>
    internal static readonly Counter<long> DispatchErrors = Meter.CreateCounter<long>(
        "orionaudit.dispatch.errors", unit: "{errors}",
        description: "Per-row dispatch failures swallowed by the dispatcher (transient + terminal).");

    /// <summary>Record a per-row dispatch failure tagged with the exception type.</summary>
    public static void RecordDispatchError(string exceptionType)
        => DispatchErrors.Add(1, new KeyValuePair<string, object?>("exception_type", exceptionType));

    /// <summary>
    /// v0.7.18 distribution of rows claimed per dispatcher cycle. Operators graph p99
    /// to spot a dispatcher that consistently maxes out BatchSize (raise the batch) or
    /// stays near 0 (over-sized polling cadence). Zero-row cycles do NOT emit; idle
    /// polling is the role of an ObservableGauge that the dispatcher already provides.
    /// </summary>
    internal static readonly Histogram<int> DispatchBatchSize = Meter.CreateHistogram<int>(
        "orionaudit.dispatch.batch_size", unit: "{rows}",
        description: "Capture-queue rows claimed per dispatcher cycle (non-empty cycles only).");

    /// <summary>
    /// v0.7.28 idle-poll counter. Increments each dispatcher cycle that claims an empty batch
    /// (the capture queue had no dispatchable rows). Operators graph the idle-poll rate against
    /// the total poll rate to right-size the polling cadence: a high idle fraction is a cost-of-
    /// poll signal, while a low fraction means the dispatcher is busy and BatchSize may need
    /// raising. Distinct from the queue/DLQ depth gauges (current backlog) and the v0.7.18
    /// batch_size histogram (which deliberately skips these zero-row cycles). Mirrors the Guard
    /// v6.5.17 / Patch v0.2.28 poll.idle counters on the Audit side.
    /// </summary>
    internal static readonly Counter<long> DispatchIdlePolls = Meter.CreateCounter<long>(
        "orionaudit.dispatch.poll.idle", unit: "{polls}",
        description: "Dispatcher cycles that claimed an empty batch.");

    /// <summary>Record one idle poll (empty-claim cycle). Public for consumer-owned dispatchers.</summary>
    public static void RecordDispatchIdlePoll() => DispatchIdlePolls.Add(1);

    /// <summary>
    /// v0.7.19 dispatch lag SLO violation counter. Increments each time a per-row
    /// dispatch lag exceeds the consumer-supplied
    /// <see cref="Configuration.AsyncCaptureOptions.DispatchLagViolationThreshold"/>.
    /// Operators alert on the rate without needing a p99 calculation; the threshold
    /// itself is the SLO.
    /// </summary>
    internal static readonly Counter<long> DispatchLagViolations = Meter.CreateCounter<long>(
        "orionaudit.dispatch.lag.violations", unit: "{rows}",
        description: "Per-row dispatch lag exceeded the consumer-configured SLO threshold.");

    /// <summary>Public so consumer-owned dispatchers can opt in to the same metric shape.</summary>
    public static void RecordDispatchLagViolation()
        => DispatchLagViolations.Add(1);

    /// <summary>Public so consumer-owned dispatchers can opt in to the same metric shape.</summary>
    public static void RecordDispatchBatchSize(int count)
    {
        if (count <= 0)
        {
            return;
        }
        DispatchBatchSize.Record(count);
    }

    /// <summary>
    /// v0.7.20 distribution of capture queue entry payload size in bytes (BeforeJson +
    /// AfterJson combined). Operators graph p99 to size storage column types, spot a
    /// tenant whose audited entities suddenly grew, and right-size the capture-queue
    /// poll cadence against actual byte throughput.
    /// </summary>
    internal static readonly Histogram<int> CaptureEntrySize = Meter.CreateHistogram<int>(
        "orionaudit.capture.entry_size_bytes", unit: "By",
        description: "Capture queue entry payload size in bytes (BeforeJson + AfterJson).");

    /// <summary>Public so consumer-owned dispatchers can opt in to the same metric shape.</summary>
    public static void RecordCaptureEntrySize(int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }
        CaptureEntrySize.Record(bytes);
    }

    /// <summary>
    /// v0.7.22 distribution of event count per <c>IAuditEventPublisher.PublishAsync</c>
    /// call. Operators graph p99 to see whether publishes are sized appropriately for
    /// the broker (small batches = under-utilising broker throughput; over-large
    /// batches risk timeouts and partial-success ambiguity). Pairs with the v0.7.21
    /// publish.duration_ms histogram - duration alone cannot distinguish a slow
    /// publisher from a publisher that took on a large batch.
    /// </summary>
    internal static readonly Histogram<int> EventsPerPublish = Meter.CreateHistogram<int>(
        "orionaudit.dispatch.events_per_publish", unit: "{events}",
        description: "Events sent per IAuditEventPublisher.PublishAsync call.");

    /// <summary>Public so consumer-owned dispatchers can opt in to the same metric shape.</summary>
    public static void RecordEventsPerPublish(int count)
    {
        if (count <= 0)
        {
            return;
        }
        EventsPerPublish.Record(count);
    }

    /// <summary>
    /// v0.7.21 distribution of <c>IAuditEventPublisher.PublishAsync</c> wall-clock per
    /// dispatcher cycle. Operators graph p99 to spot a publisher whose downstream
    /// (Kafka, RabbitMQ, etc.) has regressed independently of the database side.
    /// Recorded only when a publisher is configured AND there is at least one event to
    /// publish (zero-event cycles emit nothing).
    /// </summary>
    internal static readonly Histogram<double> PublishDuration = Meter.CreateHistogram<double>(
        "orionaudit.dispatch.publish.duration_ms", unit: "ms",
        description: "IAuditEventPublisher.PublishAsync wall-clock per dispatcher cycle.");

    /// <summary>Public so consumer-owned dispatchers can opt in to the same metric shape.</summary>
    public static void RecordPublishDuration(double milliseconds)
        => PublishDuration.Record(System.Math.Max(0d, milliseconds));

    /// <summary>
    /// v0.7.27 distribution of how many attempts a queue row had already failed when it was
    /// finally turned into an audit row (its <c>Attempts</c> on the success path: 0 = succeeded
    /// on the first attempt). Distinct from the v0.7.17 <c>dispatch.errors</c> counter (which
    /// tallies failures) and <c>rows_deadlettered</c> (terminal only): this measures the
    /// successful side, so operators can answer "are retries quietly papering over a flaky
    /// capture-to-audit path?". A healthy system sits at p50 = 0; a rising upper percentile means
    /// rows are increasingly succeeding only after transient failures. Unlike the batch-shape
    /// histograms, the zero sample IS recorded here: the fraction of first-try successes is
    /// exactly the signal. Mirrors the Guard v6.5.27 <c>retries_before_success</c> shape.
    /// </summary>
    internal static readonly Histogram<int> DispatchRetriesBeforeSuccess = Meter.CreateHistogram<int>(
        "orionaudit.dispatch.retries_before_success", unit: "{retries}",
        description: "Attempts a queue row had failed before it dispatched successfully (0 = first-try).");

    /// <summary>
    /// Record the prior-attempt count of a successfully dispatched row. Negatives clamp to 0.
    /// Public so consumer-owned dispatchers can opt in to the same metric shape.
    /// </summary>
    public static void RecordRetriesBeforeSuccess(int retries)
        => DispatchRetriesBeforeSuccess.Record(System.Math.Max(0, retries));

    internal static readonly Counter<long> DispatchRowsProcessed = Meter.CreateCounter<long>(
        "orionaudit.dispatch.rows_processed", unit: "rows", description: "Capture-queue rows turned into audit rows by the dispatcher.");

    internal static readonly Counter<long> DispatchRowsDeadLettered = Meter.CreateCounter<long>(
        "orionaudit.dispatch.rows_deadlettered", unit: "rows", description: "Capture-queue rows dead-lettered after exhausting dispatch attempts.");

    internal static readonly Histogram<double> DispatchBatchDuration = Meter.CreateHistogram<double>(
        "orionaudit.dispatch.batch.duration", unit: "ms", description: "Dispatcher batch duration per cycle.");

    /// <summary>
    /// v0.7.24 distribution of SaveChangesAsync wall-clock at dispatch time. Operators
    /// graph p99 to isolate the EF write piece (AuditLog inserts + queue row deletes +
    /// commit) from the v0.7.13 dispatch.lag (capture-to-dispatch) and the existing
    /// dispatch.batch.duration (per-cycle wall-clock covering EVERYTHING).
    /// try/finally so a commit failure still emits the sample.
    /// </summary>
    internal static readonly Histogram<double> DispatchFlushDuration = Meter.CreateHistogram<double>(
        "orionaudit.dispatch.flush_duration_ms", unit: "ms",
        description: "SaveChangesAsync wall-clock at dispatch time (AuditLog insert + queue delete + commit).");

    /// <summary>Public so consumer-owned dispatchers can opt in.</summary>
    public static void RecordDispatchFlushDuration(double milliseconds)
        => DispatchFlushDuration.Record(System.Math.Max(0d, milliseconds));

    /// <summary>
    /// v0.7.25 distribution of the claim round-trip wall-clock per dispatcher cycle
    /// (the atomic UPDATE + the SELECT of claimed rows). Operators graph p99 to spot
    /// capture-table lock contention or index degradation on the claim path,
    /// isolated from the v0.7.24 flush_duration (write side) and v0.7.21
    /// publish.duration (broker side). ALL cycles emit including zero-row claims -
    /// claim latency is itself the signal. try/finally at the call site so failures
    /// also emit.
    /// </summary>
    internal static readonly Histogram<double> DispatchClaimDuration = Meter.CreateHistogram<double>(
        "orionaudit.dispatch.claim_duration_ms", unit: "ms",
        description: "Claim round-trip wall-clock per dispatcher cycle (UPDATE + SELECT).");

    /// <summary>Public so consumer-owned dispatchers can opt in.</summary>
    public static void RecordDispatchClaimDuration(double milliseconds)
        => DispatchClaimDuration.Record(System.Math.Max(0d, milliseconds));

    private static long dispatchQueueDepth;

    /// <summary>Last observed capture-queue depth; updated by the dispatcher each cycle.</summary>
    internal static void SetQueueDepth(long depth) => Interlocked.Exchange(ref dispatchQueueDepth, depth);

    internal static readonly ObservableGauge<long> DispatchQueueDepth = Meter.CreateObservableGauge<long>(
        "orionaudit.capture.queue_depth",
        () => Interlocked.Read(ref dispatchQueueDepth),
        unit: "rows", description: "Capture-queue rows awaiting dispatch, as last observed by the dispatcher.");

    // v0.7.23 dead-letter queue depth snapshot. Fed by the dispatcher each cycle alongside
    // the pending queue_depth counter. Distinct from rows_deadlettered (a rate-style
    // counter) - this exposes the live TABLE state so operators can spot a growing
    // dead-letter table that needs triage even when the dispatch rate looks stable.
    private static long dispatchDlqDepth;

    /// <summary>v0.7.23 dead-letter queue depth setter. Dispatchers call once per cycle.</summary>
    internal static void SetDlqDepth(long depth) => Interlocked.Exchange(ref dispatchDlqDepth, depth);

    internal static readonly ObservableGauge<long> DispatchDlqDepth = Meter.CreateObservableGauge<long>(
        "orionaudit.capture.dlq_depth",
        () => Interlocked.Read(ref dispatchDlqDepth),
        unit: "rows", description: "Capture-queue rows in dead-letter state (Error != null), as last observed by the dispatcher.");

    internal static readonly Counter<long> ImportRowsWritten = Meter.CreateCounter<long>(
        "orionaudit.import.rows_written", unit: "rows", description: "Audit rows written by the bulk importer.");

    internal static readonly Counter<long> ImportRowsSkipped = Meter.CreateCounter<long>(
        "orionaudit.import.rows_skipped", unit: "rows", description: "Bulk-import rows skipped via idempotency tag.");

    internal static readonly Counter<long> ImportRowsDeadLettered = Meter.CreateCounter<long>(
        "orionaudit.import.rows_deadlettered", unit: "rows", description: "Bulk-import rows written with Error populated.");

    internal static readonly Histogram<double> ImportBatchDuration = Meter.CreateHistogram<double>(
        "orionaudit.import.batch.duration", unit: "ms", description: "AuditImportBuilder SaveAsync duration.");

    internal static readonly Counter<long> EventsPublished = Meter.CreateCounter<long>(
        "orionaudit.events.published", unit: "events", description: "Audit events handed to IAuditEventPublisher.");

    internal static readonly Counter<long> EventsDropped = Meter.CreateCounter<long>(
        "orionaudit.events.dropped", unit: "events", description: "Audit events dropped by the publisher (handler exception or shutdown abandonment).");
}
