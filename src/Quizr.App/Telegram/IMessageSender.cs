using Quizr.Domain;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Telegram;

// The one place that sends or edits a chat message. Sends go straight through. Of every
// edited message, only the game announcement can have several different people's edits
// land on it at once (everyone joining/dropping/bringing a guest to the same game) — that's
// the one edit that goes through the debouncer (AnnouncementService.RefreshAsync is its sole
// caller). Every other edited message belongs to a single dialog or menu that only its own
// opener can ever tap into, so it uses TryEditImmediatelyAsync instead — there's no burst
// from anyone else to coalesce, so debouncing there would only add latency. HTML parse mode
// throughout — CLAUDE.md: MarkdownV2's escaping rules eventually break on somebody's name.
public interface IMessageSender
{
    Task<TelegramMessageId> SendAsync(
        TelegramChatId chatId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken ct
    );

    Task EditAsync(
        TelegramChatId chatId,
        TelegramMessageId messageId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken ct
    );

    // Bypasses the debouncer and reports whether the message still exists to edit. Board
    // needs that answer synchronously, to know whether to repost. Every private dialog/menu
    // edit in UpdateRouter uses this too — same reasoning, plus it means those edits land
    // without the debounce window's delay.
    Task<bool> TryEditImmediatelyAsync(
        TelegramChatId chatId,
        TelegramMessageId messageId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken ct
    );

    // Strips a message's inline keyboard without touching its text — for a wizard prompt
    // that's been answered (by text, or a Skip tap) and moved on before its own button was
    // ever pressed. Left alone, that keyboard sits in the chat looking tappable long after it
    // stopped meaning anything. Same swallow list as TryEditImmediatelyAsync: the message may
    // already be gone, or its keyboard already gone.
    Task RemoveKeyboardAsync(TelegramChatId chatId, TelegramMessageId messageId, CancellationToken ct);
}

public sealed class MessageSender : IMessageSender
{
    private readonly ITelegramBotClient _bot;
    private readonly IMessageEditDebouncer _debouncer;

    public MessageSender(ITelegramBotClient bot, IMessageEditDebouncer debouncer)
    {
        _bot = bot;
        _debouncer = debouncer;
    }

    public async Task<TelegramMessageId> SendAsync(
        TelegramChatId chatId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken ct
    )
    {
        var message = await _bot.SendRequest(
            new SendMessageRequest
            {
                ChatId = chatId.Value,
                Text = text,
                ParseMode = ParseMode.Html,
                ReplyMarkup = keyboard,
            },
            ct
        );

        return new TelegramMessageId(message.Id);
    }

    public Task EditAsync(
        TelegramChatId chatId,
        TelegramMessageId messageId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken ct
    ) => _debouncer.ScheduleAsync(chatId, messageId, text, keyboard, ct);

    public async Task<bool> TryEditImmediatelyAsync(
        TelegramChatId chatId,
        TelegramMessageId messageId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken ct
    )
    {
        try
        {
            await _bot.SendRequest(
                new EditMessageTextRequest
                {
                    ChatId = chatId.Value,
                    MessageId = (int)messageId.Value,
                    Text = text,
                    ParseMode = ParseMode.Html,
                    ReplyMarkup = keyboard,
                },
                ct
            );
            return true;
        }
        catch (ApiRequestException ex) when (IsMessageGone(ex))
        {
            return false;
        }
        catch (ApiRequestException ex) when (IsUnmodified(ex))
        {
            // The current content already matches what was asked for — e.g. the scheduler's
            // board refresh finding nothing changed since the last tick. Telegram rejects a
            // no-op edit as a 400 rather than silently succeeding; this is that silence.
            return true;
        }
    }

    public async Task RemoveKeyboardAsync(TelegramChatId chatId, TelegramMessageId messageId, CancellationToken ct)
    {
        try
        {
            await _bot.SendRequest(
                new EditMessageReplyMarkupRequest
                {
                    ChatId = chatId.Value,
                    MessageId = (int)messageId.Value,
                    ReplyMarkup = null,
                },
                ct
            );
        }
        catch (ApiRequestException ex) when (IsMessageGone(ex) || IsUnmodified(ex)) { }
    }

    // Telegram's description for editing a deleted message; there's no dedicated error code
    // for it, just this text on a 400.
    private static bool IsMessageGone(ApiRequestException ex) =>
        ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("MESSAGE_ID_INVALID", StringComparison.OrdinalIgnoreCase);

    // Same story as IsMessageGone: no dedicated error code, just this text on a 400.
    private static bool IsUnmodified(ApiRequestException ex) =>
        ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);
}
