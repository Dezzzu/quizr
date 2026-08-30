using System.Net;
using System.Text;
using Quizr.App.Localization;
using Quizr.App.Telegram;
using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Rendering;

// A finished game's post-game view (design decision #4): every Participation row with
// Played/Attended toggles, replacing the separate Mark absent · Add player · Not played
// buttons the plan started from — one view, edited in place on every toggle.
internal static class RosterManagementRenderer
{
    public static string RenderText(Game game, IReadOnlyList<Participation> participations, IStringsFor strings)
    {
        var text = new StringBuilder();
        text.Append(strings.Text("Roster.Header", new { Title = WebUtility.HtmlEncode(game.Title) })).Append('\n');

        if (participations.Count == 0)
        {
            text.Append(strings.Text("Roster.Empty"));
            return text.ToString();
        }

        for (var i = 0; i < participations.Count; i++)
        {
            var p = participations[i];
            text.Append(i + 1).Append(". ").Append(WebUtility.HtmlEncode(NameOf(p))).Append(" — ");
            text.Append(p.Played ? strings.Text("Roster.Played") : strings.Text("Roster.NotPlayed")).Append(", ");
            text.Append(p.Attended ? strings.Text("Roster.Attended") : strings.Text("Roster.Absent"));
            text.Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    public static InlineKeyboardMarkup RenderKeyboard(
        Game game,
        IReadOnlyList<Participation> participations,
        IStringsFor strings
    )
    {
        var rows = new List<IEnumerable<InlineKeyboardButton>>();

        foreach (var p in participations)
        {
            var name = NameOf(p);
            rows.Add([
                InlineKeyboardButton.WithCallbackData(
                    strings.Text(p.Played ? "Roster.TogglePlayedOn" : "Roster.TogglePlayedOff", new { Name = name }),
                    CallbackData.Format(CallbackData.TogglePlayed, p.Id)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text(
                        p.Attended ? "Roster.ToggleAttendedOn" : "Roster.ToggleAttendedOff",
                        new { Name = name }
                    ),
                    CallbackData.Format(CallbackData.ToggleAttended, p.Id)
                ),
            ]);
        }

        rows.Add([
            InlineKeyboardButton.WithCallbackData(
                strings.Text("Roster.AddPlayerButton"),
                CallbackData.Format(CallbackData.AddPlayer, game.Id)
            ),
        ]);
        rows.Add(DoneButton.Row(strings));

        return new InlineKeyboardMarkup(rows);
    }

    // A finished game's Participation rows carry their own Name for guests/venue-assigned;
    // Player is only loaded for members.
    private static string NameOf(Participation participation) =>
        participation.IsMember ? participation.Player!.DisplayName : participation.Name ?? "?";
}
