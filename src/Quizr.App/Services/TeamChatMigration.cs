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
//
// The old id isn't discarded — it's kept on Team.OldChatId so a straggling incoming update
// still tagged with it (one already in flight when the upgrade completed) still resolves to
// this team; see TeamLookup, which every "find the team for this chat id" query goes through.
//
// That conflict is the common case, not the exception: Telegram's own my_chat_member "added"
// event for the new chat id looks identical to a genuine new addition, so
// TeamBootstrapService almost always bootstraps a fresh, empty team for it before this ever
// runs. Which one is "the team" is decided by whether that fresh team was actually
// configured (a timezone set, a Board ever posted) — not by which one merely exists first.
// An unconfigured bootstrap team is a placeholder nobody built on; retiring it and moving the
// real team's games/franchises/captains onto the new chat id is what keeps a captain's
// history from silently vanishing the moment their group happens to upgrade to a supergroup.
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
        var oldChatId = team.ChatId;

        // Team's global query filter (TeamConfiguration) already limits this to the active
        // team, if any — exactly what "is this chat id taken" needs to ask.
        var conflicting = await db.Teams.SingleOrDefaultAsync(t => t.ChatId == newChatId, ct);
        if (conflicting is null)
        {
            logger.LogInformation(
                "Team {TeamId}'s chat migrated from {OldChatId} to {NewChatId}",
                team.Id.Value,
                team.ChatId.Value,
                newChatId.Value
            );
            team.ChatId = newChatId;
            team.OldChatId = oldChatId;
            await MoveActiveDialogsAsync(db, oldChatId, newChatId, ct);
        }
        else if (conflicting.TimeZoneId is null || conflicting.BoardMessageId is null)
        {
            // TeamBootstrapService's own my_chat_member "added" event for the new chat id
            // bootstrapped a fresh team before this ran, but nobody's actually configured it
            // yet (no /settimezone, no Board ever posted) — it's the placeholder, not the
            // team. Retire it (invariant 7: a state change, not a delete) and give the real
            // team, with its games/franchises/captains, the new chat id instead of losing all
            // of that to an empty row nobody built on.
            //
            // Saved in its own round trip, before team.ChatId is set to the same value: EF
            // Core doesn't order unrelated updates within one SaveChanges batch, and the
            // filtered unique index only excludes conflicting once its own row is actually
            // committed as deactivated.
            conflicting.DeactivatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "Team {TeamId}'s chat migrated to {NewChatId} — retiring the unused bootstrap team {ConflictingTeamId} in its place",
                team.Id.Value,
                newChatId.Value,
                conflicting.Id.Value
            );
            team.ChatId = newChatId;
            team.OldChatId = oldChatId;
            await MoveActiveDialogsAsync(db, oldChatId, newChatId, ct);
        }
        else
        {
            // The fresh team was actually configured before this ran — a captain reached it
            // first, so it's the real active continuation. Retire the old team instead,
            // rather than collide with the fresh one on Team.ChatId's unique index. Any
            // DialogState still keyed to the old chat id belongs to a team that's now retired,
            // not to conflicting — left alone here, it self-expires (SchedulerService's
            // 20-minute cutoff) rather than being moved onto a team it was never part of.
            team.DeactivatedAt = clock.GetUtcNow();
            logger.LogWarning(
                "Team {TeamId}'s chat migrated to {NewChatId}, already configured as team {ConflictingTeamId} — retiring the old team",
                team.Id.Value,
                newChatId.Value,
                conflicting.Id.Value
            );
        }

        await db.SaveChangesAsync(ct);
    }

    // Telegram tags every update for a migrated chat with the new id from the moment the
    // migration completes, including callback queries on messages posted before it — so a
    // DialogState row still keyed to the old id (a captain mid-wizard at the exact instant of
    // migration) becomes unreachable the moment the next reply arrives, and the wizard breaks
    // silently rather than picking up where it left off. Bulk update, matching
    // SchedulerService's own ExecuteDeleteAsync precedent for this table — there's normally at
    // most one row to move.
    private static async Task MoveActiveDialogsAsync(
        QuizrDb db,
        TelegramChatId oldChatId,
        TelegramChatId newChatId,
        CancellationToken ct
    ) =>
        await db
            .DialogStates.Where(d => d.ChatId == oldChatId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ChatId, newChatId), ct);
}
