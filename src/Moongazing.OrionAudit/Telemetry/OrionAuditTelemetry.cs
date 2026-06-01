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

    internal static readonly Histogram<double> RetentionSweepDuration = Meter.CreateHistogram<double>(
        "orionaudit.retention.sweep.duration", unit: "ms", description: "Retention sweep duration per cycle.");

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
