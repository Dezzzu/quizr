namespace Quizr.Domain.Entities;

public sealed class Team
{
    public TeamId Id { get; set; }
    public required TelegramChatId ChatId { get; set; }
    public required string Name { get; set; }

    // IANA id, never an offset — see CLAUDE.md's Time section.
    public required string TimeZoneId { get; set; }

    // Language for group messages. DMs and the app use the person's own — see CLAUDE.md.
    public required string Locale { get; set; }

    public TimeOnly EveningBeforeAt { get; set; }
    public TimeOnly MorningOfAt { get; set; }
    public TimeSpan BeforeStartLead { get; set; }

    // The one pinned message. Null until the Board is first posted.
    public TelegramMessageId? BoardMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
