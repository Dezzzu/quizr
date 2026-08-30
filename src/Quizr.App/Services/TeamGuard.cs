using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot;

namespace Quizr.App.Services;

// Authorization checked here, not in the dispatcher (STYLE.md) — so the Telegram handler
// and, in phase 2, the HTTP endpoint get the same answer instead of each remembering to
// check first.
public sealed class TeamGuard
{
    private readonly QuizrDb _db;
    private readonly ITelegramBotClient _bot;

    public TeamGuard(QuizrDb db, ITelegramBotClient bot)
    {
        _db = db;
        _bot = bot;
    }

    // Pure over an already-loaded Team — no reason for this to be async.
    public static Result<Unit> EnsureTimeZoneConfigured(Team team) =>
        team.TimeZoneId is null ? new BusinessError.TeamNotConfigured() : new Unit();

    // Membership.IsCaptain is an explicit grant; a chat admin counts too, checked at
    // runtime rather than cached, so a demotion in Telegram takes effect immediately.
    public async Task<bool> IsCaptainAsync(
        TeamId teamId,
        PlayerId playerId,
        TelegramChatId chatId,
        TelegramUserId telegramUserId,
        CancellationToken ct
    )
    {
        var membership = await _db
            .Memberships.AsNoTracking()
            .SingleOrDefaultAsync(m => m.TeamId == teamId && m.PlayerId == playerId, ct);

        if (membership?.IsCaptain == true)
        {
            return true;
        }

        var chatMember = await _bot.GetChatMember(chatId.Value, telegramUserId.Value, ct);
        return chatMember.IsAdmin;
    }
}
