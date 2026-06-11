namespace Moongazing.OrionAudit.Tests.Capture;

using System.Diagnostics.Metrics;
using Xunit;

public sealed class DispatchBatchSizeHistogramTests
{
    [Fact]
    public void RecordDispatchBatchSize_emits_for_non_zero_input()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.dispatch.batch_size")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordDispatchBatchSize(42);

        lock (samples) { Assert.Contains(42, samples); }
    }

    [Fact]
    public void RecordDispatchBatchSize_does_not_emit_for_zero()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.dispatch.batch_size")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordDispatchBatchSize(0);

        lock (samples) { Assert.DoesNotContain(0, samples); }
    }
}
