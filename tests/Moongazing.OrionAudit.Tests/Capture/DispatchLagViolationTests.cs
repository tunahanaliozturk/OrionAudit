namespace Moongazing.OrionAudit.Tests.Capture;

using System.Diagnostics.Metrics;
using Xunit;

public sealed class DispatchLagViolationTests
{
    [Fact]
    public void RecordDispatchLagViolation_emits_a_single_increment()
    {
        long count = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.dispatch.lag.violations")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) => Interlocked.Add(ref count, val));
        listener.Start();

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordDispatchLagViolation();

        Assert.Equal(1, Interlocked.Read(ref count));
    }

    [Fact]
    public void Default_AsyncCaptureOptions_has_null_violation_threshold_for_backcompat()
    {
        var opts = new Moongazing.OrionAudit.Configuration.AsyncCaptureOptions();

        Assert.Null(opts.DispatchLagViolationThreshold);
    }
}
