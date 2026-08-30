using System.Net;
using System.Text;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.App.Telegram;
using Quizr.Domain.Entities;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Rendering;

// Acting on a player's behalf (design decision #2 of M9): every team member as a toggle row
// — tapping a not-signed-up member registers them, tapping a signed-up one drops them.
// Structurally the same shape as RosterManagementRenderer, one more instance of the
// list-of-people-with-a-toggle pattern.
internal static class ManagePlayersRenderer
{
    public static string RenderText(Game game, IReadOnlyList<MemberSignupStatus> statuses, IStringsFor strings)
    {
        var text = new StringBuilder();
        text.Append(strings.Text("ManagePlayers.Header", new { Title = WebUtility.HtmlEncode(game.Title) }))
            .Append('\n');

        if (statuses.Count == 0)
        {
            text.Append(strings.Text("ManagePlayers.Empty"));
            return text.ToString();
        }

        foreach (var status in Ordered(statuses))
        {
            text.Append("• ").Append(WebUtility.HtmlEncode(status.Membership.Player.DisplayName)).Append(" — ");
            text.Append(
                status.IsSignedUp ? strings.Text("ManagePlayers.SignedUp") : strings.Text("ManagePlayers.NotSignedUp")
            );
            text.Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    public static InlineKeyboardMarkup RenderKeyboard(IReadOnlyList<MemberSignupStatus> statuses, IStringsFor strings)
    {
        var rows = Ordered(statuses)
            .Select(status =>
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        strings.Text(
                            status.IsSignedUp ? "ManagePlayers.DropButton" : "ManagePlayers.RegisterButton",
                            new { Name = status.Membership.Player.DisplayName }
                        ),
                        CallbackData.Format(CallbackData.TogglePlayerSignup, status.Membership.PlayerId)
                    ),
                }
            )
            .ToList();

        return new InlineKeyboardMarkup(rows);
    }

    private static IEnumerable<MemberSignupStatus> Ordered(IReadOnlyList<MemberSignupStatus> statuses) =>
        statuses.OrderBy(s => s.Membership.Player.DisplayName, StringComparer.Ordinal);
}
