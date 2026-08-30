using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;

namespace Quizr.App.Telegram;

// Long polling — nothing ever connects to the bot (STACK.md). allowed_updates is set
// explicitly: Telegram excludes ChatMember unless it's listed, and the failure is silent.
public sealed class BotHostedService : BackgroundService
{
    private static readonly ReceiverOptions ReceiverOptions = new()
    {
        AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.MyChatMember, UpdateType.ChatMember],
    };

    private readonly ITelegramBotClient _bot;
    private readonly UpdateDispatcher _dispatcher;
    private readonly ILogger<BotHostedService> _logger;

    public BotHostedService(ITelegramBotClient bot, UpdateDispatcher dispatcher, ILogger<BotHostedService> logger)
    {
        _bot = bot;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await _bot.GetMe(stoppingToken);
        _logger.LogInformation("Quizr started as @{Username}", me.Username);

        await _bot.ReceiveAsync(_dispatcher, ReceiverOptions, stoppingToken);
    }
}
