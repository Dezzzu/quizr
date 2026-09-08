namespace Quizr.App.Health;

// When the scheduler last finished a whole tick. The one liveness signal in this process worth
// probing for, because the scheduler is the only loop that survives its own failures:
// SchedulerHostedService catches a failed tick and goes round again, so ticks can stop while
// the process stays perfectly alive. The Telegram loop needs no equivalent — an exception out
// of BotHostedService.ExecuteAsync stops the host, so a broken bot loop already kills the
// container.
//
// Written by the scheduler's loop and read by a request thread, so the timestamp is kept as a
// long and exchanged atomically. A DateTimeOffset field is wider than a word and could be read
// half-updated.
public sealed class SchedulerHeartbeat
{
    private long _lastTickUtcTicks;

    public void Record(DateTimeOffset at) => Interlocked.Exchange(ref _lastTickUtcTicks, at.UtcTicks);

    // Null until the first tick completes, which readiness treats as "not ready yet" rather
    // than as a failure — the scheduler runs its first tick immediately on start.
    public DateTimeOffset? LastTickAt =>
        Interlocked.Read(ref _lastTickUtcTicks) is var ticks && ticks == 0
            ? null
            : new DateTimeOffset(ticks, TimeSpan.Zero);
}
