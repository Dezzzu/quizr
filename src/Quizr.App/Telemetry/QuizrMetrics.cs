using System.Diagnostics.Metrics;

namespace Quizr.App.Telemetry;

// The three questions the structured logs answer badly: how much work the bot is doing, what
// is failing and where, and whether the scheduler is still alive. Counters rather than more
// log lines because these are asked as rates over time, and because a counter outlives log
// retention — the incident that prompted this was diagnosed by grepping container logs that
// would have aged out a day later.
//
// One Meter, named in MeterName so the composition root subscribes to exactly this and
// nothing else. When no OTLP endpoint is configured nothing subscribes at all, and an Add on
// an unobserved instrument is a no-op — so call sites never need to know whether telemetry is
// switched on, and there is no null check anywhere.
public sealed class QuizrMetrics
{
    public const string MeterName = "Quizr";

    // Where a failure was caught, not what threw it: every one of these is a place the code
    // deliberately swallows an exception to keep going, and "which of those is firing" is the
    // question worth asking. Kept coarse on purpose — this is a metric label, so every value
    // here is a time series.
    public const string UpdateSource = "update";
    public const string SchedulerTickSource = "scheduler.tick";
    public const string SchedulerTeamSource = "scheduler.team";
    public const string SchedulerGameSource = "scheduler.game";
    public const string SchedulerDialogsSource = "scheduler.dialogs";
    public const string SchedulerAnnouncementSource = "scheduler.announcement";

    private readonly Counter<long> _updates;
    private readonly Counter<long> _exceptions;
    private readonly Counter<long> _schedulerTicks;

    public QuizrMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _updates = meter.CreateCounter<long>("quizr.updates", "{update}", "Telegram updates the bot has handled.");
        _exceptions = meter.CreateCounter<long>(
            "quizr.exceptions",
            "{exception}",
            "Exceptions caught at a boundary that keeps the bot running."
        );
        _schedulerTicks = meter.CreateCounter<long>(
            "quizr.scheduler.ticks",
            "{tick}",
            "Scheduler ticks completed. The heartbeat: alert on its absence, not on its value."
        );
    }

    public void RecordUpdate() => _updates.Add(1);

    // error.type is the OpenTelemetry convention for this, and a .NET type name is bounded —
    // unlike a message, which would put arbitrary text (chat titles, player names) into a
    // label and blow up cardinality.
    public void RecordException(Exception exception, string source) =>
        _exceptions.Add(
            1,
            new KeyValuePair<string, object?>("error.type", exception.GetType().FullName),
            new KeyValuePair<string, object?>("quizr.source", source)
        );

    // Incremented once per completed tick, which is what makes it a heartbeat: the scheduler
    // runs every 30 seconds with nobody asking it to, so a gap in this counter means the loop
    // stopped — the failure an HTTP liveness probe on a long-polling process cannot see, and
    // the reason DEPLOY.md's "configure no health check" doesn't leave the bot unwatched.
    public void RecordSchedulerTick() => _schedulerTicks.Add(1);
}
