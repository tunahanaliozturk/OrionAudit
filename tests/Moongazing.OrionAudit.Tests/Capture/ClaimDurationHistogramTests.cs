namespace Moongazing.OrionAudit.Tests.Capture;

using System.Diagnostics.Metrics;
using Xunit;

public sealed class ClaimDurationHistogramTests
{
    [Fact]
    public void RecordDispatchClaimDuration_emits_for_positive_ms()
    {
        var samples = new System.Collections.Generic.List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.dispatch.claim_duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordDispatchClaimDuration(33.5);

        lock (samples) { Assert.Contains(33.5, samples); }
    }

    [Fact]
    public void RecordDispatchClaimDuration_clamps_negative_to_zero()
    {
        var samples = new System.Collections.Generic.List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.dispatch.claim_duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordDispatchClaimDuration(-12.0);

        lock (samples) { Assert.Contains(0.0, samples); }
    }
}
