using Quizr.Domain;

namespace Quizr.App.Calendar;

// Where one person stands on one game, as their calendar shows it. Playing and Reserve are
// derived from the live roster (invariant 2); Played and DidNotPlay come from the
// participation row a finished game materialised (invariant 10), which is why a finished game
// is never read back off its signups.
public enum CalendarEntryStatus
{
    Playing,
    Reserve,
    Played,
    DidNotPlay,
}

// One game, flattened to exactly what a VEVENT needs. Everything is already resolved — no
// entities, no navigations, no clock — so the renderer is a pure function over this and a
// locale, and its tests need no database.
//
// Title and FranchiseName stay separate because combining them is a localized decision
// (GameLabel's rule: don't say the brand twice), and localization belongs to the renderer.
public sealed record CalendarEntry(
    GameId GameId,
    TeamId TeamId,
    string TeamName,
    string Title,
    string? FranchiseName,
    string Venue,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset RevisedAt,
    int Revision,
    string TimeZoneId,
    CalendarEntryStatus Status,
    // 1-based within the reserve, and only meaningful when Status is Reserve.
    int ReservePosition,
    decimal? Price,
    string? Notes,
    IReadOnlyList<string> Tags,
    // Only the guests this person brought to this game, and only while the game is live — a
    // participation row records who was there, not who invited them, so a finished game
    // cannot answer this.
    int GuestCount,
    string? AnnouncementUrl
);

// SharedTeamName is the team's name when every entry belongs to one team, and null when the
// feed spans more than one — which is the same question twice: what to call the calendar, and
// whether an event's summary has to name its team to be legible. Deriving it once here keeps
// the two answers from disagreeing.
public sealed record CalendarFeed(string? SharedTeamName, IReadOnlyList<CalendarEntry> Entries);
