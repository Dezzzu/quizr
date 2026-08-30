using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Data;

// A migrated chat (basic group -> supergroup) can still deliver a straggling update tagged
// with its pre-migration id — one already in flight when Telegram completed the upgrade,
// before every later update settles on the new one (TeamChatMigration, CLAUDE.md's
// Telegram-migration note). Every "which team does this incoming chat id belong to" lookup
// needs to match either id, not just the current one — centralized here rather than repeated
// as "|| t.OldChatId == chatId" at each call site, the same reasoning as TeamConfiguration's
// own query filter for DeactivatedAt.
internal static class TeamLookup
{
    public static IQueryable<Team> ByChatId(this IQueryable<Team> teams, TelegramChatId chatId) =>
        teams.Where(t => t.ChatId == chatId || t.OldChatId == chatId);
}
