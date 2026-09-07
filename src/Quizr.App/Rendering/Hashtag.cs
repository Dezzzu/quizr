namespace Quizr.App.Rendering;

// A game's tags rendered the one way they're rendered anywhere: as real hashtags, which is
// what makes a past game findable through Telegram's own in-chat search until the mini app's
// archive lands. A space would end a hashtag early, so it becomes an underscore.
//
// Shared between the announcement and the calendar feed's event description rather than
// copied, since the two would otherwise be free to disagree about what a tag looks like.
internal static class Hashtag
{
    public static string Render(string tag) => "#" + tag.Trim().Replace(' ', '_');
}
