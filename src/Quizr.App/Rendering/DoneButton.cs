using Quizr.App.Localization;
using Quizr.App.Telegram;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Rendering;

// The one "end this view" row every open-ended menu appends as its last row — My guests,
// Manage players, Manage guests, Manage captains, reminder settings, the franchise
// field-picker, Manage roster. Same button, same CloseView verb (handled once, generically,
// in UpdateRouter.HandleCloseViewAsync), everywhere — so a new such view gets a working end
// for free instead of one more place to forget it. History: three separate views shipped
// without this before it became a shared row, one of them twice.
internal static class DoneButton
{
    public static InlineKeyboardButton[] Row(IStringsFor strings) =>
        [
            InlineKeyboardButton.WithCallbackData(
                strings.Text("Common.DoneButton"),
                CallbackData.Format(CallbackData.CloseView, 0L)
            ),
        ];
}
