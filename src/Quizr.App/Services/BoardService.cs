using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Rendering;
using Quizr.App.Telegram;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot;
using Telegram.Bot.Requests;

namespace Quizr.App.Services;

// The one pinned message per chat (CLAUDE.md invariant 12). Verified and silently restored:
// reposting from the database if it's gone, re-pinning if it's been displaced. This is the
// mechanism; the periodic call that makes restoration happen without anyone acting is the
// scheduler's job (STACK.md, M6) — RefreshAsync just needs to be safe to call repeatedly.
public sealed class BoardService
{
    private readonly QuizrDb _db;
    private readonly IMessageSender _sender;
    private readonly ITelegramBotClient _bot;
    private readonly IStrings _strings;

    public BoardService(QuizrDb db, IMessageSender sender, ITelegramBotClient bot, IStrings strings)
    {
        _db = db;
        _sender = sender;
        _bot = bot;
        _strings = strings;
    }

    public async Task RefreshAsync(Team team, CancellationToken ct)
    {
        var upcomingGames = await _db
            .Games.AsNoTracking()
            .Where(g => g.TeamId == team.Id && g.FinishedAt == null && g.DeclinedAt == null)
            .OrderBy(g => g.StartsAt)
            .ToListAsync(ct);

        var strings = _strings.For(team.Locale);
        var text = BoardRenderer.RenderText(upcomingGames, team.ChatId, team.TimeZoneId!, strings);

        if (
            team.BoardMessageId is { } messageId
            && await _sender.TryEditImmediatelyAsync(team.ChatId, messageId, text, null, ct)
        )
        {
            await EnsurePinnedAsync(team.ChatId, messageId, ct);
            return;
        }

        await PostAndPinAsync(team, text, ct);
    }

    private async Task PostAndPinAsync(Team team, string text, CancellationToken ct)
    {
        var messageId = await _sender.SendAsync(team.ChatId, text, null, ct);
        team.BoardMessageId = messageId;
        await _db.SaveChangesAsync(ct);

        await PinAsync(team.ChatId, messageId, ct);
    }

    private async Task EnsurePinnedAsync(TelegramChatId chatId, TelegramMessageId messageId, CancellationToken ct)
    {
        var chat = await _bot.SendRequest(new GetChatRequest { ChatId = chatId.Value }, ct);
        if (chat.PinnedMessage?.MessageId != (int)messageId.Value)
        {
            await PinAsync(chatId, messageId, ct);
        }
    }

    private async Task PinAsync(TelegramChatId chatId, TelegramMessageId messageId, CancellationToken ct) =>
        await _bot.SendRequest(
            new PinChatMessageRequest
            {
                ChatId = chatId.Value,
                MessageId = (int)messageId.Value,
                // "Restores it silently" — CLAUDE.md invariant 12. A re-pin the team never
                // asked for shouldn't ping everyone in the chat.
                DisableNotification = true,
            },
            ct
        );
}
