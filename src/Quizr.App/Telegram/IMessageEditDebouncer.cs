using System.Collections.Concurrent;
using Quizr.Domain;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Telegram;

// Coalesces a burst of edits to the same message into one Telegram call, so a run of
// signups doesn't blow the ~20 messages/minute per-group limit (CLAUDE.md). Every call
// for a message within the debounce window replaces the pending content; only the
// latest is ever sent.
public interface IMessageEditDebouncer
{
    Task ScheduleAsync(
        TelegramChatId chatId,
        TelegramMessageId messageId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken ct
    );
}

public sealed class MessageEditDebouncer : IMessageEditDebouncer
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(1.5);

    private readonly ITelegramBotClient _bot;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(long ChatId, long MessageId), PendingEdit> _pending = new();

    public MessageEditDebouncer(ITelegramBotClient bot, TimeProvider timeProvider)
    {
        _bot = bot;
        _timeProvider = timeProvider;
    }

    public Task ScheduleAsync(
        TelegramChatId chatId,
        TelegramMessageId messageId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken ct
    )
    {
        var key = (chatId.Value, messageId.Value);
        var isFirst = false;

        var pending = _pending.AddOrUpdate(
            key,
            _ =>
            {
                isFirst = true;
                return new PendingEdit(text, keyboard);
            },
            (_, existing) =>
            {
                existing.Text = text;
                existing.Keyboard = keyboard;
                return existing;
            }
        );

        if (isFirst)
        {
            _ = FlushAfterDelayAsync(key, pending, ct);
        }

        return Task.CompletedTask;
    }

    private async Task FlushAfterDelayAsync(
        (long ChatId, long MessageId) key,
        PendingEdit pending,
        CancellationToken ct
    )
    {
        try
        {
            await Task.Delay(DebounceWindow, _timeProvider, ct);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(key, out _);
            return;
        }

        _pending.TryRemove(key, out _);

        await _bot.SendRequest(
            new EditMessageTextRequest
            {
                ChatId = key.ChatId,
                MessageId = (int)key.MessageId,
                Text = pending.Text,
                ParseMode = ParseMode.Html,
                ReplyMarkup = pending.Keyboard,
            },
            ct
        );
    }

    private sealed class PendingEdit
    {
        public PendingEdit(string text, InlineKeyboardMarkup? keyboard)
        {
            Text = text;
            Keyboard = keyboard;
        }

        public string Text { get; set; }
        public InlineKeyboardMarkup? Keyboard { get; set; }
    }
}
