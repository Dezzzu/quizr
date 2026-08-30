namespace Quizr.Domain.Entities;

// Per team: a person in two teams wants different settings, and the team owns
// the timezone the reminder slots resolve in. Unique on (TeamId, PlayerId).
public sealed class Membership
{
    public required TeamId TeamId { get; set; }
    public required PlayerId PlayerId { get; set; }

    // Require .Include(m => m.Team) / .Include(m => m.Player); null only means "not loaded".
    public Team Team { get; set; } = null!;
    public Player Player { get; set; } = null!;

    // Explicit grant; chat admins also count, checked at runtime.
    public bool IsCaptain { get; set; }

    public ReminderChannel EveningBefore { get; set; } = ReminderChannel.Off;
    public ReminderChannel MorningOf { get; set; } = ReminderChannel.Off;
    public ReminderChannel BeforeStart { get; set; } = ReminderChannel.Off;
    public bool RemindWhenReserve { get; set; }

    public DateTimeOffset JoinedAt { get; set; }
}
