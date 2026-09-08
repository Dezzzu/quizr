using Microsoft.Extensions.Diagnostics.HealthChecks;
using Quizr.App.Data;

namespace Quizr.App.Health;

// Can this process reach Postgres at all. Everything the bot does needs it, so an instance that
// cannot is not ready to take anything over.
//
// CanConnectAsync rather than a health-check package: it uses the connection string and pool
// this application already has, swallows the failure into a bool itself, and is the whole check
// — a dependency for that would be three lines of savings and one more thing to keep current.
internal sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly QuizrDb _db;

    public DatabaseHealthCheck(QuizrDb db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) =>
        await _db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Postgres is unreachable.");
}
