using System.Net;
using System.Text;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.App.Telegram;
using Quizr.App.Validation;
using Quizr.Domain.Entities;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Rendering;

// One function per message type, interpolated strings (STACK.md) — same split as
// AnnouncementRenderer: pure render functions here, DB/Telegram orchestration in
// UpdateRouter.
internal static class FranchiseRenderer
{
    private static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday,
    ];

    public static string RenderSummary(Franchise franchise, IStringsFor strings)
    {
        var text = new StringBuilder();
        text.Append("<b>").Append(WebUtility.HtmlEncode(franchise.Name)).Append("</b>\n");

        if (franchise.ArchivedAt is not null)
        {
            text.Append(strings.Text("Franchise.Archived")).Append('\n');
        }

        text.Append(
                franchise.DefaultVenue is { } venue
                    ? strings.Text("Franchise.Venue", new { Venue = WebUtility.HtmlEncode(venue) })
                    : strings.Text("Franchise.NoVenue")
            )
            .Append('\n');
        text.Append(
                franchise.DefaultCapacity is { } capacity
                    ? strings.Text("Franchise.Capacity", new { Capacity = capacity })
                    : strings.Text("Franchise.NoCapacity")
            )
            .Append('\n');
        text.Append(
                franchise.DefaultPrice is { } price
                    ? strings.Text("Franchise.Price", new { Price = price })
                    : strings.Text("Franchise.NoPrice")
            )
            .Append('\n');
        text.Append(
            strings.Text("Franchise.Schedule", new { Schedule = FormatSchedule(franchise.Schedule, strings.Locale) })
        );

        return text.ToString();
    }

    // Every non-archived franchise, one button each — for both /newgame's franchise pick and
    // /editfranchise's target pick.
    public static InlineKeyboardMarkup RenderPicker(IReadOnlyList<Franchise> franchises, char verb) =>
        new(
            franchises
                .Select(f => new[] { InlineKeyboardButton.WithCallbackData(f.Name, CallbackData.Format(verb, f.Id)) })
                .ToList()
        );

    public static InlineKeyboardMarkup RenderFieldPicker(Franchise franchise, IStringsFor strings) =>
        new([
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Franchise.EditNameButton"),
                    CallbackData.Format(CallbackData.EditField, EditFranchiseDialogData.Name)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Franchise.EditVenueButton"),
                    CallbackData.Format(CallbackData.EditField, EditFranchiseDialogData.Venue)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Franchise.EditCapacityButton"),
                    CallbackData.Format(CallbackData.EditField, EditFranchiseDialogData.Capacity)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Franchise.EditPriceButton"),
                    CallbackData.Format(CallbackData.EditField, EditFranchiseDialogData.Price)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Franchise.EditScheduleButton"),
                    CallbackData.Format(CallbackData.EditField, EditFranchiseDialogData.Schedule)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Franchise.ArchiveButton"),
                    CallbackData.Format(CallbackData.ArchiveFranchise, franchise.Id)
                ),
            ],
            DoneButton.Row(strings),
        ]);

    private static string FormatSchedule(Dictionary<DayOfWeek, TimeOnly> schedule, string locale)
    {
        if (schedule.Count == 0)
        {
            return "—";
        }

        return string.Join(
            ", ",
            WeekOrder
                .Where(schedule.ContainsKey)
                .Select(day => $"{FieldParsing.DayName(day, locale)} {schedule[day]:HH\\:mm}")
        );
    }
}
