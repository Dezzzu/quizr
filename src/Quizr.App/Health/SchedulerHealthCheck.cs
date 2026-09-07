using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Quizr.App.Health;

internal sealed class SchedulerHealthCheck : IHealthCheck
{
    // Six missed ticks at the scheduler's 30-second interval. Slack enough that a slow database
    // does not flap the check, tight enough that a wedged loop is caught within a few minutes —
    // and the same signal `quizr.scheduler.ticks` is alerted on in Seq, asked as a question
    // instead of charted.
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(3);

    private readonly SchedulerHeartbeat _heartbeat;
    private readonly TimeProvider _clock;

    public SchedulerHealthCheck(SchedulerHeartbeat heartbeat, TimeProvider clock)
    {
        _heartbeat = heartbeat;
        _clock = clock;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (_heartbeat.LastTickAt is not { } lastTick)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The scheduler has not completed a tick yet."));
        }

        var since = _clock.GetUtcNow() - lastTick;

        return Task.FromResult(
            since <= Window
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"The scheduler last ticked {since.TotalSeconds:F0}s ago.")
        );
    }
}
