using System.Diagnostics.Metrics;
using Xunit;

namespace Moongazing.OrionAudit.Tests.Telemetry;

/// <summary>
/// v0.11.1 convergence guard. The Orion.Abstractions convergence intentionally LEFT the
/// instrumentation alone (the observer invocation converged onto SafeObserverInvoker; the telemetry
/// did not move onto OrionInstrumentation). These tests pin the observable contract that any future
/// instrumentation re-base must preserve byte-for-byte: the ActivitySource name, the Meter name, and
/// every emitted instrument's name. Renaming a metric or trace source is a silent break for every
/// dashboard / alert that filters on it, so this asserts the names a consumer's MeterListener sees.
/// </summary>
public sealed class OrionAuditTelemetryNamingTests
{
    [Fact]
    public void Source_and_meter_names_are_stable()
    {
        Assert.Equal("OrionAudit", OrionAuditTelemetry.ActivitySourceName);
        Assert.Equal("OrionAudit", OrionAuditTelemetry.MeterName);
    }

    [Fact]
    public void Every_instrument_is_published_under_the_OrionAudit_meter_with_its_expected_name()
    {
        // The instrument names a consumer's pipeline filters on. If a re-base ever changes the Meter
        // name or any instrument name, the published set diverges from this frozen list and the test
        // fails. ObservableGauges are included: they publish on listener start.
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "orionaudit.entries.written",
            "orionaudit.entries.failed",
            "orionaudit.capture.duration",
            "orionaudit.reconstruct.duration",
            "orionaudit.reconstruct.events_replayed",
            "orionaudit.snapshots.written",
            "orionaudit.retention.rows_deleted",
            "orionaudit.retention.dry_run_rows",
            "orionaudit.retention.sweep.duration",
            "orionaudit.dispatch.lag",
            "orionaudit.capture.entries_per_save",
            "orionaudit.retention.dispatched",
            "orionaudit.retention.errors",
            "orionaudit.dispatch.errors",
            "orionaudit.dispatch.batch_size",
            "orionaudit.dispatch.poll.idle",
            "orionaudit.dispatch.lag.violations",
            "orionaudit.capture.entry_size_bytes",
            "orionaudit.dispatch.events_per_publish",
            "orionaudit.dispatch.publish.duration_ms",
            "orionaudit.dispatch.retries_before_success",
            "orionaudit.dispatch.rows_processed",
            "orionaudit.dispatch.rows_deadlettered",
            "orionaudit.dispatch.batch.duration",
            "orionaudit.dispatch.flush_duration_ms",
            "orionaudit.dispatch.claim_duration_ms",
            "orionaudit.capture.queue_depth",
            "orionaudit.capture.dlq_depth",
            "orionaudit.compaction.cycles",
            "orionaudit.compaction.streams_compacted",
            "orionaudit.compaction.rows_folded",
            "orionaudit.compaction.errors",
            "orionaudit.compaction.sweep.duration",
            "orionaudit.import.rows_written",
            "orionaudit.import.rows_skipped",
            "orionaudit.import.rows_deadlettered",
            "orionaudit.import.batch.duration",
            "orionaudit.events.published",
            "orionaudit.events.dropped",
        };

        var published = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == OrionAuditTelemetry.MeterName)
            {
                published.Add(instrument.Name);
            }
        };
        listener.Start();

        // Touch the static type so its field initializers (which create every instrument) run.
        _ = OrionAuditTelemetry.MeterName;
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(OrionAuditTelemetry).TypeHandle);

        // Every expected instrument was published under the OrionAudit meter, and nothing
        // unexpected appeared under that meter name.
        Assert.True(expected.SetEquals(published),
            "Published OrionAudit instruments diverged from the frozen set. " +
            $"Missing: [{string.Join(", ", expected.Except(published))}]. " +
            $"Unexpected: [{string.Join(", ", published.Except(expected))}].");
    }
}
