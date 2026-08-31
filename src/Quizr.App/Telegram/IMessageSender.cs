using Quizr.Domain;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
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

    // Posts into the chat but visible to one member only (Bot API 10.2). Pass the callback
    // query that prompted it where there is one, so Telegram can tie the two together.
    //
    // Delivery is best-effort by design — the API says so of every ephemeral operation,
    // "especially if they are offline" — which is tolerable here only because the database is
    // the source of truth and a message nobody saw costs nothing (CLAUDE.md).
    Task<MessageRef> SendEphemeralAsync(
        TelegramChatId chatId,
        TelegramUserId receiver,
        string text,
        InlineKeyboardMarkup? keyboard,
        string? callbackQueryId,
        CancellationToken ct
    );

    // The MessageRef overloads are what callers holding a message of either kind use; the
    // chat-plus-id ones above remain for the callers that can only ever have an ordinary
    // message (the Board, the announcement).
    Task<bool> TryEditImmediatelyAsync(
        MessageRef message,
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

    Task RemoveKeyboardAsync(MessageRef message, CancellationToken ct);
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

    public async Task<MessageRef> SendEphemeralAsync(
        TelegramChatId chatId,
        TelegramUserId receiver,
        string text,
        InlineKeyboardMarkup? keyboard,
        string? callbackQueryId,
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
                EphemeralMessageParameters = new EphemeralMessageParameters
                {
                    ReceiverUserId = receiver.Value,
                    CallbackQueryId = callbackQueryId,
                },
            },
            ct
        );

        // Message.Id is 0 here; EphemeralMessageId is the handle every later edit needs. Its
        // absence would mean Telegram accepted an ephemeral send without giving anything back
        // to address it — a broken assumption rather than a business failure, so it throws
        // instead of quietly handing out a ref pointing at message 0.
        var ephemeralId =
            message.EphemeralMessageId
            ?? throw new InvalidOperationException(
                "Telegram returned an ephemeral message with no EphemeralMessageId."
            );

        return MessageRef.Ephemeral(chatId, new TelegramMessageId(ephemeralId), receiver);
    }

    public Task EditAsync(
        TelegramChatId chatId,
        TelegramMessageId messageId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken ct
    ) => _debouncer.ScheduleAsync(chatId, messageId, text, keyboard, ct);

    public Task<bool> TryEditImmediatelyAsync(
        TelegramChatId chatId,
        TelegramMessageId messageId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken ct
    ) => TryEditImmediatelyAsync(MessageRef.Ordinary(chatId, messageId), text, keyboard, ct);

    public async Task<bool> TryEditImmediatelyAsync(
        MessageRef message,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken ct
    )
    {
        try
        {
            if (message.ReceiverUserId is { } receiver)
            {
                await _bot.SendRequest(
                    new EditEphemeralMessageTextRequest
                    {
                        ChatId = message.ChatId.Value,
                        ReceiverUserId = receiver.Value,
                        EphemeralMessageId = (int)message.Id.Value,
                        Text = text,
                        ParseMode = ParseMode.Html,
                        ReplyMarkup = keyboard,
                    },
                    ct
                );
                return true;
            }

            await _bot.SendRequest(
                new EditMessageTextRequest
                {
                    ChatId = message.ChatId.Value,
                    MessageId = (int)message.Id.Value,
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

    public Task RemoveKeyboardAsync(TelegramChatId chatId, TelegramMessageId messageId, CancellationToken ct) =>
        RemoveKeyboardAsync(MessageRef.Ordinary(chatId, messageId), ct);

    public async Task RemoveKeyboardAsync(MessageRef message, CancellationToken ct)
    {
        try
        {
            if (message.ReceiverUserId is { } receiver)
            {
                await _bot.SendRequest(
                    new EditEphemeralMessageReplyMarkupRequest
                    {
                        ChatId = message.ChatId.Value,
                        ReceiverUserId = receiver.Value,
                        EphemeralMessageId = (int)message.Id.Value,
                        ReplyMarkup = null,
                    },
                    ct
                );
                return;
            }

            await _bot.SendRequest(
                new EditMessageReplyMarkupRequest
                {
                    ChatId = message.ChatId.Value,
                    MessageId = (int)message.Id.Value,
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
