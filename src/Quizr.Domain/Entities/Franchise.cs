namespace Quizr.Domain.Entities;

// Team-scoped. Captains create and edit them; there is no global catalogue.
public sealed class Franchise
{
    public FranchiseId Id { get; set; }
    public required TeamId TeamId { get; set; }

    // Requires .Include(f => f.Team); null only means "not loaded".
    public Team Team { get; set; } = null!;

    public required string Name { get; set; }

    // Venue and capacity are optional the way price already was — a captain may not know
    // them yet, or the franchise may not have a fixed one. A game created from a franchise
    // with either unset must have it filled in as an override before it can be created.
    public string? DefaultVenue { get; set; }
    public int? DefaultCapacity { get; set; }
    public decimal? DefaultPrice { get; set; }

    // An absent day is one the franchise doesn't run.
    public required Dictionary<DayOfWeek, TimeOnly> Schedule { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<Game> Games { get; set; } = [];
}
