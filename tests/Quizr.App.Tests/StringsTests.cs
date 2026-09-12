using System.Globalization;
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

    // Telegram clips an inline button's label at the width of the row it sits in, and a row
    // divides its width evenly between its buttons — so a label's budget belongs to the row,
    // not to the label. Players reported the two ways that bites: "👥 Управление гостями" and
    // "👤 Управление игроками" both arriving as "Управление…", and "🏁 Завершить сейчас"
    // arriving as "🏁 Завершить", which reads like the close button under it. Hence two rules,
    // one enforced here and one that can only be read: a label must fit its row, and two
    // labels in the same keyboard must differ before the clip, not after it.
    //
    // The widths are generous on purpose. This catches a translation that runs away, not a
    // label two characters over what one particular phone shows.
    private const int HalfRowWidth = 16;
    private const int FullRowWidth = 26;

    // Every button that shares its row with another one, by the keyboard it belongs to:
    // AnnouncementRenderer.RenderKeyboard and .RenderManagePanel, the guest-keep prompt and
    // the drop/decline confirmations in UpdateRouter, RenderEditGameFieldPicker,
    // GameConfirmRenderer, FranchiseRenderer.RenderFieldPicker, the Nudge picker, the calendar
    // view, and SkipButton.KeyboardWithCancel. Hand-maintained: moving a button to a row of
    // its own means deleting it from here, and the compiler can't say so.
    private static readonly HashSet<string> PairedButtons =
    [
        "Announcement.JoinButton",
        "Announcement.DropButton",
        "Announcement.GuestButton",
        "Announcement.MyGuestsButton",
        "Announcement.NudgeButton",
        "Announcement.ManageButton",
        "Announcement.ManagePlayersButton",
        "Announcement.ManageGuestsButton",
        "Announcement.FinishButton",
        "Announcement.DeclineButton",
        "Guest.KeepButton",
        "Guest.RemoveButton",
        "Decline.ConfirmYes",
        "Decline.ConfirmNo",
        "Drop.ConfirmYes",
        "Drop.ConfirmNo",
        "EditGame.EditTitleButton",
        "EditGame.EditVenueButton",
        "EditGame.EditCapacityButton",
        "EditGame.EditPriceButton",
        "EditGame.EditNotesButton",
        "EditGame.EditStartTimeButton",
        "NewGame.EditVenueButton",
        "NewGame.EditCapacityButton",
        "NewGame.EditPriceButton",
        "NewGame.EditNotesButton",
        "NewGame.ConfirmButton",
        "Franchise.EditNameButton",
        "Franchise.EditVenueButton",
        "Franchise.EditCapacityButton",
        "Franchise.EditPriceButton",
        "Nudge.SendButton",
        "Calendar.ReplaceButton",
        "Calendar.TurnOffButton",
        "Common.CancelButton",
        "Common.SkipButton",
    ];

    // The keys that label a button without saying so in their name.
    private static readonly HashSet<string> UnnamedButtons =
    [
        "Decline.ConfirmYes",
        "Decline.ConfirmNo",
        "Drop.ConfirmYes",
        "Drop.ConfirmNo",
        "Roster.TogglePlayedOn",
        "Roster.TogglePlayedOff",
        "Reminders.ReserveOn",
        "Reminders.ReserveOff",
    ];

    [Test]
    public void EveryButtonLabelFitsTheRowItIsRenderedOn()
    {
        foreach (var (locale, templates) in Strings.LoadAll())
        {
            foreach (var (key, template) in templates.Where(t => IsButton(t.Key)))
            {
                var shared = PairedButtons.Contains(key);

                Width(template)
                    .Should()
                    .BeLessThanOrEqualTo(
                        shared ? HalfRowWidth : FullRowWidth,
                        $"'{key}' in '{locale}.json' is rendered on a {(shared ? "shared" : "full-width")} row"
                    );
            }
        }
    }

    // The reminder rows carry their current setting in a placeholder, so the label on its own
    // says nothing about whether the row fits — the longest channel name decides. This is the
    // check that caught Russian's "Утром в день игры: в личных сообщениях", where what got
    // clipped was the setting the row exists to show.
    [Test]
    public void AReminderRowFitsWithTheLongestChannelItCanShow()
    {
        string[] channels = ["Reminders.ChannelOff", "Reminders.ChannelGroup", "Reminders.ChannelDm"];
        string[] rows = ["Reminders.EveningBeforeButton", "Reminders.MorningOfButton", "Reminders.BeforeStartButton"];

        foreach (var (locale, templates) in Strings.LoadAll())
        {
            var longest = channels.Select(key => templates[key]).MaxBy(Width)!;

            foreach (var row in rows)
            {
                Width(_strings.For(locale).Text(row, new { Channel = longest }))
                    .Should()
                    .BeLessThanOrEqualTo(FullRowWidth, $"'{row}' in '{locale}.json' can read '{longest}'");
            }
        }
    }

    private static bool IsButton(string key) =>
        key.EndsWith("Button", StringComparison.Ordinal) || UnnamedButtons.Contains(key);

    // An emoji occupies roughly two of the characters a label is otherwise made of, in both
    // alphabets these locales use. Placeholders are left out: a label can't budget for a name
    // somebody chose, which is exactly why the fixed part has to come first.
    private static int Width(string template)
    {
        var fixedPart = PlaceholderBlockPattern().Replace(template, string.Empty);
        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(fixedPart);

        while (elements.MoveNext())
        {
            width += char.ConvertToUtf32((string)elements.Current, 0) >= 0x2000 ? 2 : 1;
        }

        return width;
    }

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex PlaceholderBlockPattern();

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
