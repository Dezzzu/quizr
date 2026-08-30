namespace Quizr.Domain.Entities;

// Written when a game finishes — one row per person. Statistics read these,
// never signups. See CLAUDE.md invariants 9-11.
public sealed class Participation
{
    public ParticipationId Id { get; set; }
    public required GameId GameId { get; set; }

    // Requires .Include(p => p.Game); null only means "not loaded".
    public Game Game { get; set; } = null!;

    // Null for guests and venue-assigned.
    public PlayerId? PlayerId { get; set; }
    public Player? Player { get; set; }

    // For rows with no player.
    public string? Name { get; set; }

    public ParticipationKind Kind { get; set; }

    // False for reserves who didn't get in.
    public bool Played { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
