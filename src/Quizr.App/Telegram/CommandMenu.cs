using Quizr.App.Localization;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Quizr.App.Telegram;

// Registers Telegram's native "/" suggestion menu (setMyCommands) once at startup. Purely a
// UX convenience, not an authorization check — captaincy granted through /managecaptains
// without Telegram admin status still works when typed by hand; BotCommandScopeAllChatAdministrators
// is just the closest native match Telegram offers for "captain," so that's who gets the
// suggestions. Scopes don't merge (the most specific one replaces, not extends, the default),
// so the admin list repeats everyone's commands rather than only the captain-only ones.
//
// EveryoneCommands/CaptainOnlyCommands are the one place the command list and its
// descriptions are named — UpdateRouter's /help reads the same two lists, so there's nowhere
// else a new command needs registering and nothing for the two views to drift out of sync
// on.
internal static class CommandMenu
{
    public static readonly (string Command, string DescriptionKey)[] EveryoneCommands =
    [
        ("help", "Commands.Help"),
        ("cancel", "Commands.Cancel"),
        ("mylanguage", "Commands.MyLanguage"),
        ("myreminders", "Commands.MyReminders"),
    ];

    public static readonly (string Command, string DescriptionKey)[] CaptainOnlyCommands =
    [
        ("settimezone", "Commands.SetTimeZone"),
        ("setlanguage", "Commands.SetLanguage"),
        ("setreminders", "Commands.SetReminders"),
        ("newgame", "Commands.NewGame"),
        ("newfranchise", "Commands.NewFranchise"),
        ("editfranchise", "Commands.EditFranchise"),
        ("editgame", "Commands.EditGame"),
        ("managecaptains", "Commands.ManageCaptains"),
        ("restoreannouncements", "Commands.RestoreAnnouncements"),
    ];

    public static async Task RegisterAsync(ITelegramBotClient bot, IStrings strings, CancellationToken ct)
    {
        foreach (var locale in LocaleResolver.All)
        {
            var everyone = ToBotCommands(EveryoneCommands, strings, locale);
            var everyoneAndCaptains = ToBotCommands([.. EveryoneCommands, .. CaptainOnlyCommands], strings, locale);

            await bot.SendRequest(
                new SetMyCommandsRequest
                {
                    Commands = everyone,
                    Scope = new BotCommandScopeAllPrivateChats(),
                    LanguageCode = locale,
                },
                ct
            );
            await bot.SendRequest(
                new SetMyCommandsRequest
                {
                    Commands = everyone,
                    Scope = new BotCommandScopeAllGroupChats(),
                    LanguageCode = locale,
                },
                ct
            );
            await bot.SendRequest(
                new SetMyCommandsRequest
                {
                    Commands = everyoneAndCaptains,
                    Scope = new BotCommandScopeAllChatAdministrators(),
                    LanguageCode = locale,
                },
                ct
            );
        }
    }

    private static BotCommand[] ToBotCommands(
        IEnumerable<(string Command, string DescriptionKey)> commands,
        IStrings strings,
        string locale
    ) =>
        commands
            .Select(c => new BotCommand
            {
                Command = c.Command,
                Description = strings.For(locale).Text(c.DescriptionKey),
            })
            .ToArray();
}
