using Quizr.Domain;

namespace Quizr.App.Telegram;

// Callback data is capped at 64 bytes (CLAUDE.md), so buttons carry a compact
// "verb:id" pair rather than serialized JSON. Game-scoped verbs carry a GameId;
// guest-scoped verbs carry the guest's own SignupId, since a guest's fate isn't
// tied to whoever happens to be tapping the button.
internal static class CallbackData
{
    // Announcement buttons — carry a GameId.
    public const char Join = 'j';
    public const char Guest = 'g';
    public const char Drop = 'd';
    public const char ConfirmDrop = 'D';
    public const char Stay = 'b';

    // Guest-scoped follow-ups — carry a SignupId.
    public const char SkipGuestName = 'N';
    public const char KeepGuest = 'K';
    public const char RemoveGuestToo = 'X';

    public static string Format(char verb, GameId gameId) => Format(verb, gameId.Value);

    public static string Format(char verb, SignupId signupId) => Format(verb, signupId.Value);

    public static bool TryParse(string data, out char verb, out GameId gameId)
    {
        var ok = TryParse(data, out verb, out long value);
        gameId = ok ? new GameId(value) : default;
        return ok;
    }

    public static bool TryParse(string data, out char verb, out SignupId signupId)
    {
        var ok = TryParse(data, out verb, out long value);
        signupId = ok ? new SignupId(value) : default;
        return ok;
    }

    // Reads just the verb, so a caller can decide which typed TryParse overload
    // applies before committing to one.
    public static bool TryParseVerb(string data, out char verb)
    {
        if (data.Length > 0 && data.IndexOf(':') > 0)
        {
            verb = data[0];
            return true;
        }

        verb = default;
        return false;
    }

    private static string Format(char verb, long id) => $"{verb}:{id}";

    private static bool TryParse(string data, out char verb, out long value)
    {
        var separator = data.IndexOf(':');
        if (separator > 0 && long.TryParse(data.AsSpan(separator + 1), out value))
        {
            verb = data[0];
            return true;
        }

        verb = default;
        value = default;
        return false;
    }
}
