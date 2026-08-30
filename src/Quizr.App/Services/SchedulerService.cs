using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Telegram;
using Quizr.App.Time;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;

namespace Quizr.App.Services;

// One scheduler tick's worth of work for every active team: send whatever reminders are due,
// auto-finish games whose 4-hour window elapsed, and keep the Board correct. Deliberately a
// query — "what's due now" — not a job queue (STACK.md): idempotent, and a restart just asks
// again, which is what gives M6's "catch up on start" for free.
public sealed class SchedulerService
{
    // Invariant 8.
    private static readonly TimeSpan AutoFinishAfter = TimeSpan.FromHours(4);

    private readonly QuizrDb _db;
    private readonly IMessageSender _sender;
    private readonly IStrings _strings;
    private readonly IGameService _games;
    private readonly AnnouncementService _announcements;
    private readonly BoardService _board;
    private readonly TimeProvider _clock;
    private readonly ILogger<SchedulerService> _logger;

    public SchedulerService(
        QuizrDb db,
        IMessageSender sender,
        IStrings strings,
        IGameService games,
        AnnouncementService announcements,
        BoardService board,
        TimeProvider clock,
        ILogger<SchedulerService> logger
    )
    {
        _db = db;
        _sender = sender;
        _strings = strings;
        _games = games;
        _announcements = announcements;
        _board = board;
        _clock = clock;
        _logger = logger;
    }

    public async Task RunTickAsync(CancellationToken ct)
    {
        // A timezone is required for every reminder/finish calculation below, and a team
        // can't have games without one (CLAUDE.md's Team bootstrap) — nothing to do here yet.
        var teams = await _db.Teams.Where(t => t.DeactivatedAt == null && t.TimeZoneId != null).ToListAsync(ct);

        foreach (var team in teams)
        {
            try
            {
                await ProcessTeamAsync(team, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduler tick failed for team {TeamId}", team.Id);
            }
        }
    }

    private async Task ProcessTeamAsync(Team team, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var games = await _db
            .Games.Where(g => g.TeamId == team.Id && g.FinishedAt == null && g.DeclinedAt == null)
            .ToListAsync(ct);

        foreach (var game in games)
        {
            try
            {
                if (now >= game.StartsAt + AutoFinishAfter)
                {
                    await FinishGameAsync(team, game, ct);
                }
                else
                {
                    await SendDueRemindersAsync(team, game, now, ct);
                }
            }
            catch (Exception ex)
            {
                // "One broken game must not stop reminders for everyone else" — STYLE.md.
                _logger.LogError(ex, "Scheduler failed to process game {GameId}", game.Id);
            }
        }

        // Verifies the pin and reposts from the database if the message is gone (invariant
        // 12) as a side effect — called every tick regardless of whether a game changed, since
        // that's what makes an unpin-by-hand heal without anyone acting.
        await _board.RefreshAsync(team, ct);
    }

    // actorPlayerId is always null here — the scheduler is the system, not a captain
    // (invariant 13). The manual Finish button shares this same materialization through
    // GameService.FinishAsync; only who called it differs.
    private async Task FinishGameAsync(Team team, Game game, CancellationToken ct)
    {
        await _games.FinishAsync(game, actorPlayerId: null, ct);
        await _announcements.RefreshAsync(game, team, ct);
    }

    private async Task SendDueRemindersAsync(Team team, Game game, DateTimeOffset now, CancellationToken ct)
    {
        var gameLocalDate = DateOnly.FromDateTime(TeamTime.ConvertToLocal(game.StartsAt, team.TimeZoneId!).Date);

        await ProcessReminderKindAsync(
            team,
            game,
            NotificationKind.ReminderEveningBefore,
            TeamTime.ConvertToUtc(gameLocalDate.AddDays(-1), team.EveningBeforeAt, team.TimeZoneId!),
            now,
            m => m.EveningBefore,
            ct
        );
        await ProcessReminderKindAsync(
            team,
            game,
            NotificationKind.ReminderMorningOf,
            TeamTime.ConvertToUtc(gameLocalDate, team.MorningOfAt, team.TimeZoneId!),
            now,
            m => m.MorningOf,
            ct
        );
        await ProcessReminderKindAsync(
            team,
            game,
            NotificationKind.ReminderBeforeStart,
            game.StartsAt - team.BeforeStartLead,
            now,
            m => m.BeforeStart,
            ct
        );
    }

    private async Task ProcessReminderKindAsync(
        Team team,
        Game game,
        NotificationKind kind,
        DateTimeOffset dueAt,
        DateTimeOffset now,
        Func<Membership, ReminderChannel> channelOf,
        CancellationToken ct
    )
    {
        if (now < dueAt)
        {
            return;
        }

        // Filtered ThenInclude: only the membership for this team comes back, so a signup's
        // Player.Memberships collection here is at most one row, not every team they're in.
        var liveSignups = await _db
            .Signups.AsNoTracking()
            .Include(s => s.Player!)
                .ThenInclude(p => p.Memberships.Where(m => m.TeamId == team.Id))
            .Where(s => s.GameId == game.Id && s.CancelledAt == null)
            .ToListAsync(ct);
        var memberSignups = liveSignups.Where(s => s.IsMember).ToList();
        if (memberSignups.Count == 0)
        {
            return;
        }

        var playingIds = Roster.Split(liveSignups, game.Capacity).Playing.Select(s => s.Id).ToHashSet();

        var groupRecipients = new List<Player>();

        foreach (var signup in memberSignups)
        {
            var membership = signup.Player!.Memberships.SingleOrDefault();
            if (membership is null)
            {
                continue; // no membership yet — nothing to read a preference from
            }

            // Reserve without opting in stays unnotified for now, not forever: this is a
            // query, re-run every tick, so a later promotion naturally picks them up on
            // whichever tick is still at-or-past this slot's due time.
            if (!playingIds.Contains(signup.Id) && !membership.RemindWhenReserve)
            {
                continue;
            }

            var channel = channelOf(membership);
            if (channel == ReminderChannel.Off)
            {
                continue;
            }

            var player = signup.Player;

            if (channel == ReminderChannel.Dm)
            {
                // A bot cannot message anyone who hasn't started it (CLAUDE.md) — same
                // retry-next-tick reasoning as the reserve case above.
                if (!player.DmEnabled)
                {
                    continue;
                }

                if (await NotificationRecorder.TryRecordAsync(_db, signup.Id, kind, _clock, ct))
                {
                    await SendDmReminderAsync(player, team, game, kind, ct);
                }
            }
            else if (await NotificationRecorder.TryRecordAsync(_db, signup.Id, kind, _clock, ct))
            {
                groupRecipients.Add(player);
            }
        }

        if (groupRecipients.Count > 0)
        {
            await SendGroupReminderAsync(team, game, kind, groupRecipients, ct);
        }
    }

    private async Task SendGroupReminderAsync(
        Team team,
        Game game,
        NotificationKind kind,
        IReadOnlyList<Player> recipients,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);
        var local = TeamTime.ConvertToLocal(game.StartsAt, team.TimeZoneId!);
        var mentions = string.Join(", ", recipients.Select(Mention));

        var text = strings.Text(
            ReminderMessageKey(kind, isGroup: true),
            new
            {
                Mentions = mentions,
                Title = WebUtility.HtmlEncode(game.Title),
                When = local,
            }
        );
        await _sender.SendAsync(team.ChatId, text, null, ct);
    }

    private async Task SendDmReminderAsync(
        Player player,
        Team team,
        Game game,
        NotificationKind kind,
        CancellationToken ct
    )
    {
        // DMs use the person's own language; null falls back to the team's (Player.Locale).
        var strings = _strings.For(player.Locale ?? team.Locale);
        var local = TeamTime.ConvertToLocal(game.StartsAt, team.TimeZoneId!);

        var text = strings.Text(
            ReminderMessageKey(kind, isGroup: false),
            new { Title = WebUtility.HtmlEncode(game.Title), When = local }
        );
        await _sender.SendAsync(new TelegramChatId(player.TelegramUserId.Value), text, null, ct);
    }

    private static string Mention(Player player) =>
        $"""<a href="tg://user?id={player.TelegramUserId.Value}">{WebUtility.HtmlEncode(player.DisplayName)}</a>""";

    private static string ReminderMessageKey(NotificationKind kind, bool isGroup) =>
        (kind, isGroup) switch
        {
            (NotificationKind.ReminderEveningBefore, true) => "Reminder.EveningBefore.Group",
            (NotificationKind.ReminderEveningBefore, false) => "Reminder.EveningBefore.Dm",
            (NotificationKind.ReminderMorningOf, true) => "Reminder.MorningOf.Group",
            (NotificationKind.ReminderMorningOf, false) => "Reminder.MorningOf.Dm",
            (NotificationKind.ReminderBeforeStart, true) => "Reminder.BeforeStart.Group",
            (NotificationKind.ReminderBeforeStart, false) => "Reminder.BeforeStart.Dm",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a reminder kind."),
        };
}
