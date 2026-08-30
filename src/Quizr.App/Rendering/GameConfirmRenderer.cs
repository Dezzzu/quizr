using System.Net;
using System.Text;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.App.Telegram;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Rendering;

// The confirm screen every /newgame path lands on (design decision #2): Venue/Capacity/
// Price/Notes shown with an edit button next to each, defaulted from the franchise or the
// one-off replies but individually overridable before Create.
internal static class GameConfirmRenderer
{
    public static string RenderText(NewGameDialogData data, IStringsFor strings)
    {
        var text = new StringBuilder();
        text.Append("<b>").Append(WebUtility.HtmlEncode(data.Title)).Append("</b>\n");
        text.Append(
                data.Venue is { } venue
                    ? strings.Text("NewGame.Venue", new { Venue = WebUtility.HtmlEncode(venue) })
                    : strings.Text("NewGame.VenueNotSet")
            )
            .Append('\n');
        text.Append(strings.Text("NewGame.When", new { Date = data.Date!.Value, Time = data.Time!.Value }))
            .Append('\n');
        text.Append(
                data.Capacity is { } capacity
                    ? strings.Text("NewGame.Capacity", new { Capacity = capacity })
                    : strings.Text("NewGame.CapacityNotSet")
            )
            .Append('\n');
        text.Append(
                data.Price is { } price
                    ? strings.Text("NewGame.Price", new { Price = price })
                    : strings.Text("NewGame.NoPrice")
            )
            .Append('\n');

        if (!string.IsNullOrWhiteSpace(data.Notes))
        {
            text.Append(WebUtility.HtmlEncode(data.Notes)).Append('\n');
        }

        if (data.Tags is { Count: > 0 })
        {
            text.Append(string.Join(' ', data.Tags.Select(ToHashtag))).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    // Same rendering as AnnouncementRenderer.ToHashtag — the confirm screen previews exactly
    // what the announcement will show.
    private static string ToHashtag(string tag) => "#" + WebUtility.HtmlEncode(tag.Trim().Replace(' ', '_'));

    public static InlineKeyboardMarkup RenderKeyboard(IStringsFor strings) =>
        new([
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("NewGame.EditVenueButton"),
                    CallbackData.Format(CallbackData.EditField, NewGameDialogData.OverrideVenue)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("NewGame.EditCapacityButton"),
                    CallbackData.Format(CallbackData.EditField, NewGameDialogData.OverrideCapacity)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("NewGame.EditPriceButton"),
                    CallbackData.Format(CallbackData.EditField, NewGameDialogData.OverridePrice)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("NewGame.EditNotesButton"),
                    CallbackData.Format(CallbackData.EditField, NewGameDialogData.OverrideNotes)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("NewGame.EditTagsButton"),
                    CallbackData.Format(CallbackData.EditField, NewGameDialogData.OverrideTags)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("NewGame.ConfirmButton"),
                    CallbackData.Format(CallbackData.Confirm, 0L)
                ),
                .. CancelButton.Row(strings),
            ],
        ]);
}
