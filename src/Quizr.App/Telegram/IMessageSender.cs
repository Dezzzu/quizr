using Quizr.Domain;
using Telegram.Bot;
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
}
