using System.Net;
using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Rendering;
using Quizr.App.Telegram;
using Quizr.App.Time;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Services;

// Asks the captains who opted in, once per game, to register the team at the venue — the moment
// the roster reaches the game's threshold (RegistrationReminder). Driven by the scheduler
// rather than from each place a signup is written (join, bring a friend, manage players, add a
// team guest), so no new roster-changing path can forget it, and a restart simply asks again.
public sealed class RegistrationReminderService
{
    private readonly QuizrDb _db;
    private readonly IMessageSender _sender;
    private readonly TeamGuard _guard;
    private readonly IStrings _strings;
    private readonly PlayerBootstrapService _playerBootstrap;
    private readonly ILogger<RegistrationReminderService> _logger;

    public RegistrationReminderService(
        QuizrDb db,
        IMessageSender sender,
        TeamGuard guard,
        IStrings strings,
        PlayerBootstrapService playerBootstrap,
        ILogger<RegistrationReminderService> logger
    )
    {
        _db = db;
        _sender = sender;
        _guard = guard;
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

        // Before the claim, not after it: the claim is once-only, so spending it while nobody
        // can be asked would mean a captain who opts in tomorrow is never told about a game
        // that has been full since today.
        var recipients = await LoadRecipientsAsync(team, ct);
        if (recipients.Count == 0)
        {
            return;
        }

        // The claim is what makes this once-only, and it is a single conditional write rather
        // than a lock: of two ticks racing, one updates a row and the other updates none. It is
        // taken before sending so that a crash mid-send can lose the message but never repeat it.
        if (!await TryClaimAsync(game, now, ct))
        {
            return;
        }

        var delivered = 0;

        var group = recipients.Where(r => r.Membership.RegistrationReminder == ReminderChannel.Group).ToList();
        if (group.Count > 0)
        {
            await SendToGroupAsync(team, game, group.Select(r => r.Player).ToList(), ct);
            delivered += group.Count;
        }

        foreach (var recipient in recipients.Where(r => r.Membership.RegistrationReminder == ReminderChannel.Dm))
        {
            if (await TrySendDmAsync(recipient.Player, team, game, ct))
            {
                delivered++;
            }
        }

        _logger.LogInformation(
            "Asked {Delivered} captain(s) of team {TeamId} to register for game {GameId}",
            delivered,
            team.Id.Value,
            game.Id.Value
        );
    }

    private async Task<bool> TryClaimAsync(Game game, DateTimeOffset now, CancellationToken ct)
    {
        var claimed = await _db
            .Games.Where(g => g.Id == game.Id && g.RegistrationReminderSentAt == null && g.TeamRegisteredAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(g => g.RegistrationReminderSentAt, now), ct);
        if (claimed == 0)
        {
            return false;
        }

        game.RegistrationReminderSentAt = now;
        return true;
    }

    // Opted in, still a captain, and reachable by the channel they chose. Captaincy is
    // re-checked rather than trusted from when they opted in: a demotion in Telegram takes
    // effect immediately everywhere else (TeamGuard), and a reminder is no place to be stale.
    // A DM channel is only usable by someone who has started the bot — checked first, since it
    // is a column read and the captain check can be a Telegram call.
    private async Task<List<Recipient>> LoadRecipientsAsync(Team team, CancellationToken ct)
    {
        // Tracked, unlike most reads: a refused DM clears the player's DmEnabled, and that write
        // goes nowhere on an entity the change tracker never saw.
        var optedIn = await _db
            .Memberships.Include(m => m.Player)
            .Where(m => m.TeamId == team.Id && m.RegistrationReminder != ReminderChannel.Off)
            .ToListAsync(ct);

        var recipients = new List<Recipient>();
        foreach (var membership in optedIn)
        {
            if (membership.RegistrationReminder == ReminderChannel.Dm && !membership.Player.DmEnabled)
            {
                continue;
            }

            var actor = new Actor(membership.PlayerId, membership.Player.TelegramUserId);
            if (await _guard.IsCaptainAsync(team, actor, ct))
            {
                recipients.Add(new Recipient(membership, membership.Player));
            }
        }

        return recipients;
    }

    // One message tagging everyone who chose the group, never one each (CLAUDE.md), anchored to
    // the game's announcement so a tap on the quote jumps to it. If the send fails the claim is
    // given back, so the next tick tries again instead of the game being silently marked as
    // asked — a chat that was just migrated or briefly unreachable is the usual reason.
    private async Task SendToGroupAsync(Team team, Game game, IReadOnlyList<Player> players, CancellationToken ct)
    {
        var strings = _strings.For(team.Locale);
        var text = strings.Text(
            "RegistrationReminder.Group",
            new
            {
                Mentions = string.Join(", ", players.Select(Mention.Of)),
                Title = WebUtility.HtmlEncode(game.Title),
                When = TeamTime.ConvertToLocal(game.StartsAt, team.TimeZoneId!),
                Venue = WebUtility.HtmlEncode(game.Venue),
            }
        );

        try
        {
            await _sender.SendReplyAsync(
                team.ChatId,
                game.AnnouncementMessageId,
                text,
                DoneKeyboard(game, strings),
                ct
            );
        }
        catch (ApiRequestException)
        {
            await _db
                .Games.Where(g => g.Id == game.Id)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(g => g.RegistrationReminderSentAt, (DateTimeOffset?)null),
                    ct
                );
            game.RegistrationReminderSentAt = null;
            throw;
        }
    }

    // DmEnabled is a best guess that can be stale (see UpdateRouter.TrySendDirectlyAsync), so a
    // refusal is an ordinary outcome: it repairs the flag and moves on to the next captain.
    private async Task<bool> TrySendDmAsync(Player captain, Team team, Game game, CancellationToken ct)
    {
        // A DM is in the person's own language, not the team's, and names the team because one
        // person can be a captain of several.
        var strings = _strings.For(captain.Locale ?? team.Locale);
        var text = strings.Text(
            "RegistrationReminder.Dm",
            new
            {
                Team = WebUtility.HtmlEncode(team.Name),
                Title = WebUtility.HtmlEncode(game.Title),
                When = TeamTime.ConvertToLocal(game.StartsAt, team.TimeZoneId!),
                Venue = WebUtility.HtmlEncode(game.Venue),
            }
        );

        try
        {
            await _sender.SendAsync(
                new TelegramChatId(captain.TelegramUserId.Value),
                text,
                DoneKeyboard(game, strings),
                ct
            );
            return true;
        }
        catch (ApiRequestException ex)
        {
            _logger.LogInformation(ex, "Could not DM the registration reminder to player {PlayerId}", captain.Id.Value);
            await _playerBootstrap.SetDmEnabledAsync(captain, false, ct);
            return false;
        }
    }

    private static InlineKeyboardMarkup DoneKeyboard(Game game, IStringsFor strings) =>
        new(
            InlineKeyboardButton.WithCallbackData(
                strings.Text("RegistrationReminder.DoneButton"),
                CallbackData.Format(CallbackData.TeamRegistered, game.Id)
            )
        );

    private sealed record Recipient(Membership Membership, Player Player);
}
