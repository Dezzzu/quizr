using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.Domain;

namespace Quizr.App.Calendar;

// GET/HEAD /cal/{token}.ics — the only thing that has ever connected *to* this bot.
//
// The shape of a request, in the order it gets cheaper to answer:
//
//   1. the route constraint rejects a wrong-length token during routing
//   2. a charset check rejects a malformed one with no query
//   3. one indexed row read turns the token into a player, a version and a locale
//   4. If-None-Match against the ETag answers 304 — nothing is loaded, nothing is rendered
//   5. the in-memory cache answers with a body built by an earlier request
//   6. only then does anything touch the roster
//
// Every failure answers a bare 404: malformed, unknown and revoked are deliberately
// indistinguishable, and there is no 401 or 403 anywhere, because a challenge would only teach
// a scanner that the path is real — and no calendar client could answer one.
public sealed class CalendarEndpoint
{
    private const string ContentType = "text/calendar; charset=utf-8";

    // Long enough that a busy database still answers, short enough that a hung query cannot
    // pile connections up behind an aggressive client's retries.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // A key changes whenever anything in it changes, so nothing here ever needs evicting for
    // correctness — but the UTC date is one of those parts, so yesterday's keys would
    // otherwise sit in memory for the life of the process.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(25);

    private readonly QuizrDb _db;
    private readonly CalendarFeedService _feeds;
    private readonly IStrings _strings;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger<CalendarEndpoint> _logger;

    public CalendarEndpoint(
        QuizrDb db,
        CalendarFeedService feeds,
        IStrings strings,
        IMemoryCache cache,
        TimeProvider clock,
        ILogger<CalendarEndpoint> logger
    )
    {
        _db = db;
        _feeds = feeds;
        _strings = strings;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IResult> HandleAsync(HttpContext http, string token, CancellationToken ct)
    {
        // The third place in this codebase that catches broadly, and the first one STYLE.md did
        // not already name. Two reasons, and the second is the one that matters: a failing
        // request must not take anything else down, and an exception escaping to ASP.NET's own
        // diagnostics middleware puts RequestPath — which contains the token — into a logging
        // scope that IncludeScopes ships to Seq. Nothing below ever logs the path or the token.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            return await RespondAsync(http, token, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client hung up. Nothing to say and nobody to say it to.
            return Results.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Calendar feed request failed");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<IResult> RespondAsync(HttpContext http, string token, CancellationToken ct)
    {
        if (!CalendarToken.IsWellFormed(token))
        {
            return Results.NotFound();
        }

        var subscriber = await _db
            .Players.AsNoTracking()
            .Where(p => p.CalendarToken == token)
            .Select(p => new Subscriber(p.Id, p.CalendarVersion, p.Locale))
            .SingleOrDefaultAsync(ct);

        if (subscriber is null)
        {
            return Results.NotFound();
        }

        var key = CalendarCacheKey.Compute(
            subscriber.PlayerId,
            subscriber.CalendarVersion,
            DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime)
        );
        var etag = CalendarCacheKey.ToETag(key);

        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "private, max-age=3600";

        // A conditional request that matches ends here: no roster is loaded and nothing is
        // serialized, which is the entire point of keeping the version on the player row.
        if (Matches(http.Request.Headers.IfNoneMatch, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var body = await RenderAsync(subscriber, key, ct);

        http.Response.Headers.ContentDisposition = "inline; filename=\"quizr.ics\"";

        return Results.Text(body, ContentType, Encoding.UTF8);
    }

    private async Task<string> RenderAsync(Subscriber subscriber, string key, CancellationToken ct)
    {
        if (_cache.TryGetValue<string>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var feed = await _feeds.LoadAsync(subscriber.PlayerId, ct);

        // Their own language, never the team's: a private calendar is as personal as a DM, and
        // a feed spanning two teams still renders as one document in one language.
        var body = CalendarRenderer.Render(feed, _strings.For(subscriber.Locale ?? "en"));

        _cache.Set(
            key,
            body,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheLifetime, Size = body.Length }
        );

        return body;
    }

    // If-None-Match may carry several tags, and a client is allowed to send W/ in front of one
    // it received strong. Both are the same body here, so the comparison ignores the prefix.
    private static bool Matches(IEnumerable<string?> ifNoneMatch, string etag) =>
        ifNoneMatch
            .Where(header => header is not null)
            .SelectMany(header =>
                header!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            )
            .Any(candidate => candidate == "*" || Strip(candidate) == etag);

    private static string Strip(string candidate) =>
        candidate.StartsWith("W/", StringComparison.Ordinal) ? candidate[2..] : candidate;

    private sealed record Subscriber(PlayerId PlayerId, long CalendarVersion, string? Locale);
}
