using System.Net;
using Microsoft.Extensions.Logging;
using Quizr.Domain;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Quizr.App.Telegram;

// "On an unhandled exception, message a private channel" (CLAUDE.md) — logs say what
// happened, this says it now. Deliberately bypasses IMessageSender: the alert path
// must not depend on the same debouncer it might be alerting about.
public interface IAlertSender
{
    Task AlertAsync(Exception exception, Update update, CancellationToken ct);
}

public sealed class AlertSender : IAlertSender
{
    // Telegram caps a message at 4096 UTF-16 characters; leave room for the wrapper text.
    private const int MaxDetailLength = 3500;

    private readonly ITelegramBotClient _bot;
    private readonly TelegramChatId? _alertChatId;
    private readonly ILogger<AlertSender> _logger;

    public AlertSender(ITelegramBotClient bot, TelegramChatId? alertChatId, ILogger<AlertSender> logger)
    {
        _bot = bot;
        _alertChatId = alertChatId;
        _logger = logger;
    }

    public async Task AlertAsync(Exception exception, Update update, CancellationToken ct)
    {
        if (_alertChatId is null)
        {
            _logger.LogWarning(
                exception,
                "QUIZR_ALERT_CHAT_ID is not configured; alert for update {UpdateId} was only logged",
                update.Id
            );
            return;
        }

        var detail = exception.ToString();
        if (detail.Length > MaxDetailLength)
        {
            detail = detail[..MaxDetailLength];
        }

        await _bot.SendRequest(
            new SendMessageRequest
            {
                ChatId = _alertChatId.Value.Value,
                Text = $"Unhandled exception on update {update.Id}:\n<pre>{WebUtility.HtmlEncode(detail)}</pre>",
                ParseMode = ParseMode.Html,
            },
            ct
        );
    }
}
