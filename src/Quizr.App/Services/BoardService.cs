using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Rendering;
using Quizr.App.Telegram;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;

namespace Quizr.App.Services;

// The one pinned message per chat (CLAUDE.md invariant 12). Silently restored: reposting from
// the database if it's gone, re-pinning unconditionally otherwise — Telegram's own signal for
// "is this still pinned" (Chat.PinnedMessage) doesn't reliably reflect an unpin performed by
// anyone other than the bot, so there's nothing trustworthy to check before deciding whether
// to act. This is the mechanism; the periodic call that makes restoration happen without
// anyone acting is the scheduler's job (STACK.md, M6) — RefreshAsync just needs to be safe to
// call repeatedly.
public sealed class BoardService
{
    private readonly QuizrDb _db;
    private readonly IMessageSender _sender;
    private readonly ITelegramBotClient _bot;
    private readonly IStrings _strings;
    private readonly ILogger<BoardService> _logger;

    public BoardService(
        QuizrDb db,
        IMessageSender sender,
        ITelegramBotClient bot,
        IStrings strings,
        ILogger<BoardService> logger
    )
    {
        _db = db;
        _sender = sender;
        _bot = bot;
        _strings = strings;
        _logger = logger;
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
            await PinAsync(team.ChatId, messageId, ct);
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

    // Unconditional, every tick — no "is it already pinned?" check first. getChat's own
    // pinned_message field is the obvious way to ask that, but it doesn't reliably reflect an
    // unpin someone other than the bot performed (confirmed against a live chat), so the only
    // trustworthy way to "verify the pin" (invariant 12) is to just restore it every time and
    // let Telegram itself no-op when nothing changed.
    private async Task PinAsync(TelegramChatId chatId, TelegramMessageId messageId, CancellationToken ct)
    {
        try
        {
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
        catch (ApiRequestException ex)
            when (ex.Message.Contains("not enough rights", StringComparison.OrdinalIgnoreCase))
        {
            // The bot isn't a chat admin yet (CLAUDE.md's Telegram constraints) — expected
            // before a captain promotes it, not a bug. Swallowed here rather than left to
            // propagate: uncaught, this fires UpdateDispatcher's unhandled-exception alert
            // and apology message for every captain action until someone promotes the bot,
            // even though invariant 12 already calls this case "restores it silently" — the
            // scheduler's next tick (30s) retries unconditionally and succeeds the moment
            // admin rights exist, with nothing else to do here in the meantime. Matched on
            // the API's own wording, not just any ApiRequestException, so an unrelated pin
            // failure still surfaces as the genuine bug it would be.
            _logger.LogInformation("Board pin skipped for chat {ChatId}: the bot isn't a chat admin yet", chatId);
        }
        catch (ApiRequestException ex) when (IsAlreadyPinned(ex))
        {
            // Pinning the message that's already pinned — the common case now that this runs
            // unconditionally every tick instead of only after detecting a mismatch. Not an
            // error, just Telegram saying there was nothing to do.
        }
    }

    // Telegram's wording for "you asked to pin the message that's already pinned" — no
    // dedicated error code for it, just this text (or CHAT_NOT_MODIFIED) on a 400.
    private static bool IsAlreadyPinned(ApiRequestException ex) =>
        ex.Message.Contains("CHAT_NOT_MODIFIED", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("message is already pinned", StringComparison.OrdinalIgnoreCase);
}
