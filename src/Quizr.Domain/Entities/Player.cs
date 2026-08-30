namespace Quizr.Domain.Entities;

// Global — one row per Telegram user, shared across teams.
public sealed class Player
{
    public PlayerId Id { get; set; }
    public required TelegramUserId TelegramUserId { get; set; }
    public required string DisplayName { get; set; }
    public string? Username { get; set; }

    // Null falls back to the team's locale.
    public string? Locale { get; set; }

    // True once they've started the bot — a bot cannot message anyone who hasn't.
    public bool DmEnabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
