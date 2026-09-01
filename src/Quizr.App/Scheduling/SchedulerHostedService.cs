using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quizr.App.Services;

namespace Quizr.App.Scheduling;

// Ticks every 30 seconds and asks what's due now (STACK.md) — not a job queue, so a missed
// tick or a restart needs no reconciliation, just another query. Runs the first tick
// immediately on start rather than waiting out the interval first, which is what gives
// "catch up on start" for anything that came due while the process was down.
public sealed class SchedulerHostedService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<SchedulerHostedService> _logger;

    public SchedulerHostedService(
        IServiceScopeFactory scopeFactory,
        TimeProvider clock,
        ILogger<SchedulerHostedService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Counts ticks for SchedulerService's round-robin announcement check, which needs to
        // land on a different game each time. It lives here because this is the only thing in
        // the process that outlives a single tick — the service itself is resolved fresh from a
        // new scope below. Resetting to zero on a deploy just means the cycle restarts, which
        // costs nothing.
        var tickNumber = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope
                    .ServiceProvider.GetRequiredService<SchedulerService>()
                    .RunTickAsync(stoppingToken, tickNumber++);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // One failing tick must not stop reminders forever — the next tick just
                // asks the same idempotent question again. See STYLE.md's two broad-catch
                // boundaries.
                _logger.LogError(ex, "Scheduler tick failed");
            }

            try
            {
                await Task.Delay(TickInterval, _clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
