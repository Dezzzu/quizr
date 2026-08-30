using Quizr.Domain;

namespace Quizr.App.Telegram;

// Callback data is capped at 64 bytes (CLAUDE.md), so buttons carry a compact
// "verb:id" pair rather than serialized JSON. No handler is wired to these yet —
// M4's signup buttons are the first caller — but the scheme is fixed here so
// every future button uses the same encoding.
internal static class CallbackData
{
    public const char Join = 'j';
    public const char Guest = 'g';
    public const char Drop = 'd';

    public static string Format(char verb, GameId gameId) => $"{verb}:{gameId.Value}";

    public static bool TryParse(string data, out char verb, out GameId gameId)
    {
        var separator = data.IndexOf(':');
        if (separator > 0 && long.TryParse(data.AsSpan(separator + 1), out var value))
        {
            verb = data[0];
            gameId = new GameId(value);
            return true;
        }

        verb = default;
        gameId = default;
        return false;
    }
}
