namespace Moongazing.OrionAudit.Tests.Telemetry;

using System.Diagnostics.Metrics;
using Xunit;

public sealed class DlqDepthGaugeTests
{
    [Fact]
    public void Gauge_reports_the_value_set_via_SetDlqDepth_internal_helper()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit"
                && instrument.Name == "orionaudit.capture.dlq_depth")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) =>
        {
            long current;
            do { current = System.Threading.Interlocked.Read(ref observed); }
            while (val > current && System.Threading.Interlocked.CompareExchange(ref observed, val, current) != current);
        });
        listener.Start();

        // SetDlqDepth is internal; the test assembly already has InternalsVisibleTo wired
        // for the Tests project through Moongazing.OrionAudit.csproj.
        typeof(Moongazing.OrionAudit.OrionAuditTelemetry)
            .GetMethod("SetDlqDepth", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { 42L });

        listener.RecordObservableInstruments();

        Assert.True(System.Threading.Interlocked.Read(ref observed) >= 42L);
    }
}
