namespace Quizr.Domain.Entities;

// One active dialog per person per chat — unique on (ChatId, PlayerId). In
// Postgres so a multi-step flow like game creation survives a restart.
public sealed class DialogState
{
    public long Id { get; set; }
    public required TeamId TeamId { get; set; }
    public required PlayerId PlayerId { get; set; }
    public required TelegramChatId ChatId { get; set; }
    public required string Kind { get; set; }
    public required string Step { get; set; }

    // jsonb.
    public required string Data { get; set; }

    public TelegramMessageId? MessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
