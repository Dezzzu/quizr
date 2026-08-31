using System.Net;
using Quizr.Domain.Entities;

namespace Quizr.App.Rendering;

// A tg://user?id= inline mention — Telegram's text_mention entity, which exists precisely for
// members who have no username at all, and keeps resolving when someone changes theirs, since
// the id is the stable thing and the display name is only a label.
//
// This is a real mention, not decoration: Telegram notifies on a message being *sent*, and a
// mention reaches people who muted the group. Editing does not re-notify, so the announcement
// rewritten on every join and drop stays silent — the one loud path is
// MessageEditDebouncer's repost recovery, which sends a genuinely new message carrying the
// whole roster.
internal static class Mention
{
    public static string Of(Player player) =>
        $"""<a href="tg://user?id={player.TelegramUserId.Value}">{WebUtility.HtmlEncode(player.DisplayName)}</a>""";
}
