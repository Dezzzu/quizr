using System.Globalization;
using System.Net;
using System.Text;
using Quizr.App.Localization;
using Quizr.App.Time;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Rendering;

// The one pinned message per chat (CLAUDE.md invariant 12) — upcoming games, date-ordered,
// each linking to its announcement. One function, interpolated strings (STACK.md).
internal static class BoardRenderer
{
    public static string RenderText(
        IReadOnlyList<Game> upcomingGamesByStartDate,
        TelegramChatId chatId,
        string teamTimeZoneId,
        IStringsFor strings
    )
    {
        var text = new StringBuilder();
        text.Append(strings.Text("Board.Header")).Append("\n\n");

        if (upcomingGamesByStartDate.Count == 0)
        {
            text.Append(strings.Text("Board.NoGames"));
            return text.ToString();
        }

        foreach (var game in upcomingGamesByStartDate)
        {
            var local = TeamTime.ConvertToLocal(game.StartsAt, teamTimeZoneId);
            var titleHtml = MessageLink(chatId, game.AnnouncementMessageId) is { } link
                ? $"""<a href="{link}">{WebUtility.HtmlEncode(game.Title)}</a>"""
                : WebUtility.HtmlEncode(game.Title);

            text.Append(strings.Text("Board.Entry", new { When = local, Title = titleHtml })).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    // Telegram's t.me/c/<id>/<messageId> link scheme only exists for supergroups and
    // channels, whose chat ids look like -100XXXXXXXXXX. A plain basic group has no
    // message-link scheme at all, so those entries render without a link rather than a
    // broken one.
    private static string? MessageLink(TelegramChatId chatId, TelegramMessageId? announcementMessageId)
    {
        if (announcementMessageId is not { } messageId)
        {
            return null;
        }

        var raw = chatId.Value.ToString(CultureInfo.InvariantCulture);
        if (!raw.StartsWith("-100", StringComparison.Ordinal))
        {
            return null;
        }

        return $"https://t.me/c/{raw[4..]}/{messageId.Value}";
    }
}
