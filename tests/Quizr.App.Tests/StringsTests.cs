using AwesomeAssertions;
using Quizr.App.Localization;

namespace Quizr.App.Tests;

public class StringsTests
{
    private readonly Strings _strings = new();

    [Test]
    public void RendersAKeyWithNoPlaceholders()
    {
        _strings.For("en").Text("Start.Greeting").Should().NotBeNullOrEmpty();
    }

    [Test]
    public void InterpolatesArguments()
    {
        var text = _strings.For("en").Text("Setup.TimeZoneSet", new { TimeZoneId = "Europe/Berlin" });

        text.Should().Contain("Europe/Berlin");
    }

    [Test]
    public void FallsBackToEnglishForALocaleWithNoFileOfItsOwn()
    {
        // "fr" isn't one of CLAUDE.md's three first-class locales, so it has no file — unlike
        // "ru", which does since M8 and renders its own text, not English's.
        var french = _strings.For("fr");
        var english = _strings.For("en");

        french.Text("Start.Greeting").Should().Be(english.Text("Start.Greeting"));
    }

    [Test]
    public void ReportsTheRequestedLocaleEvenWhenFallingBackToEnglishTemplates()
    {
        _strings.For("fr").Locale.Should().Be("fr");
    }

    [Test]
    public void RussianAndGermanRenderTheirOwnText()
    {
        var russian = _strings.For("ru").Text("Start.Greeting");
        var german = _strings.For("de").Text("Start.Greeting");
        var english = _strings.For("en").Text("Start.Greeting");

        russian.Should().NotBe(english);
        german.Should().NotBe(english);
    }

    [Test]
    public void EveryKeyPresentInEnglishIsPresentInEveryOtherLoadedLocale()
    {
        // CLAUDE.md: "Test key parity — every key present in every locale file." Compares the
        // actual loaded key sets rather than a hand-maintained list, so a translator adding or
        // renaming a key in one file without the others fails here, not in production.
        var byLocale = Strings.LoadAll();
        var englishKeys = byLocale["en"].Keys.ToHashSet();

        foreach (var (locale, templates) in byLocale)
        {
            if (locale == "en")
            {
                continue;
            }

            var localeKeys = templates.Keys.ToHashSet();
            localeKeys.Should().BeEquivalentTo(englishKeys, $"'{locale}.json' should have exactly en.json's keys");
        }
    }
}
