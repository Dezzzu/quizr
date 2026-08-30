namespace Quizr.Domain.Entities;

public sealed class Team
{
    public TeamId Id { get; set; }
    public required TelegramChatId ChatId { get; set; }
    public required string Name { get; set; }

    // IANA id, never an offset — see CLAUDE.md's Time section. Null until a captain sets it;
    // every StartsAt is computed from it, so games can't be created before it's set.
    public string? TimeZoneId { get; set; }

    // Language for group messages. DMs and the app use the person's own — see CLAUDE.md.
    public required string Locale { get; set; }

    public TimeOnly EveningBeforeAt { get; set; }
    public TimeOnly MorningOfAt { get; set; }
    public TimeSpan BeforeStartLead { get; set; }

    // The one pinned message. Null until the Board is first posted.
    public TelegramMessageId? BoardMessageId { get; set; }

    // Set when the bot is removed from the chat, cleared if it's re-added. Never deleted —
    // invariant 7.
    public DateTimeOffset? DeactivatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<Game> Games { get; set; } = [];
    public List<Membership> Memberships { get; set; } = [];
    public List<Franchise> Franchises { get; set; } = [];
}
