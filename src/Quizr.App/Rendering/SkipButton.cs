using Quizr.App.Localization;
using Quizr.App.Telegram;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Rendering;

// A tap-to-skip alternative on every optional text prompt (franchise venue/capacity/schedule,
// game notes/tags/price) — Telegram clients won't let a captain send a genuinely empty
// message, so typing "skip" is the only reliable fallback; this is the faster, no-typing one.
// Same CallbackData.Skip verb everywhere, handled once, generically, in
// UpdateRouter.HandleSkipAsync — mirrors DoneButton's shared-row shape.
internal static class SkipButton
{
    public static InlineKeyboardMarkup Keyboard(IStringsFor strings) => new([Row(strings)]);

    // Every skippable prompt is also a cancellable one — same row, Skip first.
    public static InlineKeyboardMarkup KeyboardWithCancel(IStringsFor strings) =>
        new([
            [.. Row(strings), .. CancelButton.Row(strings)],
        ]);

    public static InlineKeyboardButton[] Row(IStringsFor strings) =>
        [
            InlineKeyboardButton.WithCallbackData(
                strings.Text("Common.SkipButton"),
                CallbackData.Format(CallbackData.Skip, 0L)
            ),
        ];
}
