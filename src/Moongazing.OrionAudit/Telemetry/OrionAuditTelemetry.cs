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

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.2.0");
    internal static readonly Meter Meter = new(MeterName, "0.2.0");

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
}
