using Quizr.App.Health;
using Quizr.App.Localization;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;

namespace Quizr.App.Telegram;

// Long polling — nothing connects to the bot to drive it (STACK.md); the calendar feed is the
// one inbound route and has nothing to do with this. allowed_updates is set
// explicitly: Telegram excludes ChatMember unless it's listed, and the failure is silent.
public sealed class BotHostedService : BackgroundService
{
    private static readonly ReceiverOptions ReceiverOptions = new()
    {
        AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.MyChatMember, UpdateType.ChatMember],
    };

    private readonly ITelegramBotClient _bot;
    private readonly IBotInstanceLock _instanceLock;
    private readonly UpdateDispatcher _dispatcher;
    private readonly IStrings _strings;
    private readonly ILogger<BotHostedService> _logger;

    public BotHostedService(
        ITelegramBotClient bot,
        IBotInstanceLock instanceLock,
        UpdateDispatcher dispatcher,
        IStrings strings,
        ILogger<BotHostedService> logger
    )
    {
        _bot = bot;
        _instanceLock = instanceLock;
        _dispatcher = dispatcher;
        _strings = strings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nothing here may touch Telegram until this process is the one in charge — two
        // pollers on one token collide, and getMe is already a call.
        await _instanceLock.WaitForLeadershipAsync(stoppingToken);

        var me = await _bot.GetMe(stoppingToken);
        _logger.LogInformation("Quizr started as @{Username}", me.Username);

        // The "/" suggestion menu — re-registered on every startup so a code change to the
        // command list takes effect on the next deploy with nothing else to remember.
        await CommandMenu.RegisterAsync(_bot, _strings, stoppingToken);

        // Same reasoning, same cadence: the description and short description are re-registered
        // every startup so editing the strings file is all a copy change takes.
        await BotProfile.RegisterAsync(_bot, _strings, stoppingToken);

        await _bot.ReceiveAsync(_dispatcher, ReceiverOptions, stoppingToken);
    }
}
