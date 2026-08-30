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

    // A person may belong to several teams — never a single Membership. Requires
    // .Include(p => p.Memberships), optionally filtered to one team.
    public List<Membership> Memberships { get; set; } = [];
}
