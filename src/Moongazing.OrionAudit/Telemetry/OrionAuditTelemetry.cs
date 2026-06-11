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

    internal static readonly Counter<long> DispatchRowsProcessed = Meter.CreateCounter<long>(
        "orionaudit.dispatch.rows_processed", unit: "rows", description: "Capture-queue rows turned into audit rows by the dispatcher.");

    internal static readonly Counter<long> DispatchRowsDeadLettered = Meter.CreateCounter<long>(
        "orionaudit.dispatch.rows_deadlettered", unit: "rows", description: "Capture-queue rows dead-lettered after exhausting dispatch attempts.");

    internal static readonly Histogram<double> DispatchBatchDuration = Meter.CreateHistogram<double>(
        "orionaudit.dispatch.batch.duration", unit: "ms", description: "Dispatcher batch duration per cycle.");

    private static long dispatchQueueDepth;

    /// <summary>Last observed capture-queue depth; updated by the dispatcher each cycle.</summary>
    internal static void SetQueueDepth(long depth) => Interlocked.Exchange(ref dispatchQueueDepth, depth);

    internal static readonly ObservableGauge<long> DispatchQueueDepth = Meter.CreateObservableGauge<long>(
        "orionaudit.capture.queue_depth",
        () => Interlocked.Read(ref dispatchQueueDepth),
        unit: "rows", description: "Capture-queue rows awaiting dispatch, as last observed by the dispatcher.");

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
