using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Health;

namespace Quizr.App.Tests;

// The check that carries the actual signal. The scheduler is the only loop here that survives
// its own failures, so "still ticking" is the one thing a probe can learn that a crash cannot
// already tell you.
public class SchedulerHealthCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task ARecentTickIsHealthy()
    {
        var clock = new FakeTimeProvider(Now);
        var heartbeat = new SchedulerHeartbeat();
        heartbeat.Record(Now);

        clock.Advance(TimeSpan.FromSeconds(30));

        (await CheckAsync(heartbeat, clock)).Status.Should().Be(HealthStatus.Healthy);
    }

    // The failure this exists to catch: the process is answering, so liveness would say nothing
    // is wrong, and the scheduler has quietly stopped doing any work.
    [Test]
    public async Task ATickOlderThanTheWindowIsUnhealthy()
    {
        var clock = new FakeTimeProvider(Now);
        var heartbeat = new SchedulerHeartbeat();
        heartbeat.Record(Now);

        clock.Advance(SchedulerHealthCheck.Window + TimeSpan.FromSeconds(1));

        (await CheckAsync(heartbeat, clock)).Status.Should().Be(HealthStatus.Unhealthy);
    }

    // Exactly on the boundary stays healthy, so a check running at the window's edge doesn't
    // flap between two probes a second apart.
    [Test]
    public async Task ATickExactlyAtTheWindowIsStillHealthy()
    {
        var clock = new FakeTimeProvider(Now);
        var heartbeat = new SchedulerHeartbeat();
        heartbeat.Record(Now);

        clock.Advance(SchedulerHealthCheck.Window);

        (await CheckAsync(heartbeat, clock)).Status.Should().Be(HealthStatus.Healthy);
    }

    // Starting up is not ready. The scheduler runs its first tick immediately, so this is a
    // window of seconds — but reporting ready before anything has run would be a lie.
    [Test]
    public async Task AProcessThatHasNeverTickedIsNotReady()
    {
        var result = await CheckAsync(new SchedulerHeartbeat(), new FakeTimeProvider(Now));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("not completed a tick");
    }

    [Test]
    public void TheHeartbeatReportsTheLastTickItWasGiven()
    {
        var heartbeat = new SchedulerHeartbeat();

        heartbeat.LastTickAt.Should().BeNull();

        heartbeat.Record(Now);
        heartbeat.LastTickAt.Should().Be(Now);

        heartbeat.Record(Now.AddMinutes(5));
        heartbeat.LastTickAt.Should().Be(Now.AddMinutes(5));
    }

    private static Task<HealthCheckResult> CheckAsync(SchedulerHeartbeat heartbeat, TimeProvider clock) =>
        new SchedulerHealthCheck(heartbeat, clock).CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current!.Execution.CancellationToken
        );
}
