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
    public static string Render(string title, string? franchiseName, IStringsFor strings)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        if (franchiseName is null || title.Contains(franchiseName, StringComparison.OrdinalIgnoreCase))
        {
            return encodedTitle;
        }

        return strings.Text(
            "Game.TitleWithFranchise",
            new { Franchise = WebUtility.HtmlEncode(franchiseName), Title = encodedTitle }
        );
    }
}
