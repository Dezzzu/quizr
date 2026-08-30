using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Rendering;
using Quizr.App.Telegram;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Services;

// CLAUDE.md's rule everything else follows: the database is the source of truth, chat
// messages are generated views. Every signup mutation ends with a call here to rewrite the
// announcement from what's actually in the database — never a targeted edit of the old text.
public sealed class AnnouncementService
{
    private readonly QuizrDb _db;
    private readonly IMessageSender _sender;
    private readonly IStrings _strings;

    public AnnouncementService(QuizrDb db, IMessageSender sender, IStrings strings)
    {
        _db = db;
        _sender = sender;
        _strings = strings;
    }

    public async Task<TelegramMessageId> PostAsync(Game game, Team team, CancellationToken ct)
    {
        var (text, keyboard) = await RenderAsync(game, team, ct);
        return await _sender.SendAsync(team.ChatId, text, keyboard, ct);
    }

    public async Task RefreshAsync(Game game, Team team, CancellationToken ct)
    {
        if (game.AnnouncementMessageId is not { } messageId)
        {
            return;
        }

        var (text, keyboard) = await RenderAsync(game, team, ct);
        await _sender.EditAsync(team.ChatId, messageId, text, keyboard, ct);
    }

    private async Task<(string Text, InlineKeyboardMarkup Keyboard)> RenderAsync(
        Game game,
        Team team,
        CancellationToken ct
    )
    {
        var signups = await _db
            .Signups.AsNoTracking()
            .Where(s => s.GameId == game.Id && s.CancelledAt == null)
            .ToListAsync(ct);
        var roster = Roster.Split(signups, game.Capacity);

        var playerIds = signups
            .Select(s => s.PlayerId)
            .Concat(signups.Select(s => s.InvitedByPlayerId))
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var players = await _db
            .Players.AsNoTracking()
            .Where(p => playerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var strings = _strings.For(team.Locale);

        return (
            AnnouncementRenderer.RenderText(game, roster, players, team.TimeZoneId!, strings),
            AnnouncementRenderer.RenderKeyboard(game.Id, strings)
        );
    }
}
