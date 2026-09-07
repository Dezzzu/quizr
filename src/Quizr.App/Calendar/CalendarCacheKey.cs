using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Quizr.Domain;

namespace Quizr.App.Calendar;

// One value identifies a rendered feed, and it does two jobs: it keys the in-memory cache, and
// hashed, it is the ETag. Four parts, each of which has to be there.
//
//   PlayerId          feeds are per person
//   CalendarVersion   any data change affecting this person — CalendarVersionInterceptor
//   Format version    a deploy that changes the serializer's output. Without it, fixing a bug
//                     in the .ics would keep answering 304 against the broken body forever,
//                     because the data behind it never changed
//   UTC date          the window is a rolling 30 days back and 12 months forward, so what
//                     belongs in the feed changes as days pass with no data change at all.
//                     Without it a game crossing the horizon would never appear
//
// The ETag is the hash rather than the parts, so an internal row id and a change count do not
// travel through proxies and client logs on every request.
internal static class CalendarCacheKey
{
    public static string Compute(PlayerId playerId, long calendarVersion, DateOnly utcDate)
    {
        var parts = string.Create(
            CultureInfo.InvariantCulture,
            $"{CalendarFormat.Version}:{playerId.Value}:{calendarVersion}:{utcDate:yyyy-MM-dd}"
        );

        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(parts)));
    }

    // Strong rather than weak: the renderer is a pure function of the feed, so equal keys mean
    // byte-identical bodies. CalendarRendererTests pins that by rendering twice and comparing.
    public static string ToETag(string key) => $"\"{key}\"";
}
