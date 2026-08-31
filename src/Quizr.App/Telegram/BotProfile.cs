using Quizr.App.Localization;
using Telegram.Bot;
using Telegram.Bot.Requests;

namespace Quizr.App.Telegram;

// The bot's own shop window, registered once at startup alongside the command menu.
//
// In code rather than through BotFather for the reason CLAUDE.md makes localization
// first-class: both fields take a language_code, so this gives all three languages from the
// same strings table every other message uses, while BotFather's own flow would leave a
// trilingual bot with a single-language description. It also keeps the text reviewable in the
// repo instead of living in a chat nobody can diff.
//
// These are global properties of the bot, not per-chat: running against a token also used
// elsewhere overwrites that bot's text too, exactly as CommandMenu already does.
internal static class BotProfile
{
    public static async Task RegisterAsync(ITelegramBotClient bot, IStrings strings, CancellationToken ct)
    {
        foreach (var locale in LocaleResolver.All)
        {
            var text = strings.For(locale);

            // Shown in the chat with the bot while it's still empty — so this is aimed at
            // someone who hasn't pressed Start yet, and nobody already talking to the bot
            // will see a change to it.
            await bot.SendRequest(
                new SetMyDescriptionRequest { Description = text.Text("Bot.Description"), LanguageCode = locale },
                ct
            );

            // Shown on the profile page and sent along with the link whenever someone shares
            // the bot — which is how a new team first meets it.
            await bot.SendRequest(
                new SetMyShortDescriptionRequest
                {
                    ShortDescription = text.Text("Bot.ShortDescription"),
                    LanguageCode = locale,
                },
                ct
            );
        }
    }
}
