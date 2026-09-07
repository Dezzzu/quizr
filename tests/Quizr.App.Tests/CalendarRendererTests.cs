using System.Text;
using AwesomeAssertions;
using Quizr.App.Calendar;
using Quizr.App.Localization;
using Quizr.Domain;
using IcalCalendar = Ical.Net.Calendar;

namespace Quizr.App.Tests;

// No database and no container: CalendarRenderer is a pure function over a feed and a locale,
// which is the entire reason the feed is loaded into DTOs first. This is where the effort goes
// — escaping, folding and stable UIDs are where .ics files actually go wrong, and none of it
// is visible until somebody's calendar client silently drops an event.
public class CalendarRendererTests
{
    private readonly IStringsFor _en = new Strings().For("en");

    [Test]
    public void EveryTimestampIsAUtcInstantAndNoTimeZoneIsEverNamed()
    {
        var ics = Render(Entry());

        Lines(ics).Should().Contain(line => line.StartsWith("DTSTART:", StringComparison.Ordinal));
        Lines(ics).Should().Contain("DTSTART:20260904T173000Z");
        Lines(ics).Should().Contain("DTEND:20260904T203000Z");

        // The whole point of docs/CALENDAR.md §3: TZID without a matching VTIMEZONE is the
        // classic way to have events silently dropped, and shipping VTIMEZONE would mean
        // consulting a second timezone database. Neither appears.
        ics.Should().NotContain("TZID");
        ics.Should().NotContain("VTIMEZONE");
    }

    [Test]
    public void LinesEndWithCarriageReturnLineFeed()
    {
        var ics = Render(Entry());

        ics.Should().Contain("\r\n");
        ics.Replace("\r\n", "").Should().NotContain("\n");
    }

    // RFC 5545 folds at 75 octets, not 75 characters. A Cyrillic venue name is two octets per
    // character, so a fold that counts characters splits a UTF-8 sequence and produces
    // mojibake in the middle of an address — the specific bug Ical.Net is here to own.
    [Test]
    public void ALongCyrillicVenueFoldsWithoutBreakingItsEncoding()
    {
        var venue = "Бар «Гагарин», вход со двора, второй этаж — спросить Лену на входе, скажите что вы из команды";

        var ics = Render(Entry(venue: venue));

        Lines(ics).Should().OnlyContain(line => Encoding.UTF8.GetByteCount(line) <= 75);
        Unfold(ics).Should().Contain("Бар «Гагарин»");
        Unfold(ics).Should().Contain("спросить Лену на входе");
    }

    // Ical.Net escapes the comma and the semicolon itself; the backslash it does not, which is
    // an RFC 5545 violation CalendarRenderer.Text works around. If a future version fixes it,
    // this fails on a doubled backslash rather than going quietly wrong.
    [Test]
    public void CommasSemicolonsAndBackslashesAreEscaped()
    {
        var ics = Render(Entry(venue: @"Bar, ""Gagarin""; back yard \ second floor"));

        Unfold(ics).Should().Contain(@"LOCATION:Bar\, ""Gagarin""\; back yard \\ second floor");
    }

    // The other half of the same claim: a client unescaping our output has to get the original
    // text back, one backslash and not two.
    [Test]
    public void AnEscapedBackslashSurvivesARoundTrip()
    {
        var ics = Render(Entry(venue: @"Cellar bar \ back room"));

        Parse(ics).Events.Single().Location.Should().Be(@"Cellar bar \ back room");
    }

    [Test]
    public void ANewlineInsideNotesBecomesAnEscapedSequenceRatherThanANewLine()
    {
        var ics = Render(Entry(notes: "Bring cash\nAsk for Lena"));

        Unfold(ics).Should().Contain(@"Bring cash\nAsk for Lena");
    }

    // The difference between a client updating an event and a client growing a duplicate of it
    // every time the feed is fetched.
    [Test]
    public void TheUidIsDerivedFromTheGameAndSurvivesAChangeToEverythingElse()
    {
        var before = Render(Entry(venue: "The Pub", status: CalendarEntryStatus.Playing));
        var after = Render(Entry(venue: "Somewhere else", status: CalendarEntryStatus.Reserve, revision: 9));

        Unfold(before).Should().Contain("UID:game-142@quizr.bot");
        Unfold(after).Should().Contain("UID:game-142@quizr.bot");
    }

    [Test]
    public void TwoGamesGetTwoDifferentUids()
    {
        var ics = Render(Entry(gameId: 1), Entry(gameId: 2));

        Unfold(ics).Should().Contain("UID:game-1@quizr.bot").And.Contain("UID:game-2@quizr.bot");
    }

    // The strong ETag is a promise that the same data renders the same bytes. Anything the
    // renderer took from the clock — a DTSTAMP of "now", a CREATED — would break that
    // silently, and a client would re-download an identical feed forever.
    [Test]
    public void RenderingTheSameFeedTwiceProducesIdenticalBytes()
    {
        var feed = Feed(Entry());

        CalendarRenderer.Render(feed, _en).Should().Be(CalendarRenderer.Render(feed, _en));
    }

    [Test]
    public void DtStampAndSequenceComeFromTheGamesOwnRevision()
    {
        var ics = Render(Entry(revision: 4));

        Lines(ics).Should().Contain("DTSTAMP:20260901T100000Z");
        Lines(ics).Should().Contain("SEQUENCE:4");
    }

    [Test]
    public void APlayingSeatIsConfirmedAndBooksTheEvening()
    {
        var ics = Render(Entry(status: CalendarEntryStatus.Playing));

        Lines(ics).Should().Contain("STATUS:CONFIRMED").And.Contain("TRANSP:OPAQUE");
    }

    // A reserve place may never become a real one, so it says so and leaves the evening free.
    [Test]
    public void AReservePlaceIsTentativeAndLeavesTheEveningFree()
    {
        var ics = Render(Entry(status: CalendarEntryStatus.Reserve, reservePosition: 3));

        Lines(ics).Should().Contain("STATUS:TENTATIVE").And.Contain("TRANSP:TRANSPARENT");
        Unfold(ics).Should().Contain("(reserve #3)");
    }

    [Test]
    public void AGameThatWasNotPlayedStaysConfirmedButStopsBookingTheEvening()
    {
        var ics = Render(Entry(status: CalendarEntryStatus.DidNotPlay));

        Lines(ics).Should().Contain("STATUS:CONFIRMED").And.Contain("TRANSP:TRANSPARENT");
        Unfold(ics).Should().Contain("(didn't play)");
    }

    [Test]
    public void TheRefreshHintsAndCalendarNameAreAllPresent()
    {
        var ics = Render(Entry());

        Lines(ics).Should().Contain("REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        Lines(ics).Should().Contain("X-PUBLISHED-TTL:PT1H");
        Lines(ics).Should().Contain("PRODID:-//Quizr//Quizr Bot//EN");
        Unfold(ics).Should().Contain("X-WR-CALNAME:Quizr — Мозгобойня");
    }

    // Google will not fire an alarm on a subscribed calendar, so one here would be a reminder
    // that arrives on some phones and not others. The bot's own reminders are the real path.
    [Test]
    public void NoAlarmIsEverEmitted()
    {
        var ics = Render(Entry());

        ics.Should().NotContain("VALARM");
    }

    // METHOD:REQUEST would make this an iTIP scheduling message rather than a subscription,
    // and some clients answer one with invitation emails.
    [Test]
    public void NoSchedulingMethodIsEmitted()
    {
        var ics = Render(Entry());

        Lines(ics).Should().NotContain(line => line.StartsWith("METHOD:", StringComparison.Ordinal));
    }

    // Some clients treat a zero-byte or malformed body as an error and stop refreshing
    // altogether, so an empty schedule still has to be a valid calendar.
    [Test]
    public void AFeedWithNoGamesIsStillAValidCalendar()
    {
        var ics = CalendarRenderer.Render(new CalendarFeed(null, []), _en);

        Lines(ics).Should().Contain("BEGIN:VCALENDAR").And.Contain("END:VCALENDAR");
        ics.Should().NotContain("BEGIN:VEVENT");
        Parse(ics).Events.Should().BeEmpty();
    }

    // A team's name earns a place on an event only when there is more than one to tell apart.
    [Test]
    public void AnEventNamesItsTeamOnlyWhenTheFeedSpansMoreThanOne()
    {
        var oneTeam = CalendarRenderer.Render(Feed(Entry()), _en);
        var twoTeams = CalendarRenderer.Render(new CalendarFeed(null, [Entry()]), _en);

        Unfold(oneTeam).Should().Contain("SUMMARY:Квиз\\, плиз! · #142").And.NotContain("Мозгобойня · Квиз");
        Unfold(twoTeams).Should().Contain("SUMMARY:Мозгобойня · Квиз\\, плиз! · #142");
    }

    // The line that makes the feed unambiguous for anyone reading it away from home: the
    // client shows the viewer's own zone, and this shows the venue's.
    [Test]
    public void TheDescriptionLeadsWithTheTimeAtTheVenue()
    {
        var ics = Render(Entry());

        Unfold(ics).Should().Contain(@"DESCRIPTION:Starts Fri\, 04 Sep\, 19:30 · Europe/Berlin (UTC+02:00)");
    }

    // Same instant, a zone west of Greenwich: TimeSpan's format specifiers drop the sign, so
    // this is the case that catches it coming back as "+05:00".
    [Test]
    public void AZoneBehindUtcKeepsItsNegativeOffset()
    {
        var ics = Render(Entry(timeZoneId: "America/New_York"));

        Unfold(ics).Should().Contain("(UTC-04:00)");
    }

    [Test]
    public void TagsBecomeBothCategoriesAndHashtags()
    {
        var ics = Render(Entry(tags: ["music", "true crime"]));

        Unfold(ics).Should().Contain("CATEGORIES:music,true crime");
        Unfold(ics).Should().Contain("#music #true_crime");
    }

    [Test]
    public void AGameWithNoAnnouncementLinkOmitsTheUrlRatherThanEmittingAnEmptyOne()
    {
        var ics = Render(Entry(announcementUrl: null));

        Lines(ics).Should().NotContain(line => line.StartsWith("URL", StringComparison.Ordinal));
        Unfold(ics).Should().NotContain("Open in Telegram");
    }

    // Round-tripping through Ical.Net's own parser is the check that the output is a calendar
    // rather than merely a string that looks like one.
    [Test]
    public void TheOutputParsesBackIntoTheSameEvent()
    {
        var ics = Render(Entry(status: CalendarEntryStatus.Playing));

        var parsed = Parse(ics).Events.Single();

        parsed.Uid.Should().Be("game-142@quizr.bot");
        parsed.Start!.AsUtc.Should().Be(new DateTime(2026, 9, 4, 17, 30, 0, DateTimeKind.Utc));
        parsed.End!.AsUtc.Should().Be(new DateTime(2026, 9, 4, 20, 30, 0, DateTimeKind.Utc));
        parsed.Sequence.Should().Be(3);
        parsed.Location.Should().Be("The Pub");
        parsed.Alarms.Should().BeEmpty();
    }

    [Test]
    public void EveryLocaleRendersTheSameEventWithoutAMissingKey()
    {
        var strings = new Strings();

        foreach (var locale in LocaleResolver.All)
        {
            var ics = CalendarRenderer.Render(Feed(Entry(status: CalendarEntryStatus.Reserve)), strings.For(locale));

            Parse(ics).Events.Should().ContainSingle();
        }
    }

    private string Render(params CalendarEntry[] entries) => CalendarRenderer.Render(Feed(entries), _en);

    private static CalendarFeed Feed(params CalendarEntry[] entries) => new("Мозгобойня", entries);

    // One realistic entry, with every awkward value already in it — a franchise whose name
    // carries a comma, a Cyrillic team, a real t.me link — so each test overrides only the one
    // thing it is about.
    private static CalendarEntry Entry(
        long gameId = 142,
        string venue = "The Pub",
        string timeZoneId = "Europe/Berlin",
        CalendarEntryStatus status = CalendarEntryStatus.Playing,
        int reservePosition = 0,
        int revision = 3,
        string? notes = null,
        IReadOnlyList<string>? tags = null,
        int guestCount = 0,
        string? announcementUrl = "https://t.me/c/1234567890/55"
    ) =>
        new(
            new GameId(gameId),
            new TeamId(1),
            "Мозгобойня",
            "#142",
            "Квиз, плиз!",
            venue,
            new DateTimeOffset(2026, 9, 4, 17, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 4, 20, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
            revision,
            timeZoneId,
            status,
            reservePosition,
            null,
            notes,
            tags ?? [],
            guestCount,
            announcementUrl
        );

    // Load is nullable by signature. Null here would mean the renderer produced something
    // that is not a calendar at all, which is a failure rather than a case to assert around.
    private static IcalCalendar Parse(string ics) =>
        IcalCalendar.Load(ics)
        ?? throw new InvalidOperationException("The rendered feed did not parse back as a calendar.");

    private static string[] Lines(string ics) => ics.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    // RFC 5545 unfolding: a CRLF followed by a single space or tab is a continuation, and the
    // whole sequence disappears. Assertions about content run against this; assertions about
    // the 75-octet limit run against the folded lines.
    private static string Unfold(string ics) => ics.Replace("\r\n ", "").Replace("\r\n\t", "");
}
