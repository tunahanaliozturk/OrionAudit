using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Tests;

public class NoOpAuditDispatcherTests
{
    [Fact]
    public async Task NoOp_FlushPending_Returns0()
        => Assert.Equal(0, await new NoOpAuditDispatcher().FlushPendingAsync());

    [Fact]
    public async Task NoOp_GetQueueDepth_Returns0()
        => Assert.Equal(0, await new NoOpAuditDispatcher().GetQueueDepthAsync());
}
