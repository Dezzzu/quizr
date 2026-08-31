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

    // Who this dialog belongs to, in the form Telegram needs. PlayerId is the domain identity;
    // this is the one an ephemeral message is addressed by, and every prompt a dialog sends
    // goes to its owner, so it is a fact about the dialog rather than about any one message.
    // Null on rows written before private wizards existed — those fall back to ordinary sends.
    public TelegramUserId? OwnerTelegramUserId { get; set; }

    // The prompt this dialog is currently waiting on, so its keyboard can be stripped once the
    // step it belongs to has actually been answered.
    public TelegramMessageId? MessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
