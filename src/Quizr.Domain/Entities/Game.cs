namespace Quizr.Domain.Entities;

// A franchise is a template, not a live reference: venue, capacity, price and
// time are copied onto the game at creation and stay editable there, so
// editing a franchise never rewrites past games.
//
// Open and in-progress are not stored — they're derived from the clock against
// StartsAt and FinishedAt, the same way playing-versus-reserve is derived from
// the queue. See CLAUDE.md invariant 8.
public sealed class Game
{
    public GameId Id { get; set; }
    public required TeamId TeamId { get; set; }

    // Requires .Include(g => g.Team); null only means "not loaded".
    public Team Team { get; set; } = null!;

    // Null for a one-off game.
    public FranchiseId? FranchiseId { get; set; }
    public Franchise? Franchise { get; set; }

    public List<Signup> Signups { get; set; } = [];

    public required string Title { get; set; }
    public required string Venue { get; set; }

    // Computed from picked date + schedule time + team zone. Stored as the
    // instant, never re-derived from another game's start.
    public DateTimeOffset StartsAt { get; set; }

    public int Capacity { get; set; }
    public decimal? Price { get; set; }
    public string? Notes { get; set; }
    public TelegramMessageId? AnnouncementMessageId { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? DeclinedAt { get; set; }

    // Cooldown for nudges, not deduplication.
    public DateTimeOffset? LastNudgedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public required PlayerId CreatedByPlayerId { get; set; }
}
