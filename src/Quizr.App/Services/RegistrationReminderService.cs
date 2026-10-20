using System.Net;
using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Telegram;
using Quizr.App.Time;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Services;

// Asks the captains, once per game, to register the team at the venue — the moment the roster
// reaches RegistrationReminder.Threshold. Driven by the scheduler rather than from each place a
// signup is written (join, bring a friend, manage players, add a team guest), so no new
// roster-changing path can forget it, and a restart simply asks again.
public sealed class RegistrationReminderService
{
    private readonly QuizrDb _db;
    private readonly IMessageSender _sender;
    private readonly ITelegramBotClient _bot;
    private readonly IStrings _strings;
    private readonly PlayerBootstrapService _playerBootstrap;
    private readonly ILogger<RegistrationReminderService> _logger;

    public RegistrationReminderService(
        QuizrDb db,
        IMessageSender sender,
        ITelegramBotClient bot,
        IStrings strings,
        PlayerBootstrapService playerBootstrap,
        ILogger<RegistrationReminderService> logger
    )
    {
        _db = db;
        _sender = sender;
        _bot = bot;
        _strings = strings;
        _playerBootstrap = playerBootstrap;
        _logger = logger;
    }

    public async Task SendIfDueAsync(Team team, Game game, DateTimeOffset now, CancellationToken ct)
    {
        // The cheap checks first: this runs every tick for every live game, and the count is a
        // query that only matters once nothing already settles the answer.
        if (game.TeamRegisteredAt is not null || game.RegistrationReminderSentAt is not null)
        {
            return;
        }

        var liveSignups = await _db.Signups.CountAsync(s => s.GameId == game.Id && s.CancelledAt == null, ct);
        if (!RegistrationReminder.IsDue(game, liveSignups, now))
        {
            return;
        }

        // The claim is what makes this once-only, and it is a single conditional write rather
        // than a lock: of two ticks racing, one updates a row and the other updates none. It is
        // taken before sending so that a crash mid-send can lose the message but never repeat it.
        var claimed = await _db
            .Games.Where(g => g.Id == game.Id && g.RegistrationReminderSentAt == null && g.TeamRegisteredAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(g => g.RegistrationReminderSentAt, now), ct);
        if (claimed == 0)
        {
            return;
        }

        game.RegistrationReminderSentAt = now;

        var captains = await LoadReachableCaptainsAsync(team, ct);
        var delivered = 0;
        foreach (var captain in captains)
        {
            if (await TrySendAsync(captain, team, game, ct))
            {
                delivered++;
            }
        }

        if (delivered == 0)
        {
            // Nobody who can be messaged: every captain has either never started the bot or
            // has blocked it. Logged rather than retried — retrying would mean asking Telegram
            // for the chat's administrators on every tick for as long as that stays true.
            _logger.LogWarning(
                "No captain of team {TeamId} could be reached to register for game {GameId}",
                team.Id.Value,
                game.Id.Value
            );
            return;
        }

        _logger.LogInformation(
            "Asked {Delivered} captain(s) of team {TeamId} to register for game {GameId}",
            delivered,
            team.Id.Value,
            game.Id.Value
        );
    }

    // Captain is two things (CLAUDE.md): an explicit grant in the database, and anyone who is a
    // chat admin in Telegram — which only Telegram knows, so it has to be asked. Both feed one
    // query because a person can be both, and must only be messaged once.
    private async Task<List<Player>> LoadReachableCaptainsAsync(Team team, CancellationToken ct)
    {
        var adminUserIds = await LoadAdminUserIdsAsync(team, ct);

        return await _db
            .Players.Where(p => p.DmEnabled)
            .Where(p =>
                adminUserIds.Contains(p.TelegramUserId)
                || _db.Memberships.Any(m => m.PlayerId == p.Id && m.TeamId == team.Id && m.IsCaptain)
            )
            .ToListAsync(ct);
    }

    private async Task<List<TelegramUserId>> LoadAdminUserIdsAsync(Team team, CancellationToken ct)
    {
        try
        {
            var admins = await _bot.SendRequest(new GetChatAdministratorsRequest { ChatId = team.ChatId.Value }, ct);
            return admins.Where(a => !a.User.IsBot).Select(a => new TelegramUserId(a.User.Id)).ToList();
        }
        catch (ApiRequestException ex)
        {
            // Explicit captains can still be reached without it, which beats sending nobody.
            _logger.LogWarning(
                ex,
                "Could not list the administrators of team {TeamId}; asking explicit captains only",
                team.Id.Value
            );
            return [];
        }
    }

    // DmEnabled is a best guess that can be stale (see UpdateRouter.TrySendDirectlyAsync), so a
    // refusal is an ordinary outcome: it repairs the flag and moves on to the next captain.
    private async Task<bool> TrySendAsync(Player captain, Team team, Game game, CancellationToken ct)
    {
        // A DM is in the person's own language, not the team's.
        var strings = _strings.For(captain.Locale ?? team.Locale);
        var text = strings.Text(
            "RegistrationReminder.Dm",
            new
            {
                Title = WebUtility.HtmlEncode(game.Title),
                When = TeamTime.ConvertToLocal(game.StartsAt, team.TimeZoneId!),
                Venue = WebUtility.HtmlEncode(game.Venue),
            }
        );
        var keyboard = new InlineKeyboardMarkup(
            InlineKeyboardButton.WithCallbackData(
                strings.Text("RegistrationReminder.DoneButton"),
                CallbackData.Format(CallbackData.TeamRegistered, game.Id)
            )
        );

        try
        {
            await _sender.SendAsync(new TelegramChatId(captain.TelegramUserId.Value), text, keyboard, ct);
            return true;
        }
        catch (ApiRequestException ex)
        {
            _logger.LogInformation(ex, "Could not DM the registration reminder to player {PlayerId}", captain.Id.Value);
            await _playerBootstrap.SetDmEnabledAsync(captain, false, ct);
            return false;
        }
    }
}
