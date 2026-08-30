namespace Quizr.Domain.Entities;

// Someone holding a place in a game. CreatedAt determines queue order,
// permanently — see CLAUDE.md invariant 1. Dropping out cancels the signup
// entirely (invariant 3); re-registering creates a new one, at the back.
public sealed class Signup
{
    public SignupId Id { get; set; }
    public required GameId GameId { get; set; }

    // Null means guest.
    public PlayerId? PlayerId { get; set; }

    // Optional for guests, required for team guests.
    public string? GuestName { get; set; }

    // Null on a guest means team guest — no owner, must be named.
    public PlayerId? InvitedByPlayerId { get; set; }

    // Queue order. Never rewritten.
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public PlayerId? CancelledByPlayerId { get; set; }
}
