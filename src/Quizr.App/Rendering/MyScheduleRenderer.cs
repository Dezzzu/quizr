using System.Net;
using System.Text;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.App.Time;
using Quizr.Domain;

namespace Quizr.App.Rendering;

// One person's own upcoming games, date-ordered across every team they play for. The Board's
// counterpart, and deliberately not its twin: the Board answers "how full is this game", this
// answers "where am I going, and am I actually in".
internal static class MyScheduleRenderer
{
    public static string RenderText(IReadOnlyList<MyScheduleEntry> upcomingByStart, IStringsFor strings)
    {
        var text = new StringBuilder();
        text.Append(strings.Text("MySchedule.Header")).Append("\n\n");

        if (upcomingByStart.Count == 0)
        {
            text.Append(strings.Text("MySchedule.NoGames"));
            return text.ToString();
        }

        // A team's name only earns a place on the line when there is more than one to tell
        // apart. In a group there never is; in a DM, somebody who belongs to two teams but is
        // signed up in only one of them has nothing to disambiguate either. Derived from the
        // entries rather than passed in, so the three call paths can't disagree about it.
        var spansTeams = upcomingByStart.Select(e => e.Team.Id).Distinct().Count() > 1;

        foreach (var (game, team, franchiseName, placement, guestCount) in upcomingByStart)
        {
            // Each team's own zone, not one shared clock: this is the time that team's own
            // announcement and Board already said, and there is no per-person timezone to
            // convert to instead. Ordering is by the stored instant, so a cross-timezone
            // schedule still reads in the order the evenings actually happen.
            var local = TeamTime.ConvertToLocal(game.StartsAt, team.TimeZoneId!);
            var label = GameLabel.Render(game.Title, franchiseName, strings);
            var titleHtml = AnnouncementLink.Wrap(label, team.ChatId, game.AnnouncementMessageId);

            text.Append(
                    spansTeams
                        ? strings.Text(
                            "MySchedule.EntryWithTeam",
                            new
                            {
                                When = local,
                                Title = titleHtml,
                                Team = WebUtility.HtmlEncode(team.Name),
                            }
                        )
                        : strings.Text("MySchedule.Entry", new { When = local, Title = titleHtml })
                )
                .Append('\n')
                .Append(strings.Text("MySchedule.Venue", new { Venue = WebUtility.HtmlEncode(game.Venue) }))
                .Append('\n')
                .Append(StatusLine(placement, guestCount, strings))
                .Append("\n\n");
        }

        return text.ToString().TrimEnd();
    }

    // Four whole templates rather than a status with a "+n guests" fragment appended: user-
    // visible text is never concatenated (CLAUDE.md), and a locale that wants the guest count
    // in front of the status — or the whole sentence rebuilt around it — can say so.
    private static string StatusLine(SignupPlacement placement, int guestCount, IStringsFor strings) =>
        (placement.IsPlaying, guestCount > 0) switch
        {
            (true, false) => strings.Text("MySchedule.StatusPlaying"),
            (true, true) => strings.Text("MySchedule.StatusPlayingWithGuests", new { Guests = guestCount }),
            (false, false) => strings.Text("MySchedule.StatusReserve", new { Position = placement.Position }),
            (false, true) => strings.Text(
                "MySchedule.StatusReserveWithGuests",
                new { Position = placement.Position, Guests = guestCount }
            ),
        };
}
