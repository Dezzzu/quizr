using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Quizr.App.Telegram;

// Switches on update type and, for messages, on the parsed command. Scoped — one instance
// per update, sharing the DbContext UpdateDispatcher's scope created. See STACK.md: "Update
// dispatch — switch on update type, match callback-data prefixes to DI-resolved handlers."
public sealed class UpdateRouter
{
    private readonly QuizrDb _db;
    private readonly IMessageSender _sender;
    private readonly ITelegramBotClient _bot;
    private readonly IStrings _strings;
    private readonly TeamBootstrapService _teamBootstrap;
    private readonly PlayerBootstrapService _playerBootstrap;
    private readonly TeamGuard _teamGuard;
    private readonly ILogger<UpdateRouter> _logger;

    public UpdateRouter(
        QuizrDb db,
        IMessageSender sender,
        ITelegramBotClient bot,
        IStrings strings,
        TeamBootstrapService teamBootstrap,
        PlayerBootstrapService playerBootstrap,
        TeamGuard teamGuard,
        ILogger<UpdateRouter> logger
    )
    {
        _db = db;
        _sender = sender;
        _bot = bot;
        _strings = strings;
        _teamBootstrap = teamBootstrap;
        _playerBootstrap = playerBootstrap;
        _teamGuard = teamGuard;
        _logger = logger;
    }

    public async Task RouteAsync(Update update, CancellationToken ct)
    {
        switch (update.Type)
        {
            case UpdateType.MyChatMember:
                await _teamBootstrap.HandleMyChatMemberAsync(update.MyChatMember!, ct);
                break;

            case UpdateType.ChatMember:
                // CLAUDE.md mentions using this to mark departures from Membership, which has
                // no such field yet and isn't part of M3 — subscribed via allowed_updates so
                // nothing has to change when that's built.
                _logger.LogDebug("Ignoring chat_member update for chat {ChatId}", update.ChatMember!.Chat.Id);
                break;

            case UpdateType.Message when update.Message?.Text is not null:
                await HandleMessageAsync(update.Message, ct);
                break;

            case UpdateType.CallbackQuery:
                await HandleCallbackQueryAsync(update.CallbackQuery!, ct);
                break;

            default:
                _logger.LogDebug("Ignoring update {UpdateId} of type {Type}", update.Id, update.Type);
                break;
        }
    }

    private async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        var chatId = new TelegramChatId(message.Chat.Id);
        var team = await _db.Teams.FirstOrDefaultAsync(t => t.ChatId == chatId, ct);

        Player? player = null;
        if (message.From is not null)
        {
            player = await _playerBootstrap.GetOrCreateAsync(message.From, ct);
            if (team is not null)
            {
                await _playerBootstrap.EnsureMembershipAsync(team.Id, player.Id, ct);
            }
        }

        var (command, argument) = CommandText.Parse(message.Text!);

        switch (command)
        {
            case "/start":
                var locale = LocaleResolver.Resolve(player?.Locale, message.From?.LanguageCode, team?.Locale ?? "en");
                await _sender.SendAsync(chatId, _strings.For(locale).Text("Start.Greeting"), null, ct);
                break;

            case "/settimezone" when team is not null && player is not null && message.From is not null:
                await HandleSetTimeZoneAsync(
                    team,
                    player.Id,
                    chatId,
                    new TelegramUserId(message.From.Id),
                    argument,
                    ct
                );
                break;

            case "/newgame" when team is not null && player is not null && message.From is not null:
                await HandleNewGameAsync(team, player.Id, chatId, new TelegramUserId(message.From.Id), ct);
                break;
        }
    }

    private async Task HandleSetTimeZoneAsync(
        Team team,
        PlayerId playerId,
        TelegramChatId chatId,
        TelegramUserId telegramUserId,
        string? argument,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

        if (!await _teamGuard.IsCaptainAsync(team.Id, playerId, chatId, telegramUserId, ct))
        {
            await _sender.SendAsync(chatId, strings.Text("NewGame.NotCaptain"), null, ct);
            return;
        }

        if (argument is null || !IsValidTimeZone(argument))
        {
            await _sender.SendAsync(
                chatId,
                strings.Text("Setup.TimeZoneInvalid", new { Input = argument ?? "" }),
                null,
                ct
            );
            return;
        }

        team.TimeZoneId = argument;
        await _db.SaveChangesAsync(ct);

        await _sender.SendAsync(chatId, strings.Text("Setup.TimeZoneSet", new { TimeZoneId = argument }), null, ct);
    }

    private async Task HandleNewGameAsync(
        Team team,
        PlayerId playerId,
        TelegramChatId chatId,
        TelegramUserId telegramUserId,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

        if (!await _teamGuard.IsCaptainAsync(team.Id, playerId, chatId, telegramUserId, ct))
        {
            await _sender.SendAsync(chatId, strings.Text("NewGame.NotCaptain"), null, ct);
            return;
        }

        var guard = TeamGuard.EnsureTimeZoneConfigured(team);
        var text = guard.Match(_ => strings.Text("NewGame.NotBuiltYet"), _ => strings.Text("NewGame.NeedsTimeZone"));

        await _sender.SendAsync(chatId, text, null, ct);
    }

    private async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        // No inline keyboards are sent yet in M3 — this just keeps the update type from
        // throwing once M4 starts sending CallbackData-encoded buttons.
        _logger.LogDebug("No handler for callback data {Data}", callbackQuery.Data);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private static bool IsValidTimeZone(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
    }
}
