using System.Globalization;
using System.Text;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Quizr.App.Localization;
using Quizr.App.Rendering;
using Quizr.App.Time;
using IcalCalendar = Ical.Net.Calendar;
using IcalSerializer = Ical.Net.Serialization.CalendarSerializer;

namespace Quizr.App.Calendar;

// A CalendarFeed to the bytes of an .ics file. Pure: no clock, no database, no configuration —
// the same feed and locale always produce byte-identical output, which is what lets the ETag
// be a strong one rather than a hopeful one.
//
// Ical.Net is here for the two things that are genuinely fiddly and genuinely where .ics files
// go wrong: RFC 5545 text escaping, and folding lines at 75 *octets* without splitting a UTF-8
// sequence — which a Russian venue name will do to a naive implementation on its first outing.
// It is not here for timezones; the feed emits UTC instants and no VTIMEZONE, so Ical.Net's
// bundled NodaTime tzdb is never consulted. See docs/CALENDAR.md §3.
internal static class CalendarRenderer
{
    public static string Render(CalendarFeed feed, IStringsFor strings)
    {
        var calendar = new IcalCalendar { ProductId = CalendarFormat.ProductId };

        calendar.AddProperty("X-WR-CALNAME", CalendarName(feed, strings));
        calendar.AddProperty("X-PUBLISHED-TTL", CalendarFormat.RefreshInterval);

        // VALUE=DURATION is not optional here: without the parameter the value reads as a
        // plain string and Apple ignores the hint entirely.
        var refresh = new CalendarProperty("REFRESH-INTERVAL", CalendarFormat.RefreshInterval);
        refresh.AddParameter("VALUE", "DURATION");
        calendar.AddProperty(refresh);

        foreach (var entry in feed.Entries)
        {
            calendar.Events.Add(ToEvent(entry, feed.SharedTeamName is null, strings));
        }

        // Nullable by signature only — Ical.Net returns null for a null calendar, which is not
        // a state this method can be in.
        return new IcalSerializer(calendar).SerializeToString()
            ?? throw new InvalidOperationException("Ical.Net serialized the calendar feed to nothing.");
    }

    private static string CalendarName(CalendarFeed feed, IStringsFor strings) =>
        feed.SharedTeamName is { } team
            ? strings.Text("Calendar.Name", new { Team = team })
            : strings.Text("Calendar.NameAllTeams");

    private static CalendarEvent ToEvent(CalendarEntry entry, bool spansTeams, IStringsFor strings)
    {
        var occupiesTheEvening = entry.Status is CalendarEntryStatus.Playing or CalendarEntryStatus.Played;

        var calendarEvent = new CalendarEvent
        {
            // Derived from our own GameId, so it survives every edit to the game and never
            // becomes a fresh GUID on re-render — the difference between a client updating an
            // event and a client growing a duplicate of it.
            Uid = $"game-{entry.GameId.Value}@{CalendarFormat.UidDomain}",

            // Both from stored columns rather than the clock, so the same data renders the same
            // bytes every time. CalendarVersionInterceptor is what keeps them current.
            DtStamp = Utc(entry.RevisedAt),
            Sequence = entry.Revision,

            Start = Utc(entry.StartsAt),
            End = Utc(entry.EndsAt),
            Summary = Text(Summary(entry, spansTeams, strings)),
            Description = Text(Description(entry, strings)),
            Location = Text(entry.Venue),

            // A reserve place is exactly what TENTATIVE means, and TRANSPARENT keeps an evening
            // somebody may not play from showing them as busy. A game they didn't play stays
            // CONFIRMED — it happened — but stops occupying the evening in hindsight.
            Status = entry.Status is CalendarEntryStatus.Reserve ? EventStatus.Tentative : EventStatus.Confirmed,
            Transparency = occupiesTheEvening ? TransparencyType.Opaque : TransparencyType.Transparent,
        };

        foreach (var tag in entry.Tags)
        {
            calendarEvent.Categories.Add(tag);
        }

        if (entry.AnnouncementUrl is { } url)
        {
            calendarEvent.Url = new Uri(url);
        }

        // Deliberately no VALARM. Google does not fire alarms on a subscribed calendar, so one
        // here would be a reminder that arrives on some phones and not others — and the bot's
        // own reminders are the real notification path, already opt-in per slot and channel.
        return calendarEvent;
    }

    // Built by nesting whole templates rather than by concatenating fragments (CLAUDE.md): the
    // team prefix is one template, the status suffix another, and a locale is free to reorder
    // either around the title.
    private static string Summary(CalendarEntry entry, bool spansTeams, IStringsFor strings)
    {
        var label = GameLabel.RenderPlain(entry.Title, entry.FranchiseName, strings);

        // A team's name earns a place only when there is more than one to tell apart — the
        // same rule /myschedule already applies to its own lines.
        if (spansTeams)
        {
            label = strings.Text("Calendar.TitleWithTeam", new { Team = entry.TeamName, Title = label });
        }

        return entry.Status switch
        {
            CalendarEntryStatus.Reserve => strings.Text(
                "Calendar.SummaryReserve",
                new { Title = label, Position = entry.ReservePosition }
            ),
            CalendarEntryStatus.DidNotPlay => strings.Text("Calendar.SummaryDidNotPlay", new { Title = label }),
            _ => label,
        };
    }

    private static string Description(CalendarEntry entry, IStringsFor strings)
    {
        var lines = new StringBuilder();

        // Venue time first, and it is the whole reason this line exists: a client renders the
        // event in the *viewer's* zone, which is the wrong answer for anyone reading it away
        // from home. TZID would not have helped — it says how to interpret a wall clock, not
        // which one to display — so the venue's own time is text we write. See
        // docs/CALENDAR.md §3.
        var local = TeamTime.ConvertToLocal(entry.StartsAt, entry.TimeZoneId);
        var offset = TeamTime.GetUtcOffset(entry.StartsAt, entry.TimeZoneId);

        lines.AppendLine(
            strings.Text(
                "Calendar.VenueTime",
                new
                {
                    When = local,
                    Zone = entry.TimeZoneId,
                    Offset = FormatOffset(offset),
                }
            )
        );

        lines.AppendLine(StatusLine(entry, strings));

        if (entry.GuestCount > 0)
        {
            lines.AppendLine(strings.Text("Calendar.Guests", new { Guests = entry.GuestCount }));
        }

        if (entry.Price is { } price)
        {
            lines.AppendLine(strings.Text("Calendar.Price", new { Price = price }));
        }

        if (!string.IsNullOrWhiteSpace(entry.Notes))
        {
            lines.AppendLine(strings.Text("Calendar.Notes", new { Notes = entry.Notes }));
        }

        if (entry.Tags.Count > 0)
        {
            lines.AppendLine(
                strings.Text("Calendar.Tags", new { Tags = string.Join(' ', entry.Tags.Select(Hashtag.Render)) })
            );
        }

        if (entry.AnnouncementUrl is { } url)
        {
            lines.AppendLine(strings.Text("Calendar.OpenInTelegram", new { Url = url }));
        }

        return lines.ToString().TrimEnd();
    }

    private static string StatusLine(CalendarEntry entry, IStringsFor strings) =>
        entry.Status switch
        {
            CalendarEntryStatus.Playing => strings.Text("Calendar.StatusPlaying"),
            CalendarEntryStatus.Reserve => strings.Text(
                "Calendar.StatusReserve",
                new { Position = entry.ReservePosition }
            ),
            CalendarEntryStatus.Played => strings.Text("Calendar.StatusPlayed"),
            CalendarEntryStatus.DidNotPlay => strings.Text("Calendar.StatusDidNotPlay"),
            _ => throw new ArgumentOutOfRangeException(nameof(entry)),
        };

    // Ical.Net 5.2.3 escapes commas and semicolons in a TEXT value but leaves a backslash
    // alone, which RFC 5545 section 3.3.11 does not allow: a lone "\" starts an escape
    // sequence, so "C:\bar" reaches a client as the undefined "\b" and a strict parser is
    // entitled to reject the whole calendar. Pre-escaping ours means Ical.Net sees two
    // backslashes and passes both through, which is the correct output.
    //
    // If a future Ical.Net fixes this, the escaping test fails on the doubled result rather
    // than going quietly wrong — which is why the workaround is here and not in a sanitizer
    // that would swallow the evidence.
    private static string Text(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);

    // TimeSpan's custom format specifiers render the absolute value, so a zone west of
    // Greenwich needs its sign put back by hand.
    private static string FormatOffset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private static CalDateTime Utc(DateTimeOffset instant) =>
        new(instant.UtcDateTime, CalDateTime.UtcTzId, hasTime: true);
}
