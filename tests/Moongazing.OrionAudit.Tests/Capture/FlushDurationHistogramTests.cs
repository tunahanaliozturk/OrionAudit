namespace Moongazing.OrionAudit.Tests.Capture;

using System.Diagnostics.Metrics;
using Xunit;

public sealed class FlushDurationHistogramTests
{
    [Fact]
    public void RecordDispatchFlushDuration_emits_for_positive_ms()
    {
        var samples = new System.Collections.Generic.List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.dispatch.flush_duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordDispatchFlushDuration(95.5);

        lock (samples) { Assert.Contains(95.5, samples); }
    }

    [Fact]
    public void RecordDispatchFlushDuration_clamps_negative_to_zero()
    {
        var samples = new System.Collections.Generic.List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.dispatch.flush_duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordDispatchFlushDuration(-25.0);

        lock (samples) { Assert.Contains(0.0, samples); }
    }
}
