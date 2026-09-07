namespace Quizr.App.Calendar;

// Where the feed lives, in the one place both the route and the link the bot hands out can
// read it — so a change to the path cannot make /mycalendar start printing a URL that 404s.
//
// QUIZR_PUBLIC_URL is what turns the feature on. Unset, the endpoint is never mapped and
// /mycalendar says the feed is unavailable: a local run and a deployment that has not been
// given a domain both keep behaving exactly as they did before this existed.
public sealed class CalendarUrls
{
    // The token's length is fixed (CalendarToken.Length), so the constraint rejects a
    // wrong-sized one during routing, before any handler or database work happens. The charset
    // is checked in the handler — a route constraint cannot express it without a regex whose
    // braces have to be escaped into the template, which is a worse trade than one `if`.
    public const string Route = "/cal/{token:length(43)}.ics";

    private readonly string? _baseUrl;

    public CalendarUrls(string? publicUrl) => _baseUrl = publicUrl?.TrimEnd('/');

    public bool Available => _baseUrl is not null;

    public string For(string token) => $"{_baseUrl}/cal/{token}.ics";
}
