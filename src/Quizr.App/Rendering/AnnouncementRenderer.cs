using System.Net;
using System.Text;
using Quizr.App.Localization;
using Quizr.App.Telegram;
using Quizr.App.Time;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Rendering;

// One function per message type, interpolated strings, no templating engine (STACK.md).
// HTML parse mode throughout (CLAUDE.md) — every piece of user-supplied text is encoded.
internal static class AnnouncementRenderer
{
    public static string RenderText(
        Game game,
        RosterSplit roster,
        IReadOnlyDictionary<PlayerId, Player> players,
        string teamTimeZoneId,
        IStringsFor strings
    )
    {
        var local = TeamTime.ConvertToLocal(game.StartsAt, teamTimeZoneId);
        var text = new StringBuilder();

        text.Append("<b>").Append(WebUtility.HtmlEncode(game.Title)).Append("</b>\n");
        text.Append(strings.Text("Announcement.Venue", new { Venue = WebUtility.HtmlEncode(game.Venue) })).Append('\n');
        // The format spec lives in the template, not here — SmartFormat applies it through
        // IFormattable with the team's locale, so day and month names come out localized
        // (e.g. "Fri"/"пт"/"Fr") instead of fixed to one culture. A literal ':' in a .NET
        // format string has to be escaped as '\:', since SmartFormat also uses ':' as its
        // own placeholder-to-format delimiter.
        text.Append(strings.Text("Announcement.When", new { When = local })).Append('\n');

        if (game.Price is { } price)
        {
            text.Append(strings.Text("Announcement.Price", new { Price = price })).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(game.Notes))
        {
            text.Append(WebUtility.HtmlEncode(game.Notes)).Append('\n');
        }

        text.Append('\n');
        text.Append(
            strings.Text("Announcement.PlayingHeader", new { Count = roster.Playing.Count, Capacity = game.Capacity })
        );
        text.Append('\n');
        AppendRoster(text, roster.Playing, players, strings);

        if (roster.Reserve.Count > 0)
        {
            text.Append('\n');
            text.Append(strings.Text("Announcement.ReserveHeader", new { Count = roster.Reserve.Count }));
            text.Append('\n');
            AppendRoster(text, roster.Reserve, players, strings);
        }

        return text.ToString().TrimEnd();
    }

    public static InlineKeyboardMarkup RenderKeyboard(GameId gameId, IStringsFor strings) =>
        new([
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.JoinButton"),
                    CallbackData.Format(CallbackData.Join, gameId)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.GuestButton"),
                    CallbackData.Format(CallbackData.Guest, gameId)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.DropButton"),
                    CallbackData.Format(CallbackData.Drop, gameId)
                ),
            ],
        ]);

    private static void AppendRoster(
        StringBuilder text,
        IReadOnlyList<Signup> signups,
        IReadOnlyDictionary<PlayerId, Player> players,
        IStringsFor strings
    )
    {
        if (signups.Count == 0)
        {
            text.Append(strings.Text("Announcement.NoOneYet")).Append('\n');
            return;
        }

        for (var i = 0; i < signups.Count; i++)
        {
            text.Append(i + 1).Append(". ").Append(NameOf(signups[i], players, strings)).Append('\n');
        }
    }

    private static string NameOf(Signup signup, IReadOnlyDictionary<PlayerId, Player> players, IStringsFor strings)
    {
        if (signup.PlayerId is { } playerId)
        {
            return WebUtility.HtmlEncode(players[playerId].DisplayName);
        }

        if (signup.GuestName is { } guestName)
        {
            var encodedName = WebUtility.HtmlEncode(guestName);

            return signup.InvitedByPlayerId is { } namedInviterId
                ? strings.Text(
                    "Announcement.NamedGuest",
                    new { Name = encodedName, Inviter = WebUtility.HtmlEncode(players[namedInviterId].DisplayName) }
                )
                : strings.Text("Announcement.TeamGuest", new { Name = encodedName });
        }

        // Invariant 5: an unnamed guest never survives without an inviter, so this always
        // has one.
        var inviterId = signup.InvitedByPlayerId!.Value;
        return strings.Text(
            "Announcement.AnonymousGuest",
            new { Inviter = WebUtility.HtmlEncode(players[inviterId].DisplayName) }
        );
    }
}
