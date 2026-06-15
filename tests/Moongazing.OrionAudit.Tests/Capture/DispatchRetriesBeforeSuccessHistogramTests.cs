namespace Moongazing.OrionAudit.Tests.Capture;

using System.Diagnostics.Metrics;
using Xunit;

public sealed class DispatchRetriesBeforeSuccessHistogramTests
{
    private const string InstrumentName = "orionaudit.dispatch.retries_before_success";

    private static System.Collections.Generic.List<int> Capture(System.Action act)
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "OrionAudit" && instrument.Name == InstrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        act();

        lock (samples) { return new System.Collections.Generic.List<int>(samples); }
    }

    [Fact]
    public void RecordRetriesBeforeSuccess_emits_a_first_try_zero()
    {
        // The first-try success (0 prior attempts) IS recorded: the zero fraction is the signal.
        var samples = Capture(() => Moongazing.OrionAudit.OrionAuditTelemetry.RecordRetriesBeforeSuccess(0));
        Assert.Contains(0, samples);
    }

    [Fact]
    public void RecordRetriesBeforeSuccess_emits_the_retry_count()
    {
        var samples = Capture(() => Moongazing.OrionAudit.OrionAuditTelemetry.RecordRetriesBeforeSuccess(2));
        Assert.Contains(2, samples);
    }

    [Fact]
    public void RecordRetriesBeforeSuccess_clamps_negative_to_zero()
    {
        var samples = Capture(() => Moongazing.OrionAudit.OrionAuditTelemetry.RecordRetriesBeforeSuccess(-5));
        Assert.Contains(0, samples);
        Assert.DoesNotContain(-5, samples);
    }
}
