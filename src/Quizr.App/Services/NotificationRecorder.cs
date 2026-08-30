using Microsoft.EntityFrameworkCore;
using Npgsql;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Services;

// The dedup mechanism from CLAUDE.md's Conventions: a notifications table keyed
// (SignupId, Kind) with a unique constraint, so two near-simultaneous changes that both
// conclude the same person moved up produce one message, not two. No lock — a duplicate
// insert is simply rejected and the caller treats that as "already handled".
internal static class NotificationRecorder
{
    public static async Task<bool> TryRecordAsync(
        QuizrDb db,
        SignupId signupId,
        NotificationKind kind,
        TimeProvider clock,
        CancellationToken ct
    )
    {
        var notification = new Notification
        {
            SignupId = signupId,
            Kind = kind,
            SentAt = clock.GetUtcNow(),
        };
        db.Notifications.Add(notification);

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(notification).State = EntityState.Detached;
            return false;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
