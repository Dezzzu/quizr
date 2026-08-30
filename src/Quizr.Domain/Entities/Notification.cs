namespace Quizr.Domain.Entities;

// The dedup record for outbound notifications, written in the same transaction
// as the change that caused it — unique on (SignupId, Kind). See CLAUDE.md's
// Conventions section for why this exists instead of a lock.
public sealed class Notification
{
    public long Id { get; set; }
    public required SignupId SignupId { get; set; }
    public NotificationKind Kind { get; set; }
    public DateTimeOffset SentAt { get; set; }
}
