using Quizr.App.Health;
using Quizr.App.Services;
using Quizr.App.Telemetry;

namespace Quizr.App.Scheduling;

// Ticks every 30 seconds and asks what's due now (STACK.md) — not a job queue, so a missed
// tick or a restart needs no reconciliation, just another query. Runs the first tick
// immediately on start rather than waiting out the interval first, which is what gives
// "catch up on start" for anything that came due while the process was down.
public sealed class SchedulerHostedService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBotInstanceLock _instanceLock;
    private readonly SchedulerHeartbeat _heartbeat;
    private readonly TimeProvider _clock;
    private readonly QuizrMetrics _metrics;
    private readonly ILogger<SchedulerHostedService> _logger;

    public SchedulerHostedService(
        IServiceScopeFactory scopeFactory,
        IBotInstanceLock instanceLock,
        SchedulerHeartbeat heartbeat,
        TimeProvider clock,
        QuizrMetrics metrics,
        ILogger<SchedulerHostedService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _instanceLock = instanceLock;
        _heartbeat = heartbeat;
        _clock = clock;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Counts ticks for SchedulerService's round-robin announcement check, which needs to
        // land on a different game each time. It lives here because this is the only thing in
        // the process that outlives a single tick — the service itself is resolved fresh from a
        // new scope below. Resetting to zero on a deploy just means the cycle restarts, which
        // costs nothing.
        // Reminders, auto-finish and pin maintenance all write, and auto-finish has no
        // duplicate guard of its own — so a second instance must not tick at all.
        await _instanceLock.WaitForLeadershipAsync(stoppingToken);

        var tickNumber = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Re-checked every tick rather than only at the start: leadership is lost by
                // the database session dying, and this is what stops the most damaging work in
                // the seconds before the process notices and exits.
                if (!_instanceLock.IsLeading)
                {
                    break;
                }

                using var scope = _scopeFactory.CreateScope();
                await scope
                    .ServiceProvider.GetRequiredService<SchedulerService>()
                    .RunTickAsync(stoppingToken, tickNumber++);

                // Only after the tick actually returned. Counted here rather than inside
                // RunTickAsync so it means "a whole tick completed" — a tick that threw past
                // the per-team handling leaves a gap, which is the point of a heartbeat.
                _metrics.RecordSchedulerTick();

                // Same moment and the same meaning as the counter above — a whole tick
                // completed — but readable as a question rather than charted as a rate, which
                // is what a readiness probe needs.
                _heartbeat.Record(_clock.GetUtcNow());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _metrics.RecordException(ex, QuizrMetrics.SchedulerTickSource);
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
