namespace Moongazing.OrionAudit.Tests.Capture;

using System.Diagnostics.Metrics;
using Xunit;

public sealed class PublishDurationHistogramTests
{
    [Fact]
    public void RecordPublishDuration_emits_for_positive_milliseconds()
    {
        var samples = new System.Collections.Generic.List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.dispatch.publish.duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordPublishDuration(42.5);

        lock (samples) { Assert.Contains(42.5, samples); }
    }

    [Fact]
    public void RecordPublishDuration_clamps_negative_to_zero()
    {
        var samples = new System.Collections.Generic.List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.dispatch.publish.duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordPublishDuration(-5.0);

        lock (samples) { Assert.Contains(0.0, samples); }
    }
}
