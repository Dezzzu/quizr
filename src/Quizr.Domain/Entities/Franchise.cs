namespace Quizr.Domain.Entities;

// Team-scoped. Captains create and edit them; there is no global catalogue.
public sealed class Franchise
{
    public FranchiseId Id { get; set; }
    public required TeamId TeamId { get; set; }
    public required string Name { get; set; }
    public required string DefaultVenue { get; set; }
    public int DefaultCapacity { get; set; }
    public decimal? DefaultPrice { get; set; }

    // An absent day is one the franchise doesn't run.
    public required Dictionary<DayOfWeek, TimeOnly> Schedule { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
