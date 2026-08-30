namespace Quizr.Domain.Entities;

// Someone holding a place in a game. CreatedAt determines queue order,
// permanently — see CLAUDE.md invariant 1. Dropping out cancels the signup
// entirely (invariant 3); re-registering creates a new one, at the back.
public sealed class Signup
{
    public SignupId Id { get; set; }
    public required GameId GameId { get; set; }

    // Requires .Include(s => s.Game); null only means "not loaded".
    public Game Game { get; set; } = null!;

    // Null means guest. Null also means "not loaded" — branch on PlayerId, never on this,
    // or a member whose Player wasn't Included silently reads as an anonymous guest.
    public PlayerId? PlayerId { get; set; }
    public Player? Player { get; set; }

    // Optional for guests, required for team guests.
    public string? GuestName { get; set; }

    // Null on a guest means team guest — no owner, must be named.
    public PlayerId? InvitedByPlayerId { get; set; }
    public Player? InvitedByPlayer { get; set; }

    // Queue order. Never rewritten.
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public PlayerId? CancelledByPlayerId { get; set; }
    public Player? CancelledByPlayer { get; set; }
}
