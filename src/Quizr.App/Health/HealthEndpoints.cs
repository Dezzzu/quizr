namespace Quizr.App.Health;

// Two questions, deliberately not one.
//
// Liveness is "did the process answer", and nothing more. It runs no checks and touches no
// database, which is what makes it safe to poll often and safe to exempt from rate limiting.
//
// Readiness is "could this instance do the work" — Postgres reachable, and the scheduler
// actually ticking. That is the failure worth catching, because it is the one that leaves a
// process looking perfectly alive while it quietly stops doing anything.
//
// Neither is gated on QUIZR_PUBLIC_URL: they answer whether the bot is healthy, which is a
// question worth asking on a deployment that serves no calendar feed at all.
internal static class HealthEndpoints
{
    public const string Live = "/health/live";
    public const string Ready = "/health/ready";

    // Everything tagged with this runs for readiness and is skipped by liveness.
    public const string ReadyTag = "ready";
}
