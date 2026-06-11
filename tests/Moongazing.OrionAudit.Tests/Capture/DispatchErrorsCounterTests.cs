namespace Moongazing.OrionAudit.Tests.Capture;

using System.Diagnostics.Metrics;
using Xunit;

public sealed class DispatchErrorsCounterTests
{
    [Fact]
    public void RecordDispatchError_emits_a_measurement_tagged_with_exception_type()
    {
        var samples = new System.Collections.Generic.List<(string exceptionType, long val)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.dispatch.errors")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, val, tags, _) =>
        {
            string type = string.Empty;
            foreach (var t in tags)
            {
                if (t.Key == "exception_type" && t.Value is string s) { type = s; }
            }
            lock (samples) { samples.Add((type, val)); }
        });
        listener.Start();

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordDispatchError("JsonException");

        lock (samples)
        {
            Assert.Contains(samples, s => s.exceptionType == "JsonException" && s.val == 1);
        }
    }
}
