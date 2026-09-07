namespace Quizr.App.Calendar;

// Where the feed lives, in the one place both the route and the link the bot hands out can
// read it — so a change to the path cannot make /mycalendar start printing a URL that 404s.
//
// QUIZR_PUBLIC_URL is what turns the feature on. Unset, the endpoint is never mapped and
// /mycalendar says the feed is unavailable: a local run and a deployment that has not been
// given a domain both keep behaving exactly as they did before this existed.
public sealed class CalendarUrls
{
    // The token is a query parameter and deliberately not a path segment. ASP.NET's own
    // request scope carries RequestPath, and IncludeScopes ships scopes to Seq — so anything
    // at all that logs during a feed request would carry a token that lived in the path. That
    // is not hypothetical: an EF Core query warning did exactly that, and so did this
    // endpoint's own error handler. The scope does not carry the query string, which is the
    // whole reason for this shape.
    //
    // The .ics suffix stays in the path so a client still sees a calendar file.
    public const string Route = "/cal/feed.ics";

    public const string TokenParameter = "t";

    private readonly string? _baseUrl;

    public CalendarUrls(string? publicUrl) => _baseUrl = publicUrl?.TrimEnd('/');

    public bool Available => _baseUrl is not null;

    public string For(string token) => $"{_baseUrl}/cal/feed.ics?{TokenParameter}={token}";
}
