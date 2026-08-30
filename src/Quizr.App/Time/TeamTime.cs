namespace Quizr.App.Time;

// The one place a stored instant is converted to a team's local clock — CLAUDE.md: "All
// conversions live behind a TeamTime service... Nowhere else." A game's StartsAt is the
// only thing stored; everything a person reads is derived from it here.
internal static class TeamTime
{
    public static DateTimeOffset ConvertToLocal(DateTimeOffset instant, string timeZoneId) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));

    public static TimeSpan GetUtcOffset(DateTimeOffset instant, string timeZoneId) =>
        TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).GetUtcOffset(instant);
}
