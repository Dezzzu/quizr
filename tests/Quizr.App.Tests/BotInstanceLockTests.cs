using AwesomeAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quizr.App.Health;

namespace Quizr.App.Tests;

// Two processes, one database, and the claim that only one of them ever does any work. Real
// Postgres because that is the entire mechanism — an advisory lock is a database behaviour, and
// a faked one would only prove the test agrees with itself.
//
// NotInParallel: every test here contends for the same advisory lock key, which is the point,
// so they cannot be allowed to contend with each other.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
[NotInParallel]
public class BotInstanceLockTests
{
    private readonly PostgresFixture _fixture;

    public BotInstanceLockTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task TheFirstInstanceToStartLeads()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var leader = Create();

        await leader.Service.StartAsync(ct);
        await WaitForLeadershipAsync(leader.Service, ct);

        leader.Service.IsLeading.Should().BeTrue();
        leader.Lifetime.DidNotReceive().StopApplication();
    }

    // The rolling-update case. The second container starts, finds the lock taken, and waits —
    // it does not poll Telegram, does not tick, and above all does not give up and exit.
    [Test]
    public async Task ASecondInstanceStandsByRatherThanLeadingOrStopping()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var leader = Create();
        await using var standby = Create();

        await leader.Service.StartAsync(ct);
        await WaitForLeadershipAsync(leader.Service, ct);
        await standby.Service.StartAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        standby.Service.IsLeading.Should().BeFalse();
        standby.Lifetime.DidNotReceive().StopApplication();
        leader.Service.IsLeading.Should().BeTrue();
    }

    // And the handover: the old container stops, Postgres drops its session, and the waiting
    // one takes over without anybody coordinating it.
    [Test]
    public async Task TheStandbyTakesOverOnceTheLeaderStops()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        var leader = Create();
        await using var standby = Create();

        await leader.Service.StartAsync(ct);
        await WaitForLeadershipAsync(leader.Service, ct);
        await standby.Service.StartAsync(ct);
        standby.Service.IsLeading.Should().BeFalse();

        await leader.DisposeAsync();

        await WaitForLeadershipAsync(standby.Service, ct, TimeSpan.FromSeconds(20));
        standby.Service.IsLeading.Should().BeTrue();
    }

    private static async Task WaitForLeadershipAsync(
        BotInstanceLock instanceLock,
        CancellationToken ct,
        TimeSpan? timeout = null
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));

        await instanceLock.WaitForLeadershipAsync(deadline.Token);
    }

    private Instance Create()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();

        return new Instance(
            new BotInstanceLock(_fixture.ConnectionString, lifetime, NullLogger<BotInstanceLock>.Instance),
            lifetime
        );
    }

    private sealed record Instance(BotInstanceLock Service, IHostApplicationLifetime Lifetime) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
        }
    }
}
