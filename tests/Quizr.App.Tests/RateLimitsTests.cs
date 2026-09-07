using System.Net;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Quizr.App.Http;

namespace Quizr.App.Tests;

// The two limits mean different things and are registered differently, so they are checked
// separately. What would go unnoticed otherwise is the global one quietly covering nothing —
// it is the only thing standing between the process and a scanner, whose every request lands
// in a fresh per-token partition and so can never be refused by the feed's own policy.
public class RateLimitsTests
{
    [Test]
    public void OneAddressGetsSixtyRequestsAMinuteWhateverItAsksFor()
    {
        var limiter = GlobalLimiter();
        var caller = Request("203.0.113.7");

        var allowed = Enumerable.Range(0, 60).Count(_ => limiter.AttemptAcquire(caller).IsAcquired);

        allowed.Should().Be(60);
        limiter.AttemptAcquire(caller).IsAcquired.Should().BeFalse();
    }

    [Test]
    public void TwoAddressesDoNotShareABudget()
    {
        var limiter = GlobalLimiter();
        var noisy = Request("203.0.113.7");
        var innocent = Request("203.0.113.8");

        for (var i = 0; i < 60; i++)
        {
            limiter.AttemptAcquire(noisy);
        }

        limiter.AttemptAcquire(noisy).IsAcquired.Should().BeFalse();
        limiter.AttemptAcquire(innocent).IsAcquired.Should().BeTrue();
    }

    // A scanner's whole shape: a new well-formed token every request. The feed's policy cannot
    // refuse any of them, which is why the address limit is global rather than folded in here.
    [Test]
    public void EveryTokenGetsItsOwnPartition()
    {
        RateLimits
            .TokenPartitionKey(Request("203.0.113.7", token: "first"))
            .Should()
            .NotBe(RateLimits.TokenPartitionKey(Request("203.0.113.7", token: "second")));
    }

    [Test]
    public void TheSameTokenFromAnywhereIsOnePartition()
    {
        RateLimits
            .TokenPartitionKey(Request("203.0.113.7", token: "same"))
            .Should()
            .Be(RateLimits.TokenPartitionKey(Request("198.51.100.4", token: "same")));
    }

    // Everything without a token shares one partition, which is exactly why health probes must
    // not be covered by this policy: they carry no token, so a probe every few seconds would
    // spend a budget meant for malformed feed requests and answer 429 to its own monitor.
    [Test]
    public void RequestsWithNoTokenAllShareOnePartition()
    {
        var key = RateLimits.TokenPartitionKey(Request("203.0.113.7"));

        key.Should().BeEmpty();
        RateLimits.TokenPartitionKey(Request("198.51.100.4")).Should().Be(key);
    }

    private static PartitionedRateLimiter<HttpContext> GlobalLimiter()
    {
        var options = new RateLimiterOptions();
        RateLimits.Configure(options);

        return options.GlobalLimiter!;
    }

    private static DefaultHttpContext Request(string address, string? token = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);

        if (token is not null)
        {
            context.Request.QueryString = new QueryString($"?t={token}");
        }

        return context;
    }
}
