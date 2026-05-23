using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AsyncCaptureWiringTests
{
    private sealed class WiringDb : DbContext
    {
        public WiringDb(DbContextOptions<WiringDb> options) : base(options) { }
    }

    [Fact]
    public void SyncMode_Registers_NoOpDispatcher_And_NoHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<WiringDb>(o => { });
        using var sp = services.BuildServiceProvider();

        Assert.IsType<NoOpAuditDispatcher>(sp.GetRequiredService<IAuditDispatcher>());
        Assert.DoesNotContain(sp.GetServices<IHostedService>(),
            h => h is AuditDispatcherHostedService<WiringDb>);
        Assert.Null(sp.GetService<AsyncCaptureOptions>());
    }

    [Fact]
    public void AsyncMode_Registers_RealDispatcher_AndHostedService_AndOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<WiringDb>(o => o.UseAsyncCapture(q => q.BatchSize(7)));
        using var sp = services.BuildServiceProvider();

        Assert.IsType<AuditDispatcher<WiringDb>>(sp.GetRequiredService<IAuditDispatcher>());
        Assert.Contains(sp.GetServices<IHostedService>(),
            h => h is AuditDispatcherHostedService<WiringDb>);
        Assert.Equal(7, sp.GetRequiredService<AsyncCaptureOptions>().BatchSize);
    }
}
