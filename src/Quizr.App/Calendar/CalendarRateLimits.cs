using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace Quizr.App.Calendar;

// Two limits, chained, and the numbers are the whole content of this file — the mechanism is
// ASP.NET Core's own rate limiter, which needs no help beyond being told what to partition on.
//
// The per-token limit stops one misconfigured client outweighing all real traffic: a calendar
// app polling in a loop is the ordinary failure here, not an attack. The per-IP limit stops a
// scanner, whose requests never reach a real token at all and would otherwise cost a route
// match each.
//
// Generous on purpose. Google refetches a subscribed calendar every 8-24 hours and Apple about
// hourly, so a person's own client asking ten times in a minute is already a client that has
// gone wrong.
internal static class CalendarRateLimits
{
    private const int PerTokenPerMinute = 10;
    private const int PerAddressPerMinute = 60;

    public static void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = (context, _) =>
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
            return ValueTask.CompletedTask;
        };

        // GlobalLimiter rather than a named policy, because chaining produces a
        // PartitionedRateLimiter that AddPolicy cannot take — and because the feed is the only
        // route this process serves, so "global" and "on that endpoint" are the same set. That
        // stops being true the moment a second route is added, which is when this needs
        // splitting into a policy attached to the calendar endpoint alone.
        options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            // Read straight off the query string rather than from route values, which keeps
            // this independent of where the middleware sits relative to routing.
            Partitioned(context => context.Request.Query[CalendarUrls.TokenParameter].ToString(), PerTokenPerMinute),
            Partitioned(context => context.Connection.RemoteIpAddress?.ToString() ?? "unknown", PerAddressPerMinute)
        );
    }

    private static PartitionedRateLimiter<HttpContext> Partitioned(
        Func<HttpContext, string> partitionKey,
        int permitsPerMinute
    ) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                }
            )
        );
}
