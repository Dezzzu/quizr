using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Quizr.App.Calendar;

namespace Quizr.App.Http;

// Two limits that mean different things, so they are registered differently rather than
// chained into one blanket rule.
//
// The per-address limit is **global**: a flood from one place must not cost this process
// whatever route it aims at, and that has nothing to do with the calendar. It is what stops a
// scanner, whose requests never reach a real token and would otherwise get a fresh per-token
// partition each time.
//
// The per-token limit is **the feed's**, attached to its endpoint alone. It stops one
// misconfigured client outweighing all real traffic — a calendar app polling in a loop is the
// ordinary failure here, not an attack.
//
// Both apply to a feed request: the middleware takes a lease from the global limiter and from
// the endpoint's policy, and either can refuse. Everything else in the process gets the global
// limit only.
//
// Generous on purpose. Google refetches a subscribed calendar every 8-24 hours and Apple about
// hourly, so a person's own client asking ten times in a minute has already gone wrong.
internal static class RateLimits
{
    public const string CalendarFeedPolicy = "calendar-feed";

    private const int PerTokenPerMinute = 10;
    private const int PerAddressPerMinute = 60;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = (context, _) =>
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
            return ValueTask.CompletedTask;
        };

        // Global, so it covers routes that do not exist yet — health probes among them, which
        // is the whole reason the per-token limit stopped being global. A probe carries no
        // token, so under one blanket rule every probe shared a partition with every malformed
        // feed request, and a container could be restarted for answering 429 to its own health
        // check.
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            Fixed(AddressPartitionKey(context), PerAddressPerMinute)
        );

        options.AddPolicy(CalendarFeedPolicy, context => Fixed(TokenPartitionKey(context), PerTokenPerMinute));
    }

    // Read straight off the query string rather than from route values, which keeps this
    // independent of where the middleware sits relative to routing. Every request without a
    // token shares one partition — harmless for the feed, where a tokenless request is a 404
    // either way, and the specific reason this policy must never cover a health probe.
    internal static string TokenPartitionKey(HttpContext context) =>
        context.Request.Query[CalendarUrls.TokenParameter].ToString();

    internal static string AddressPartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static RateLimitPartition<string> Fixed(string partitionKey, int permitsPerMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitsPerMinute, Window = Window }
        );
}
