using AwesomeAssertions;
using Quizr.App.Localization;
using Telegram.Bot.Types.Enums;

namespace Quizr.App.Tests;

public class LocaleResolverTests
{
    [Test]
    public void InADmExplicitChoiceWinsOverEverythingElse()
    {
        LocaleResolver
            .Resolve(ChatType.Private, explicitChoice: "de", telegramLanguageCode: "ru", teamDefault: "en")
            .Should()
            .Be("de");
    }

    [Test]
    public void InADmFallsBackToTelegramLanguageWhenNoExplicitChoice()
    {
        LocaleResolver
            .Resolve(ChatType.Private, explicitChoice: null, telegramLanguageCode: "ru-RU", teamDefault: "en")
            .Should()
            .Be("ru");
    }

    [Test]
    public void InADmFallsBackToTeamDefaultWhenTelegramLanguageIsUnsupported()
    {
        LocaleResolver
            .Resolve(ChatType.Private, explicitChoice: null, telegramLanguageCode: "fr", teamDefault: "de")
            .Should()
            .Be("de");
    }

    [Test]
    public void InADmFallsBackToTeamDefaultWhenTelegramLanguageIsMissing()
    {
        LocaleResolver
            .Resolve(ChatType.Private, explicitChoice: null, telegramLanguageCode: null, teamDefault: "de")
            .Should()
            .Be("de");
    }

    // CLAUDE.md: "Group messages use the team's language" — full stop. A player's own
    // /mylanguage choice, and Telegram's own client language, both apply to DMs only; a
    // message read by the whole team can't honour one person's preference over what the
    // team chose.
    [Test]
    [Arguments(ChatType.Group)]
    [Arguments(ChatType.Supergroup)]
    public void InAGroupTheTeamsLanguageAlwaysWinsRegardlessOfPersonalOrTelegramLanguage(ChatType chatType)
    {
        LocaleResolver
            .Resolve(chatType, explicitChoice: "de", telegramLanguageCode: "fr", teamDefault: "ru")
            .Should()
            .Be("ru");
    }

    [Test]
    [Arguments("en", "en")]
    [Arguments("ru-RU", "ru")]
    [Arguments("DE", "de")]
    [Arguments("fr", null)]
    [Arguments(null, null)]
    public void MapsToSupportedLocaleOrNull(string? languageCode, string? expected)
    {
        LocaleResolver.MapToSupported(languageCode).Should().Be(expected);
    }

    [Test]
    [Arguments("en", true)]
    [Arguments("ru", true)]
    [Arguments("de", true)]
    [Arguments("fr", false)]
    [Arguments("ru-RU", false)]
    public void IsSupportedRequiresAnExactMatch(string code, bool expected)
    {
        LocaleResolver.IsSupported(code).Should().Be(expected);
    }
}
