using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quizr.App.Localization;
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
    private readonly IStrings _strings;
    private readonly ILogger<BotHostedService> _logger;

    public BotHostedService(
        ITelegramBotClient bot,
        UpdateDispatcher dispatcher,
        IStrings strings,
        ILogger<BotHostedService> logger
    )
    {
        _bot = bot;
        _dispatcher = dispatcher;
        _strings = strings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await _bot.GetMe(stoppingToken);
        _logger.LogInformation("Quizr started as @{Username}", me.Username);

        // The "/" suggestion menu — re-registered on every startup so a code change to the
        // command list takes effect on the next deploy with nothing else to remember.
        await CommandMenu.RegisterAsync(_bot, _strings, stoppingToken);

        await _bot.ReceiveAsync(_dispatcher, ReceiverOptions, stoppingToken);
    }
}
