using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.Domain;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Quizr.App.Telegram;

// One DI scope per update (STYLE.md) and the one broad catch STYLE.md allows: a single
// failing handler must never take the bot down. Registered singleton — it only holds
// scope-safe dependencies, and Telegram.Bot needs one long-lived IUpdateHandler.
public sealed class UpdateDispatcher : IUpdateHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UpdateDispatcher> _logger;

    public UpdateDispatcher(IServiceScopeFactory scopeFactory, ILogger<UpdateDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task HandleUpdateAsync(
        ITelegramBotClient botClient,
        Update update,
        CancellationToken cancellationToken
    )
    {
        using var scope = _scopeFactory.CreateScope();
        using var logScope = _logger.BeginScope(
            new Dictionary<string, object?>
            {
                ["UpdateId"] = update.Id,
                ["ChatId"] = ChatIdOf(update),
                ["UserId"] = UserIdOf(update),
            }
        );

        try
        {
            await scope.ServiceProvider.GetRequiredService<UpdateRouter>().RouteAsync(update, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception handling update {UpdateId}", update.Id);

            // Best-effort: the alert is what matters here, so a failure sending it (or the
            // apology below) must not turn into an unhandled exception of its own.
            try
            {
                await scope
                    .ServiceProvider.GetRequiredService<IAlertSender>()
                    .AlertAsync(ex, update, cancellationToken);
            }
            catch (Exception alertEx)
            {
                _logger.LogError(alertEx, "Failed to send the alert for update {UpdateId}", update.Id);
            }

            try
            {
                await SendApologyAsync(scope.ServiceProvider, ChatIdOf(update), cancellationToken);
            }
            catch (Exception apologyEx)
            {
                _logger.LogError(apologyEx, "Failed to send the apology for update {UpdateId}", update.Id);
            }
        }
    }

    private static async Task SendApologyAsync(IServiceProvider services, long? chatId, CancellationToken ct)
    {
        if (chatId is null)
        {
            return;
        }

        var db = services.GetRequiredService<QuizrDb>();
        var team = await db
            .Teams.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ChatId == new TelegramChatId(chatId.Value), ct);
        var strings = services.GetRequiredService<IStrings>();
        var apology = strings.For(team?.Locale ?? "en").Text("Error.Generic");

        await services
            .GetRequiredService<IMessageSender>()
            .SendAsync(new TelegramChatId(chatId.Value), apology, null, ct);
    }

    public Task HandleErrorAsync(
        ITelegramBotClient botClient,
        Exception exception,
        HandleErrorSource source,
        CancellationToken cancellationToken
    )
    {
        // Transient network faults while polling, not a failed update — ReceiveAsync's own
        // loop keeps retrying, so there's nothing else to do here but record it.
        _logger.LogError(exception, "Polling error from {Source}", source);
        return Task.CompletedTask;
    }

    private static long? ChatIdOf(Update update) =>
        update.Type switch
        {
            UpdateType.Message => update.Message?.Chat.Id,
            UpdateType.CallbackQuery => update.CallbackQuery?.Message?.Chat.Id,
            UpdateType.MyChatMember => update.MyChatMember?.Chat.Id,
            UpdateType.ChatMember => update.ChatMember?.Chat.Id,
            _ => null,
        };

    private static long? UserIdOf(Update update) =>
        update.Type switch
        {
            UpdateType.Message => update.Message?.From?.Id,
            UpdateType.CallbackQuery => update.CallbackQuery?.From.Id,
            UpdateType.MyChatMember => update.MyChatMember?.From.Id,
            UpdateType.ChatMember => update.ChatMember?.From.Id,
            _ => null,
        };
}
