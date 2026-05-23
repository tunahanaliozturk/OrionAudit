using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AsyncCaptureOptionsTests
{
    [Fact]
    public void Defaults_AreSensible()
    {
        var o = new AsyncCaptureOptions();
        Assert.Equal(TimeSpan.FromSeconds(2), o.PollInterval);
        Assert.Equal(500, o.BatchSize);
        Assert.Equal(5, o.MaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(5), o.ClaimLease);
    }

    [Fact]
    public void UseAsyncCapture_NotCalled_LeavesAsyncDisabled()
    {
        var options = new OrionAuditOptions();
        Assert.False(options.AsyncCaptureEnabled);
    }

    [Fact]
    public void UseAsyncCapture_EnablesAndAppliesBuilderOverrides()
    {
        var options = new OrionAuditOptions();
        options.UseAsyncCapture(q => q
            .PollInterval(TimeSpan.FromSeconds(10))
            .BatchSize(50)
            .MaxAttempts(3)
            .ClaimLease(TimeSpan.FromMinutes(1)));

        Assert.True(options.AsyncCaptureEnabled);
        Assert.Equal(TimeSpan.FromSeconds(10), options.AsyncCaptureOptions.PollInterval);
        Assert.Equal(50, options.AsyncCaptureOptions.BatchSize);
        Assert.Equal(3, options.AsyncCaptureOptions.MaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(1), options.AsyncCaptureOptions.ClaimLease);
    }

    [Fact]
    public void BatchSize_Rejects_NonPositive()
    {
        var b = new AsyncCaptureBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => b.BatchSize(0));
    }
}
