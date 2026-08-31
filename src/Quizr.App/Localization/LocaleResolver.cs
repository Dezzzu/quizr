using Telegram.Bot.Types.Enums;

namespace Quizr.App.Localization;

// CLAUDE.md: "Group messages use the team's language; DMs and the app use the person's own."
// A command like /start or /help is reachable from either, so the resolution order itself
// depends on where it ran — in a DM there's no team to defer to as a shared default, and a
// group message read by everyone can't honour one person's own preference over what the team
// chose. One place so the split can't drift between call sites, or silently vanish back into
// a single order that happens to look right in a DM and wrong for everyone else in a group.
internal static class LocaleResolver
{
    // CLAUDE.md's three first-class locales, in the order they're offered. Anything that has to
    // do something once per language — the command menu, the bot's own profile text — reads
    // this rather than keeping a copy that can drift out of step with what's supported.
    public static readonly string[] All = ["en", "ru", "de"];

    private static readonly HashSet<string> Supported = [.. All];

    public static string Resolve(
        ChatType chatType,
        string? explicitChoice,
        string? telegramLanguageCode,
        string teamDefault
    ) =>
        chatType == ChatType.Private
            ? explicitChoice ?? MapToSupported(telegramLanguageCode) ?? teamDefault
            : teamDefault;

    // For validating a captain's /setlanguage or a person's /mylanguage argument — exact
    // match only, unlike MapToSupported's "ru-RU" -> "ru" leniency for Telegram's own codes.
    public static bool IsSupported(string code) => Supported.Contains(code);

    // "ru-RU" -> "ru"; anything not in {en, ru, de} maps to null so the caller falls
    // through to the next step in the chain instead of storing an unsupported locale.
    public static string? MapToSupported(string? languageCode)
    {
        if (string.IsNullOrEmpty(languageCode))
        {
            return null;
        }

        var baseLanguage = languageCode.Split('-')[0].ToLowerInvariant();
        return Supported.Contains(baseLanguage) ? baseLanguage : null;
    }
}
