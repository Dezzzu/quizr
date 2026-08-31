using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot;

namespace Quizr.App.Services;

// The mechanism behind STYLE.md's "authorization is checked in the application service, not
// the dispatcher" — so the Telegram handler and, in phase 2, the HTTP endpoint get the same
// answer instead of each remembering to check first. Only services call this; the router
// reads the BusinessError.NotCaptain they return.
public sealed class TeamGuard
{
    private readonly QuizrDb _db;
    private readonly ITelegramBotClient _bot;

    // One DI scope is one update (STYLE.md), so a captain's status cannot change midway
    // through handling one. Without this, moving the check into every service turned a single
    // tap into one GetChatMember call per service the handler touches — opening the manage-
    // players view alone loads a dialog and a member list — against an API this bot is
    // already rate-limit-conscious about.
    private readonly Dictionary<(TeamId TeamId, PlayerId PlayerId), bool> _answers = [];

    public TeamGuard(QuizrDb db, ITelegramBotClient bot)
    {
        _db = db;
        _bot = bot;
    }

    // Pure over an already-loaded Team — no reason for this to be async.
    public static Result<Unit> EnsureTimeZoneConfigured(Team team) =>
        team.TimeZoneId is null ? new BusinessError.TeamNotConfigured() : new Unit();

    // What a captain-only service method calls: one line, and the failure is a value the
    // caller already knows how to render.
    public async Task<Result<Unit>> RequireCaptainAsync(Team team, Actor actor, CancellationToken ct) =>
        await IsCaptainAsync(team, actor, ct) ? new Unit() : new BusinessError.NotCaptain();

    // Membership.IsCaptain is an explicit grant; a chat admin counts too, checked at
    // runtime rather than cached across updates, so a demotion in Telegram takes effect
    // immediately.
    public async Task<bool> IsCaptainAsync(Team team, Actor actor, CancellationToken ct)
    {
        if (_answers.TryGetValue((team.Id, actor.PlayerId), out var answered))
        {
            return answered;
        }

        var isCaptain = await ResolveAsync(team, actor, ct);
        _answers[(team.Id, actor.PlayerId)] = isCaptain;
        return isCaptain;
    }

    private async Task<bool> ResolveAsync(Team team, Actor actor, CancellationToken ct)
    {
        var membership = await _db
            .Memberships.AsNoTracking()
            .SingleOrDefaultAsync(m => m.TeamId == team.Id && m.PlayerId == actor.PlayerId, ct);

        if (membership?.IsCaptain == true)
        {
            return true;
        }

        var chatMember = await _bot.GetChatMember(team.ChatId.Value, actor.TelegramUserId.Value, ct);
        return chatMember.IsAdmin;
    }
}
