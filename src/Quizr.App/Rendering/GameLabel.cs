using System.Net;
using Quizr.App.Localization;

namespace Quizr.App.Rendering;

// How a game is named wherever it's named — the announcement's own headline and each Board
// entry, which have to agree.
//
// A game keeps whatever title it was given, and a captain who renames one — on the confirm
// screen or later through /editgame — usually drops the franchise out of it: "Halloween
// special", not "Квиз, плиз! #12". So the brand goes back in front of a title that doesn't
// already carry it.
internal static class GameLabel
{
    // Returns HTML-ready text: both halves are encoded before the template joins them, since
    // the result gets spliced into the announcement's <b> and the Board's <a>, and
    // user-visible text is never concatenated by hand (CLAUDE.md).
    //
    // Contains rather than StartsWith: the point is not to say the name twice, and a title
    // like "Осенний Квиз, плиз!" already says it.
    public static string Render(string title, string? franchiseName, IStringsFor strings) =>
        Combine(title, franchiseName, strings, WebUtility.HtmlEncode);

    // The same rule for somewhere HTML would be wrong: a calendar event's SUMMARY, which is
    // plain text that the .ics escaping handles on its own terms. Shared rather than copied so
    // a game cannot be named one way in the chat and another way in a calendar.
    public static string RenderPlain(string title, string? franchiseName, IStringsFor strings) =>
        Combine(title, franchiseName, strings, text => text);

    private static string Combine(string title, string? franchiseName, IStringsFor strings, Func<string, string> escape)
    {
        if (franchiseName is null || title.Contains(franchiseName, StringComparison.OrdinalIgnoreCase))
        {
            return escape(title);
        }

        return strings.Text(
            "Game.TitleWithFranchise",
            new { Franchise = escape(franchiseName), Title = escape(title) }
        );
    }
}
