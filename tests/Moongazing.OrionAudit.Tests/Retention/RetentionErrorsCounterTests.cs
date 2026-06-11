namespace Moongazing.OrionAudit.Tests.Retention;

using System.Diagnostics.Metrics;
using Xunit;

public sealed class RetentionErrorsCounterTests
{
    [Fact]
    public void RecordRetentionError_emits_a_measurement_tagged_with_exception_type()
    {
        var samples = new System.Collections.Generic.List<(string exceptionType, long val)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.retention.errors")
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

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordRetentionError("TimeoutException");

        lock (samples)
        {
            Assert.Contains(samples, s => s.exceptionType == "TimeoutException" && s.val == 1);
        }
    }
}
