using System.Globalization;
using Quizr.Domain;

namespace Quizr.App.Rendering;

// A deep link back to the game's own announcement, for the two places that list games rather
// than being one: the Board and a person's own schedule. The Board could arguably get away
// without it — its entries sit in the same chat as the posts they name — but /myschedule is
// read in a DM, where the link is the only thing tying a line back to the game it describes.
internal static class AnnouncementLink
{
    // Telegram's t.me/c/<id>/<messageId> scheme only exists for supergroups and channels,
    // whose chat ids look like -100XXXXXXXXXX. A plain basic group has no message-link scheme
    // at all, so its entries render as bare text rather than as a broken link.
    public static string Wrap(string labelHtml, TelegramChatId chatId, TelegramMessageId? announcementMessageId)
    {
        var url = Build(chatId, announcementMessageId);

        return url is null ? labelHtml : $"""<a href="{url}">{labelHtml}</a>""";
    }

    // The bare URL, for somewhere that can't carry an anchor — a VEVENT's URL property and the
    // line of its description that points back at the chat. Null wherever Wrap would decline
    // to make a link, so the two can't disagree about which chats have one.
    public static string? Build(TelegramChatId chatId, TelegramMessageId? announcementMessageId)
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
