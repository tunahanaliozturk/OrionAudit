namespace Moongazing.OrionAudit.Tests.Capture;

using Moongazing.OrionAudit.Capture;
using Xunit;

public sealed class AuditCaptureObserverContractTests
{
    [Fact]
    public void NullAuditCaptureObserver_OnCaptured_completes_without_throwing()
    {
        var sut = new NullAuditCaptureObserver();

        sut.OnCaptured(auditedEntityCount: 5, isAsyncCapture: true);
        sut.OnCaptured(auditedEntityCount: 0, isAsyncCapture: false);
    }

    [Fact]
    public void Custom_observer_receives_count_and_async_flag()
    {
        int capturedCount = -1;
        bool? capturedAsync = null;
        var sut = new CapturingObserver((count, async) =>
        {
            capturedCount = count;
            capturedAsync = async;
        });

        sut.OnCaptured(auditedEntityCount: 42, isAsyncCapture: true);

        Assert.Equal(42, capturedCount);
        Assert.True(capturedAsync);
    }

    private sealed class CapturingObserver : IAuditCaptureObserver
    {
        private readonly System.Action<int, bool> capture;
        public CapturingObserver(System.Action<int, bool> capture) => this.capture = capture;
        public void OnCaptured(int auditedEntityCount, bool isAsyncCapture) => capture(auditedEntityCount, isAsyncCapture);
    }
}
