using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Services;

// Telegram permanently invalidates a group's chat id the moment it's upgraded to a
// supergroup — every send against the old id then fails with a 400 forever, unless the
// team's stored ChatId is updated to the new one. Two independent triggers both need to
// react to this the same way: UpdateRouter, proactively, from the migrate system message
// Telegram delivers to the old chat (a Message with no text, otherwise indistinguishable
// from noise and easy to miss); SchedulerService, reactively, from the API error every
// subsequent send against the stale id keeps returning until this runs. Centralized so both
// apply the same conflict rule rather than risking two different answers to "what if a team
// already owns the new chat id."
internal static class TeamChatMigration
{
    public static async Task ApplyAsync(
        QuizrDb db,
        Team team,
        TelegramChatId newChatId,
        TimeProvider clock,
        ILogger logger,
        CancellationToken ct
    )
    {
        var conflicting = await db.Teams.SingleOrDefaultAsync(t => t.ChatId == newChatId, ct);
        if (conflicting is not null)
        {
            // TeamBootstrapService already bootstrapped a fresh team for the new chat id —
            // Telegram's own my_chat_member "added" event for it looks identical to a
            // genuine new addition, and nothing stops it arriving before this does — so the
            // fresh team is the active continuation. Retire this one (invariant 7: a state
            // change, not a delete) rather than fail here forever or collide with the other
            // team on Team.ChatId's unique index.
            team.DeactivatedAt = clock.GetUtcNow();
            logger.LogWarning(
                "Team {TeamId}'s chat migrated to {NewChatId}, already owned by team {ConflictingTeamId} — retiring the old team",
                team.Id.Value,
                newChatId.Value,
                conflicting.Id.Value
            );
        }
        else
        {
            logger.LogInformation(
                "Team {TeamId}'s chat migrated from {OldChatId} to {NewChatId}",
                team.Id.Value,
                team.ChatId.Value,
                newChatId.Value
            );
            team.ChatId = newChatId;
        }

        await db.SaveChangesAsync(ct);
    }
}
