using Quizr.Domain;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Telegram;

// The one place that sends or edits a chat message. Sends go straight through; edits go
// through the debouncer so a burst of changes becomes one edit. HTML parse mode
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

    // Bypasses the debouncer and reports whether the message still exists to edit — the
    // Board (BoardService) needs that answer synchronously, to know whether to repost,
    // which a fire-and-forget debounced edit can never give it.
    Task<bool> TryEditImmediatelyAsync(
        TelegramChatId chatId,
        TelegramMessageId messageId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken ct
    );
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

    // Telegram's description for editing a deleted message; there's no dedicated error code
    // for it, just this text on a 400.
    private static bool IsMessageGone(ApiRequestException ex) =>
        ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("MESSAGE_ID_INVALID", StringComparison.OrdinalIgnoreCase);

    // Same story as IsMessageGone: no dedicated error code, just this text on a 400.
    private static bool IsUnmodified(ApiRequestException ex) =>
        ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);
}
