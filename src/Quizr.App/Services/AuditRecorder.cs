using System.Text.Json;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Services;

// CLAUDE.md invariant 13: a small, fixed set of captain actions that affect someone else, not
// a general-purpose event log. Unlike NotificationRecorder, AuditEntry has no uniqueness
// constraint to enforce, so this doesn't call SaveChangesAsync itself — the write rides along
// in the caller's own next save, same transaction as the change that caused it.
internal static class AuditRecorder
{
    public static void Record(
        QuizrDb db,
        TeamId teamId,
        GameId? gameId,
        PlayerId? actorPlayerId,
        string action,
        object payload,
        TimeProvider clock
    ) =>
        db.AuditEntries.Add(
            new AuditEntry
            {
                TeamId = teamId,
                GameId = gameId,
                ActorPlayerId = actorPlayerId,
                Action = action,
                Payload = JsonSerializer.Serialize(payload),
                CreatedAt = clock.GetUtcNow(),
            }
        );
}

// ActorPlayerId null means the system did it (the scheduler's auto-finish) — everything else
// always has a captain as the actor. Ordinary self-service Join/Drop already carry their own
// actor via Signup.CancelledByPlayerId; these are for the actions that don't.
internal static class AuditActions
{
    public const string GameDeclined = "GameDeclined";
    public const string GameFinished = "GameFinished";
    public const string PlayerRegisteredOnBehalf = "PlayerRegisteredOnBehalf";
    public const string PlayerDroppedOnBehalf = "PlayerDroppedOnBehalf";
    public const string CaptainGranted = "CaptainGranted";
    public const string CaptainRevoked = "CaptainRevoked";
    public const string ParticipationAttendedToggled = "ParticipationAttendedToggled";
    public const string ParticipationPlayedToggled = "ParticipationPlayedToggled";
    public const string VenuePlayerAdded = "VenuePlayerAdded";
}
