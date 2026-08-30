using AwesomeAssertions;
using Quizr.App.Localization;

namespace Quizr.App.Tests;

public class LocaleResolverTests
{
    [Test]
    public void ExplicitChoiceWinsOverEverythingElse()
    {
        LocaleResolver.Resolve(explicitChoice: "de", telegramLanguageCode: "ru", teamDefault: "en").Should().Be("de");
    }

    [Test]
    public void FallsBackToTelegramLanguageWhenNoExplicitChoice()
    {
        LocaleResolver
            .Resolve(explicitChoice: null, telegramLanguageCode: "ru-RU", teamDefault: "en")
            .Should()
            .Be("ru");
    }

    [Test]
    public void FallsBackToTeamDefaultWhenTelegramLanguageIsUnsupported()
    {
        LocaleResolver.Resolve(explicitChoice: null, telegramLanguageCode: "fr", teamDefault: "de").Should().Be("de");
    }

    [Test]
    public void FallsBackToTeamDefaultWhenTelegramLanguageIsMissing()
    {
        LocaleResolver.Resolve(explicitChoice: null, telegramLanguageCode: null, teamDefault: "de").Should().Be("de");
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
