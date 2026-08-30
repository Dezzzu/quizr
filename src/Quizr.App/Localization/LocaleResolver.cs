namespace Quizr.App.Localization;

// CLAUDE.md's resolution order: explicit user choice -> Telegram language_code ->
// team default -> English. One place so it can't drift between call sites.
internal static class LocaleResolver
{
    private static readonly HashSet<string> Supported = ["en", "ru", "de"];

    public static string Resolve(string? explicitChoice, string? telegramLanguageCode, string teamDefault) =>
        explicitChoice ?? MapToSupported(telegramLanguageCode) ?? teamDefault;

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
