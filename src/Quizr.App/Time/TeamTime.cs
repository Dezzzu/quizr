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

    // The other direction: a local date and time in the team's zone (a reminder slot, a
    // picked game date) to the instant it actually is. Ambiguous or nonexistent times around
    // a DST transition resolve to whatever TimeZoneInfo's own rules pick — not worth a bespoke
    // policy for a once-a-day reminder slot.
    public static DateTimeOffset ConvertToUtc(DateOnly localDate, TimeOnly localTime, string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = localDate.ToDateTime(localTime);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
