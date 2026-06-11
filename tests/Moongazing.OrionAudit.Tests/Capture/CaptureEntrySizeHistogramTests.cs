namespace Moongazing.OrionAudit.Tests.Capture;

using System.Diagnostics.Metrics;
using Xunit;

public sealed class CaptureEntrySizeHistogramTests
{
    [Fact]
    public void RecordCaptureEntrySize_emits_for_positive_bytes()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.capture.entry_size_bytes")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordCaptureEntrySize(2048);

        lock (samples) { Assert.Contains(2048, samples); }
    }

    [Fact]
    public void RecordCaptureEntrySize_ignores_zero_and_negative_bytes()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.capture.entry_size_bytes")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordCaptureEntrySize(0);
        Moongazing.OrionAudit.OrionAuditTelemetry.RecordCaptureEntrySize(-10);

        lock (samples)
        {
            Assert.DoesNotContain(0, samples);
            Assert.DoesNotContain(-10, samples);
        }
    }
}
