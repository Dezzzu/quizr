using System.Globalization;
using System.Net;
using System.Text;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.App.Time;
using Quizr.Domain;

namespace Quizr.App.Rendering;

// The one pinned message per chat (CLAUDE.md invariant 12) — upcoming games, date-ordered,
// each linking to its announcement and showing how full it is. One function, interpolated
// strings (STACK.md).
internal static class BoardRenderer
{
    public static string RenderText(
        IReadOnlyList<BoardEntry> upcomingByStartDate,
        TelegramChatId chatId,
        string teamTimeZoneId,
        IStringsFor strings
    )
    {
        var text = new StringBuilder();
        text.Append(strings.Text("Board.Header")).Append("\n\n");

        if (upcomingByStartDate.Count == 0)
        {
            text.Append(strings.Text("Board.NoGames"));
            return text.ToString();
        }

        foreach (var (game, playing, reserve, franchiseName) in upcomingByStartDate)
        {
            var local = TeamTime.ConvertToLocal(game.StartsAt, teamTimeZoneId);
            var label = Label(game.Title, franchiseName, strings);
            var titleHtml = MessageLink(chatId, game.AnnouncementMessageId) is { } link
                ? $"""<a href="{link}">{label}</a>"""
                : label;

            // Two whole templates rather than one plus a "+n" fragment appended to it: user-
            // visible text is never concatenated (CLAUDE.md), so a locale that wants the queue
            // written differently — or somewhere else in the line — can say so.
            text.Append(
                    strings.Text(
                        reserve > 0 ? "Board.EntryWithReserve" : "Board.Entry",
                        new
                        {
                            When = local,
                            Title = titleHtml,
                            Playing = playing,
                            Capacity = game.Capacity,
                            Reserve = reserve,
                        }
                    )
                )
                .Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    // A game keeps whatever title it was given, and a captain who renames one — on the confirm
    // screen or later through /editgame — usually drops the franchise out of it: "Halloween
    // special", not "Квиз, плиз! #12". That reads fine on the announcement, where the franchise
    // is obvious from context, and badly on the Board, where a dozen entries from three
    // franchises sit in one list. So the Board puts the brand back in front of a title that
    // doesn't already carry it.
    //
    // Contains rather than StartsWith: the point is not to say the name twice, and a title like
    // "Осенний Квиз, плиз!" already says it. Both halves are encoded before the template joins
    // them, since the result is spliced into an <a> below and user-visible text is never
    // concatenated (CLAUDE.md).
    private static string Label(string title, string? franchiseName, IStringsFor strings)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        if (franchiseName is null || title.Contains(franchiseName, StringComparison.OrdinalIgnoreCase))
        {
            return encodedTitle;
        }

        return strings.Text(
            "Board.TitleWithFranchise",
            new { Franchise = WebUtility.HtmlEncode(franchiseName), Title = encodedTitle }
        );
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
