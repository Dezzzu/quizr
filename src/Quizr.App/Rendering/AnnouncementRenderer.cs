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
    // franchiseName defaults to null because that is what a one-off game has. For a franchise
    // game it comes from AnnouncementService, which looks it up rather than reading it off the
    // Game — see its own comment on why the navigation can't be trusted here.
    public static string RenderText(
        Game game,
        RosterSplit roster,
        string teamTimeZoneId,
        IStringsFor strings,
        string? franchiseName = null
    )
    {
        var local = TeamTime.ConvertToLocal(game.StartsAt, teamTimeZoneId);
        var text = new StringBuilder();

        text.Append("<b>").Append(GameLabel.Render(game.Title, franchiseName, strings)).Append("</b>\n");

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

    // What everybody can do, plus one door to what only a captain can. Telegram shows a
    // message's keyboard identically to every member — there is no per-viewer variant — so the
    // captain actions sit behind Manage and open privately, rather than five buttons that
    // refuse most of the team. Nudge stays out here with the self-serve ones: anyone waiting
    // on a late player can chase them, and GameService's own cooldown is what stops it being
    // used as a bludgeon.
    //
    // A declined game keeps no buttons at all — there's no roster left to act on, but it stays
    // visible for the record (invariant 7).
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
                [ManageButton(gameId, strings)],
            ]);
        }

        return new([
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.JoinButton"),
                    CallbackData.Format(CallbackData.Join, gameId)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.DropButton"),
                    CallbackData.Format(CallbackData.Drop, gameId)
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
                    strings.Text("Announcement.NudgeButton"),
                    CallbackData.Format(CallbackData.Nudge, gameId)
                ),
                ManageButton(gameId, strings),
            ],
        ]);
    }

    private static InlineKeyboardButton ManageButton(GameId gameId, IStringsFor strings) =>
        InlineKeyboardButton.WithCallbackData(
            strings.Text("Announcement.ManageButton"),
            CallbackData.Format(CallbackData.Manage, gameId)
        );

    // What opens behind that door, private to the captain who tapped it. A finished game has
    // only its roster left to edit (invariant 11); a live one has everything else.
    public static InlineKeyboardMarkup RenderManagePanel(Game game, IStringsFor strings)
    {
        var gameId = game.Id;
        var rows = new List<IEnumerable<InlineKeyboardButton>>();

        if (game.IsFinished)
        {
            rows.Add([
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.ManageRosterButton"),
                    CallbackData.Format(CallbackData.ManageRoster, gameId)
                ),
            ]);
        }
        else
        {
            rows.Add([
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.ManagePlayersButton"),
                    CallbackData.Format(CallbackData.ManagePlayers, gameId)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.ManageGuestsButton"),
                    CallbackData.Format(CallbackData.ManageGuests, gameId)
                ),
            ]);
            // The same door /editgame opens, minus its pick-a-game step: the panel already
            // knows which game it belongs to. Live games only — after a finish, invariant 11
            // says the thing a captain edits is the participation rows, which is what the
            // Manage roster button above leads to instead.
            rows.Add([
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.EditGameButton"),
                    CallbackData.Format(CallbackData.PickGameToEdit, gameId)
                ),
            ]);
            rows.Add([
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.FinishButton"),
                    CallbackData.Format(CallbackData.FinishGame, gameId)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Announcement.DeclineButton"),
                    CallbackData.Format(CallbackData.DeclineGame, gameId)
                ),
            ]);
        }

        rows.Add(DoneButton.Row(strings));
        return new InlineKeyboardMarkup(rows);
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
    //
    // Every real person here is a mention rather than plain text, so two members who share a
    // display name are still tellable apart by tapping through to the profile. Guests are not:
    // a guest has no Telegram account to point at, only whoever brought them.
    private static string NameOf(Signup signup, IStringsFor strings)
    {
        if (signup.IsMember)
        {
            return Mention.Of(signup.Player!);
        }

        if (signup.GuestName is { } guestName)
        {
            var encodedName = WebUtility.HtmlEncode(guestName);

            return signup.HasInviter
                ? strings.Text(
                    "Announcement.NamedGuest",
                    new { Name = encodedName, Inviter = Mention.Of(signup.InvitedByPlayer!) }
                )
                : strings.Text("Announcement.TeamGuest", new { Name = encodedName });
        }

        // Invariant 5: an unnamed guest never survives without an inviter, so this always
        // has one.
        return strings.Text("Announcement.AnonymousGuest", new { Inviter = Mention.Of(signup.InvitedByPlayer!) });
    }
}
