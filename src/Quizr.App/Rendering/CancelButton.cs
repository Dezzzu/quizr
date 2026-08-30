using Quizr.App.Localization;
using Quizr.App.Telegram;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Rendering;

// A way out of every step of franchise/game creation and editing, not just the confirm
// screen and Nudge picker that already had one — a captain who changes their mind mid-wizard
// shouldn't have to know /cancel exists or answer every remaining prompt to escape it. Same
// CallbackData.CancelDialog verb everywhere (already handled generically, dialog-kind-
// agnostic, in UpdateRouter.HandleCancelDialogAsync) — mirrors DoneButton/SkipButton's shared-
// row shape, and combines with SkipButton.Row on any prompt that's also skippable.
internal static class CancelButton
{
    public static InlineKeyboardMarkup Keyboard(IStringsFor strings) => new([Row(strings)]);

    public static InlineKeyboardButton[] Row(IStringsFor strings) =>
        [
            InlineKeyboardButton.WithCallbackData(
                strings.Text("Common.CancelButton"),
                CallbackData.Format(CallbackData.CancelDialog, 0L)
            ),
        ];
}
