using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Quizr.App.Health;

// Which container is in charge. Exactly one process may poll Telegram and run the scheduler:
// two callers of getUpdates collide, and worse, auto-finish would materialise a game's
// participation rows twice (docs/DEPLOY.md).
public interface IBotInstanceLock
{
    bool IsLeading { get; }

    // Completes once this process holds the lock. A standby waits here for as long as the
    // leader lives, which is the whole point — it stays started, stays answering health checks,
    // and does no work.
    Task WaitForLeadershipAsync(CancellationToken ct);
}

// A Postgres session-level advisory lock, held on a connection of its own for the life of the
// process. No table, no lease, no expiry: Postgres releases it when the session ends, so a
// hard-killed container hands over with nothing to clean up and no clock skew to reason about.
//
// STACK.md says there are no locks in this system. That rule is about domain state — it exists
// so nobody reaches for one instead of the derived playing/reserve split. This is process
// election, a different question, and it is the one place the answer is a lock.
//
// Acquisition deliberately runs in the background rather than blocking startup. A standby that
// blocked would never finish starting, never serve /health/ready, and so never be declared
// ready by Coolify — which is precisely the deadlock that would make a rolling update time out
// instead of proceeding.
public sealed class BotInstanceLock : BackgroundService, IBotInstanceLock
{
    // Arbitrary but fixed forever: 0x5175697A is "Quiz" in ASCII. Advisory lock keys are global
    // to the database, and nothing else in this one takes any.
    private const long LockKey = 0x5175697A;

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    // Frequent because it bounds the only window in which two processes could both believe they
    // lead: the leader's session dying, and the leader noticing.
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource _leading = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _connectionString;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<BotInstanceLock> _logger;

    private volatile bool _isLeading;

    public BotInstanceLock(string connectionString, IHostApplicationLifetime lifetime, ILogger<BotInstanceLock> logger)
    {
        // Pooling off, and this is not a detail. An advisory lock belongs to the session, and a
        // pooled connection handed back keeps its session alive — Npgsql defers the DISCARD ALL
        // that would release the lock until the connection is next *used*, which on shutdown is
        // never. The lock would then outlive the container that took it, and the replacement
        // would wait for an idle timeout rather than taking over. A test caught this; nothing
        // about the code looked wrong.
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        _lifetime = lifetime;
        _logger = logger;
    }

    public bool IsLeading => _isLeading;

    public Task WaitForLeadershipAsync(CancellationToken ct) => _leading.Task.WaitAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Its own unpooled connection — see the constructor for why pooling would break the
        // handover this exists to make work.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(stoppingToken);

        if (!await AcquireAsync(connection, stoppingToken))
        {
            return;
        }

        await HoldAsync(connection, stoppingToken);
    }

    private async Task<bool> AcquireAsync(NpgsqlConnection connection, CancellationToken stoppingToken)
    {
        var announced = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
            command.Parameters.AddWithValue(LockKey);

            if (await command.ExecuteScalarAsync(stoppingToken) is true)
            {
                _isLeading = true;
                _leading.TrySetResult();
                _logger.LogInformation("This instance is leading: the bot and scheduler are starting");
                return true;
            }

            // Once, not every five seconds — a standby can wait for the length of a deploy, and
            // repeating itself would bury whatever else is happening.
            if (!announced)
            {
                _logger.LogInformation(
                    "Another instance is leading. Standing by, serving health checks and doing no work"
                );
                announced = true;
            }

            await Task.Delay(RetryInterval, stoppingToken);
        }

        return false;
    }

    private async Task HoldAsync(NpgsqlConnection connection, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(ProbeInterval, stoppingToken);

            // The lock is held by the session, so the session being alive is the whole check.
            try
            {
                await using var probe = new NpgsqlCommand("SELECT 1", connection);
                await probe.ExecuteScalarAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The session is gone, so Postgres has already released the lock and another
                // instance may take it at any moment. Stopping is the honest response: standing
                // down and re-acquiring would mean reasoning about a window where two processes
                // both think they lead, and the bot cannot do anything useful without a database
                // anyway. Coolify restarts it, and it leads again or waits.
                _isLeading = false;
                _logger.LogError(ex, "Lost the instance lock's database session; stopping so the container restarts");
                _lifetime.StopApplication();
                return;
            }
        }
    }
}

// Reports which instance is leading, so a standby is not mistaken for a broken one. It is
// healthy — started, serving, and deliberately idle — and saying otherwise would stall the
// rolling update that put it there.
internal sealed class LeadershipHealthCheck : IHealthCheck
{
    private readonly IBotInstanceLock _instanceLock;

    public LeadershipHealthCheck(IBotInstanceLock instanceLock) => _instanceLock = instanceLock;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            HealthCheckResult.Healthy(_instanceLock.IsLeading ? "Leading." : "Standing by for the leader to stop.")
        );
}
