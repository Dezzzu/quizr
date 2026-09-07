using System.Text.RegularExpressions;
using AwesomeAssertions;
using Quizr.App.Localization;

namespace Quizr.App.Tests;

public partial class StringsTests
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

    // Telegram caps setMyDescription at 512 characters and setMyShortDescription at 120, and
    // BotProfile sends one of each per language at startup — so a translation that runs long
    // takes the bot down on boot against the live API, which is the worst place to learn it.
    // Checked per locale because the same sentence is a different length in each.
    // Key parity says every locale has every key. This says every locale's template asks for
    // the same *arguments* — a translation that renamed {Url} or dropped {Position} passes
    // parity and then throws the first time somebody reading that language runs the command.
    // Nothing else would catch it: the renderers are only ever exercised in English.
    [Test]
    public void EveryLocaleUsesTheSamePlaceholdersForAKeyAsEnglishDoes()
    {
        var byLocale = Strings.LoadAll();
        var english = byLocale["en"];

        foreach (var (locale, templates) in byLocale.Where(l => l.Key != "en"))
        {
            foreach (var (key, template) in templates)
            {
                Placeholders(template)
                    .Should()
                    .BeEquivalentTo(
                        Placeholders(english[key]),
                        $"'{key}' in '{locale}' must take the same arguments as the English template"
                    );
            }
        }
    }

    // The leading identifier of each {Name...} — SmartFormat's own colon-separated options
    // (a date format, a plural list) are the translator's business and deliberately not
    // compared. Doubled braces are literal and stripped first so they can't read as one.
    private static HashSet<string> Placeholders(string template) =>
        PlaceholderPattern()
            .Matches(template.Replace("{{", string.Empty, StringComparison.Ordinal))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex PlaceholderPattern();

    [Test]
    public void TheBotProfileTextFitsTelegramsLimitsInEveryLocale()
    {
        foreach (var (locale, templates) in Strings.LoadAll())
        {
            templates["Bot.Description"]
                .Length.Should()
                .BeLessThanOrEqualTo(512, $"'{locale}.json' Bot.Description is sent to setMyDescription");
            templates["Bot.ShortDescription"]
                .Length.Should()
                .BeLessThanOrEqualTo(120, $"'{locale}.json' Bot.ShortDescription is sent to setMyShortDescription");
        }
    }
}
