namespace Quizr.Domain.Entities;

// Nothing is ever deleted — this is what makes queue disputes answerable.
// See CLAUDE.md invariant 7.
public sealed class AuditEntry
{
    public long Id { get; set; }
    public required TeamId TeamId { get; set; }
    public GameId? GameId { get; set; }

    // Null means system.
    public PlayerId? ActorPlayerId { get; set; }

    public required string Action { get; set; }

    // jsonb.
    public required string Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
