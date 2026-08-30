using System.Net;
using System.Text;
using Quizr.App.Localization;
using Quizr.App.Telegram;
using Quizr.App.Time;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Rendering;

// One function per message type, interpolated strings, no templating engine (STACK.md).
// HTML parse mode throughout (CLAUDE.md) — every piece of user-supplied text is encoded.
internal static class AnnouncementRenderer
{
    public static string RenderText(Game game, RosterSplit roster, string teamTimeZoneId, IStringsFor strings)
    {
        var local = TeamTime.ConvertToLocal(game.StartsAt, teamTimeZoneId);
        var text = new StringBuilder();

        text.Append("<b>").Append(WebUtility.HtmlEncode(game.Title)).Append("</b>\n");

        if (game.IsFinished)
        {
            text.Append(strings.Text("Announcement.Finished")).Append('\n');
        }
        else if (game.IsDeclined)
        {
            text.Append(strings.Text("Announcement.Declined")).Append('\n');
        }

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

        if (game.Tags.Count > 0)
        {
            text.Append(string.Join(' ', game.Tags.Select(ToHashtag))).Append('\n');
        }

        text.Append('\n');
        text.Append(
            strings.Text("Announcement.PlayingHeader", new { Count = roster.Playing.Count, Capacity = game.Capacity })
        );
        text.Append('\n');
        AppendRoster(text, roster.Playing, strings);

        if (roster.Reserve.Count > 0)
        {
            text.Append('\n');
            text.Append(strings.Text("Announcement.ReserveHeader", new { Count = roster.Reserve.Count }));
            text.Append('\n');
            AppendRoster(text, roster.Reserve, strings);
        }

        return text.ToString().TrimEnd();
    }

    // Self-serve buttons before a game finishes or is declined (invariant 8); after, just the
    // captain-only door into the Manage roster view (design decision #4) replaces them, or
    // nothing at all for a declined game — there's no roster to manage, but it stays visible
    // for the record (invariant 7).
    public static InlineKeyboardMarkup RenderKeyboard(Game game, IStringsFor strings)
    {
        var gameId = game.Id;

        if (game.IsDeclined)
        {
            return new InlineKeyboardMarkup(new List<IEnumerable<InlineKeyboardButton>>());
        }

        if (game.IsFinished)
        {
            return new([
                [
                    InlineKeyboardButton.WithCallbackData(
                        strings.Text("Announcement.ManageRosterButton"),
                        CallbackData.Format(CallbackData.ManageRoster, gameId)
                    ),
                ],
            ]);
        }

        return new([
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
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.MyGuestsButton"),
                    CallbackData.Format(CallbackData.MyGuests, gameId)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.DropButton"),
                    CallbackData.Format(CallbackData.Drop, gameId)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.NudgeButton"),
                    CallbackData.Format(CallbackData.Nudge, gameId)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.ManagePlayersButton"),
                    CallbackData.Format(CallbackData.ManagePlayers, gameId)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.ManageGuestsButton"),
                    CallbackData.Format(CallbackData.ManageGuests, gameId)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.FinishButton"),
                    CallbackData.Format(CallbackData.FinishGame, gameId)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.DeclineButton"),
                    CallbackData.Format(CallbackData.DeclineGame, gameId)
                ),
            ],
        ]);
    }

    // A real, clickable Telegram hashtag — internal whitespace becomes '_' so it stays one
    // token. This is deliberately the interim archive: Telegram's own in-chat search already
    // finds every message with a given hashtag, finished and declined games included, with no
    // bot command needed until the mini app's archive lands.
    private static string ToHashtag(string tag) => "#" + WebUtility.HtmlEncode(tag.Trim().Replace(' ', '_'));

    private static void AppendRoster(StringBuilder text, IReadOnlyList<Signup> signups, IStringsFor strings)
    {
        if (signups.Count == 0)
        {
            text.Append(strings.Text("Announcement.NoOneYet")).Append('\n');
            return;
        }

        for (var i = 0; i < signups.Count; i++)
        {
            text.Append(i + 1).Append(". ").Append(NameOf(signups[i], strings)).Append('\n');
        }
    }

    // IsMember/IsGuest read from PlayerId — the stored fact — never from whether the
    // Player/InvitedByPlayer navigation happens to be populated. A signup whose Player wasn't
    // Included would otherwise silently read as an anonymous guest instead of a missing load.
    private static string NameOf(Signup signup, IStringsFor strings)
    {
        if (signup.IsMember)
        {
            return WebUtility.HtmlEncode(signup.Player!.DisplayName);
        }

        if (signup.GuestName is { } guestName)
        {
            var encodedName = WebUtility.HtmlEncode(guestName);

            return signup.HasInviter
                ? strings.Text(
                    "Announcement.NamedGuest",
                    new { Name = encodedName, Inviter = WebUtility.HtmlEncode(signup.InvitedByPlayer!.DisplayName) }
                )
                : strings.Text("Announcement.TeamGuest", new { Name = encodedName });
        }

        // Invariant 5: an unnamed guest never survives without an inviter, so this always
        // has one.
        return strings.Text(
            "Announcement.AnonymousGuest",
            new { Inviter = WebUtility.HtmlEncode(signup.InvitedByPlayer!.DisplayName) }
        );
    }
}
