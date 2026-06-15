namespace Moongazing.OrionAudit.Tests.Capture;

using System.Diagnostics.Metrics;
using Xunit;

public sealed class DispatchIdlePollCounterTests
{
    [Fact]
    public void RecordDispatchIdlePoll_increments_the_counter()
    {
        var total = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit" && instrument.Name == "orionaudit.dispatch.poll.idle")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) =>
            System.Threading.Interlocked.Add(ref total, val));
        listener.Start();

        Moongazing.OrionAudit.OrionAuditTelemetry.RecordDispatchIdlePoll();
        Moongazing.OrionAudit.OrionAuditTelemetry.RecordDispatchIdlePoll();

        Assert.Equal(2L, System.Threading.Interlocked.Read(ref total));
    }
}
