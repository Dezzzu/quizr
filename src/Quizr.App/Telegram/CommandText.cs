namespace Quizr.App.Telegram;

// Splits a Telegram message like "/settimezone@quizr_team_bot Europe/Berlin" into a
// command ("/settimezone") and its argument ("Europe/Berlin"). Groups let a command
// target a specific bot with an "@name" suffix; that suffix is stripped so matching
// stays a plain string comparison.
internal static class CommandText
{
    public static (string Command, string? Argument) Parse(string text)
    {
        var trimmed = text.Trim();
        var spaceIndex = trimmed.IndexOf(' ');
        var firstToken = spaceIndex < 0 ? trimmed : trimmed[..spaceIndex];
        var argument = spaceIndex < 0 ? null : trimmed[(spaceIndex + 1)..].Trim();

        var atIndex = firstToken.IndexOf('@');
        var command = atIndex < 0 ? firstToken : firstToken[..atIndex];

        return (command, string.IsNullOrEmpty(argument) ? null : argument);
    }
}
