using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// Background service that periodically drains the async capture queue via
/// <see cref="AuditDispatcher{TDbContext}"/>. Registered automatically by <c>AddOrionAudit</c>
/// when <c>UseAsyncCapture</c> is configured.
/// </summary>
public sealed partial class AuditDispatcherHostedService<TDbContext> : BackgroundService
    where TDbContext : DbContext
{
    [LoggerMessage(EventId = 12, Level = LogLevel.Error,
        Message = "OrionAudit dispatch cycle failed; will retry on the next interval.")]
    private partial void LogCycleFailed(Exception ex);

    private readonly AuditDispatcher<TDbContext> dispatcher;
    private readonly AsyncCaptureOptions options;
    private readonly TimeProvider clock;
    private readonly ILogger<AuditDispatcherHostedService<TDbContext>> logger;

    /// <summary>Initializes a new dispatcher hosted service.</summary>
    public AuditDispatcherHostedService(
        AuditDispatcher<TDbContext> dispatcher,
        AsyncCaptureOptions options,
        TimeProvider clock,
        ILogger<AuditDispatcherHostedService<TDbContext>> logger)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval, clock);
        do
        {
            try
            {
                // Drain fully each tick so a burst does not accumulate across intervals.
                int processed;
                do
                {
                    processed = await dispatcher.DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                while (processed > 0 && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // background loop swallows unexpected failures to keep ticking
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogCycleFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
