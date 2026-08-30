using AwesomeAssertions;
using Quizr.App.Localization;

namespace Quizr.App.Tests;

public class LocaleResolverTests
{
    [Fact]
    public void ExplicitChoiceWinsOverEverythingElse()
    {
        LocaleResolver.Resolve(explicitChoice: "de", telegramLanguageCode: "ru", teamDefault: "en").Should().Be("de");
    }

    [Fact]
    public void FallsBackToTelegramLanguageWhenNoExplicitChoice()
    {
        LocaleResolver
            .Resolve(explicitChoice: null, telegramLanguageCode: "ru-RU", teamDefault: "en")
            .Should()
            .Be("ru");
    }

    [Fact]
    public void FallsBackToTeamDefaultWhenTelegramLanguageIsUnsupported()
    {
        LocaleResolver.Resolve(explicitChoice: null, telegramLanguageCode: "fr", teamDefault: "de").Should().Be("de");
    }

    [Fact]
    public void FallsBackToTeamDefaultWhenTelegramLanguageIsMissing()
    {
        LocaleResolver.Resolve(explicitChoice: null, telegramLanguageCode: null, teamDefault: "de").Should().Be("de");
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("ru-RU", "ru")]
    [InlineData("DE", "de")]
    [InlineData("fr", null)]
    [InlineData(null, null)]
    public void MapsToSupportedLocaleOrNull(string? languageCode, string? expected)
    {
        LocaleResolver.MapToSupported(languageCode).Should().Be(expected);
    }
}
