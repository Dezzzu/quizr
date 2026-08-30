using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.App.Telegram;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Game = Quizr.Domain.Entities.Game;

namespace Quizr.App.Tests;

// SchedulerService.RunTickAsync processes every team in the database, not just the one a
// test seeded — TUnit runs tests within a class in parallel by default, unlike xUnit, so two
// tests' tick calls could otherwise race over each other's teams and steal each other's
// notifications. NotInParallel restores the sequential-within-class execution these tests
// were written against.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
[NotInParallel]
public class SchedulerServiceTests
{
    private static readonly TimeOnly EveningBeforeAt = new(20, 0);
    private static readonly TimeOnly MorningOfAt = new(9, 0);
    private static readonly TimeSpan BeforeStartLead = TimeSpan.FromHours(2);

    private readonly PostgresFixture _fixture;

    public SchedulerServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task AGameLeftAloneFinishesItselfFourHoursAfterItStarted()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var startsAt = new DateTimeOffset(2026, 3, 6, 19, 0, 0, TimeSpan.Zero);
        var team = await SeedTeamAsync(db, chatId: 9001, ct);
        var game = await SeedGameAsync(db, team, startsAt, capacity: 1, ct);
        var playing = await SeedPlayerAsync(db, 9001, ct);
        var reserve = await SeedPlayerAsync(db, 9002, ct);
        await SeedSignupAsync(db, game, playing, startsAt.AddMinutes(-100), ct);
        await SeedSignupAsync(db, game, reserve, startsAt.AddMinutes(-90), ct);
        var (scheduler, _) = CreateScheduler(db, startsAt.AddHours(4).AddMinutes(1));

        await scheduler.RunTickAsync(ct);

        var refreshed = await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct);
        refreshed.FinishedAt.Should().NotBeNull();

        var participations = await db.Participations.AsNoTracking().Where(p => p.GameId == game.Id).ToListAsync(ct);
        participations.Should().HaveCount(2);
        participations.Single(p => p.PlayerId == playing.Id).Played.Should().BeTrue();
        participations.Single(p => p.PlayerId == reserve.Id).Played.Should().BeFalse();
        participations.Should().OnlyContain(p => p.Attended);
    }

    [Test]
    public async Task AGameNotYetFourHoursPastItsStartIsLeftAlone()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var startsAt = new DateTimeOffset(2026, 3, 6, 19, 0, 0, TimeSpan.Zero);
        var team = await SeedTeamAsync(db, chatId: 9003, ct);
        var game = await SeedGameAsync(db, team, startsAt, capacity: 5, ct);
        var (scheduler, _) = CreateScheduler(db, startsAt.AddHours(3));

        await scheduler.RunTickAsync(ct);

        (await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct)).FinishedAt.Should().BeNull();
    }

    [Test]
    public async Task FinishingRemovesTheJoinButtonsFromTheAnnouncement()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var startsAt = new DateTimeOffset(2026, 3, 6, 19, 0, 0, TimeSpan.Zero);
        var team = await SeedTeamAsync(db, chatId: 9004, ct);
        var game = await SeedGameAsync(db, team, startsAt, capacity: 5, ct);
        game.AnnouncementMessageId = new TelegramMessageId(1);
        await db.SaveChangesAsync(ct);
        var (scheduler, bot) = CreateScheduler(db, startsAt.AddHours(5));

        await scheduler.RunTickAsync(ct);
        await WaitUntilAsync(() => bot.EditedTexts(9004).Count > 0, ct);

        bot.EditedTexts(9004)
            .Should()
            .ContainSingle(text => text != null && text.Contains("Finished", StringComparison.Ordinal));
    }

    [Test]
    public async Task AGroupReminderBatchesEveryOptedInPlayerIntoOneMessage()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var startsAt = new DateTimeOffset(2026, 3, 6, 19, 0, 0, TimeSpan.Zero);
        var team = await SeedTeamAsync(db, chatId: 9005, ct);
        var game = await SeedGameAsync(db, team, startsAt, capacity: 5, ct);
        var alice = await SeedPlayerAsync(db, 9005, ct, displayName: "Alice");
        var bob = await SeedPlayerAsync(db, 9006, ct, displayName: "Bob");
        await SeedSignupAsync(db, game, alice, startsAt.AddDays(-3), ct);
        await SeedSignupAsync(db, game, bob, startsAt.AddDays(-3).AddMinutes(1), ct);
        await SeedMembershipAsync(db, team, alice, ReminderChannel.Group, ct);
        await SeedMembershipAsync(db, team, bob, ReminderChannel.Group, ct);
        // Evening-before due instant: 2026-03-05 20:00 Berlin = 19:00 UTC.
        var (scheduler, bot) = CreateScheduler(db, new DateTimeOffset(2026, 3, 5, 19, 0, 0, TimeSpan.Zero));

        await scheduler.RunTickAsync(ct);

        var reminders = bot.SentTexts(9005).Where(t => t.Contains("Reminder", StringComparison.Ordinal)).ToList();
        reminders.Should().ContainSingle();
        reminders.Single().Should().Contain("Alice").And.Contain("Bob");
    }

    [Test]
    public async Task ARunningTheSameTickTwiceDoesNotSendTheReminderTwice()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var startsAt = new DateTimeOffset(2026, 3, 6, 19, 0, 0, TimeSpan.Zero);
        var team = await SeedTeamAsync(db, chatId: 9007, ct);
        var game = await SeedGameAsync(db, team, startsAt, capacity: 5, ct);
        var alice = await SeedPlayerAsync(db, 9007, ct);
        await SeedSignupAsync(db, game, alice, startsAt.AddDays(-3), ct);
        await SeedMembershipAsync(db, team, alice, ReminderChannel.Group, ct);
        var (scheduler, bot) = CreateScheduler(db, new DateTimeOffset(2026, 3, 5, 19, 0, 0, TimeSpan.Zero));

        await scheduler.RunTickAsync(ct);
        await scheduler.RunTickAsync(ct);

        bot.SentTexts(9007).Count(t => t.Contains("Reminder", StringComparison.Ordinal)).Should().Be(1);
    }

    [Test]
    public async Task ADmReminderIsSentOnlyWhenTheRecipientHasStartedTheBot()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var startsAt = new DateTimeOffset(2026, 3, 6, 19, 0, 0, TimeSpan.Zero);
        var team = await SeedTeamAsync(db, chatId: 9008, ct);
        var game = await SeedGameAsync(db, team, startsAt, capacity: 5, ct);
        var noDm = await SeedPlayerAsync(db, 90080, ct, dmEnabled: false);
        var withDm = await SeedPlayerAsync(db, 90090, ct, dmEnabled: true);
        await SeedSignupAsync(db, game, noDm, startsAt.AddDays(-3), ct);
        await SeedSignupAsync(db, game, withDm, startsAt.AddDays(-3).AddMinutes(1), ct);
        await SeedMembershipAsync(db, team, noDm, ReminderChannel.Dm, ct);
        await SeedMembershipAsync(db, team, withDm, ReminderChannel.Dm, ct);
        var (scheduler, bot) = CreateScheduler(db, new DateTimeOffset(2026, 3, 5, 19, 0, 0, TimeSpan.Zero));

        await scheduler.RunTickAsync(ct);

        var reminders = bot.SentTexts(withDm.TelegramUserId.Value)
            .Where(t => t.Contains("Reminder", StringComparison.Ordinal))
            .ToList();
        reminders.Should().ContainSingle();
        bot.SentTexts(noDm.TelegramUserId.Value).Should().BeEmpty();
        var notified = await db.Notifications.AsNoTracking().ToListAsync(ct);
        var withDmSignup = await db.Signups.AsNoTracking().SingleAsync(s => s.PlayerId == withDm.Id, ct);
        notified.Should().ContainSingle(n => n.SignupId == withDmSignup.Id);
    }

    [Test]
    public async Task AReserveIsSkippedUnlessTheyOptedInToReserveReminders()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var startsAt = new DateTimeOffset(2026, 3, 6, 19, 0, 0, TimeSpan.Zero);
        var team = await SeedTeamAsync(db, chatId: 9010, ct);
        var game = await SeedGameAsync(db, team, startsAt, capacity: 1, ct);
        var playing = await SeedPlayerAsync(db, 9010, ct, displayName: "Playing Alice");
        var reserve = await SeedPlayerAsync(db, 9011, ct, displayName: "Reserve Bob");
        await SeedSignupAsync(db, game, playing, startsAt.AddDays(-3), ct);
        await SeedSignupAsync(db, game, reserve, startsAt.AddDays(-3).AddMinutes(1), ct);
        await SeedMembershipAsync(db, team, playing, ReminderChannel.Group, ct);
        await SeedMembershipAsync(db, team, reserve, ReminderChannel.Group, ct, remindWhenReserve: false);
        var (scheduler, bot) = CreateScheduler(db, new DateTimeOffset(2026, 3, 5, 19, 0, 0, TimeSpan.Zero));

        await scheduler.RunTickAsync(ct);

        var reminders = bot.SentTexts(9010).Where(t => t.Contains("Reminder", StringComparison.Ordinal)).ToList();
        reminders.Should().ContainSingle();
        reminders.Single().Should().Contain("Playing Alice");
        reminders.Single().Should().NotContain("Reserve Bob");
    }

    [Test]
    public async Task AReserveWhoOptedInIsIncluded()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var startsAt = new DateTimeOffset(2026, 3, 6, 19, 0, 0, TimeSpan.Zero);
        var team = await SeedTeamAsync(db, chatId: 9012, ct);
        var game = await SeedGameAsync(db, team, startsAt, capacity: 1, ct);
        var playing = await SeedPlayerAsync(db, 9013, ct);
        var reserve = await SeedPlayerAsync(db, 9014, ct);
        await SeedSignupAsync(db, game, playing, startsAt.AddDays(-3), ct);
        await SeedSignupAsync(db, game, reserve, startsAt.AddDays(-3).AddMinutes(1), ct);
        await SeedMembershipAsync(db, team, playing, ReminderChannel.Off, ct);
        await SeedMembershipAsync(db, team, reserve, ReminderChannel.Group, ct, remindWhenReserve: true);
        var (scheduler, _) = CreateScheduler(db, new DateTimeOffset(2026, 3, 5, 19, 0, 0, TimeSpan.Zero));

        await scheduler.RunTickAsync(ct);

        var notified = await db.Notifications.AsNoTracking().ToListAsync(ct);
        var reserveSignup = await db.Signups.AsNoTracking().SingleAsync(s => s.PlayerId == reserve.Id, ct);
        notified.Should().ContainSingle(n => n.SignupId == reserveSignup.Id);
    }

    [Test]
    public async Task TheBeforeStartReminderFiresRelativeToTheGamesStartTime()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var startsAt = new DateTimeOffset(2026, 3, 6, 19, 0, 0, TimeSpan.Zero);
        var team = await SeedTeamAsync(db, chatId: 9015, ct);
        var game = await SeedGameAsync(db, team, startsAt, capacity: 5, ct);
        var alice = await SeedPlayerAsync(db, 9015, ct);
        await SeedSignupAsync(db, game, alice, startsAt.AddDays(-3), ct);
        await SeedMembershipAsync(db, team, alice, ReminderChannel.Off, ct, beforeStart: ReminderChannel.Group);
        // Before too early: BeforeStartLead is 2h, so 3h before start must not fire yet.
        var (tooEarly, botTooEarly) = CreateScheduler(db, startsAt.AddHours(-3));
        await tooEarly.RunTickAsync(ct);
        botTooEarly.SentTexts(9015).Where(t => t.Contains("Reminder", StringComparison.Ordinal)).Should().BeEmpty();

        var (dueNow, botDueNow) = CreateScheduler(db, startsAt.AddHours(-1));
        await dueNow.RunTickAsync(ct);

        botDueNow.SentTexts(9015).Where(t => t.Contains("Reminder", StringComparison.Ordinal)).Should().ContainSingle();
    }

    [Test]
    public async Task ARestartMidWindowStillSendsAReminderThatCameDueWhileItWasDown()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var startsAt = new DateTimeOffset(2026, 3, 6, 19, 0, 0, TimeSpan.Zero);
        var team = await SeedTeamAsync(db, chatId: 9016, ct);
        var game = await SeedGameAsync(db, team, startsAt, capacity: 5, ct);
        var alice = await SeedPlayerAsync(db, 9016, ct);
        await SeedSignupAsync(db, game, alice, startsAt.AddDays(-3), ct);
        await SeedMembershipAsync(
            db,
            team,
            alice,
            ReminderChannel.Group,
            ct,
            morningOf: ReminderChannel.Off,
            beforeStart: ReminderChannel.Off
        );
        // "Process restarted" after the evening-before slot came due (2026-03-05 19:00 UTC)
        // but before the morning-of slot (2026-03-06 08:00 UTC) — only the missed one fires.
        var (scheduler, bot) = CreateScheduler(db, new DateTimeOffset(2026, 3, 6, 0, 0, 0, TimeSpan.Zero));

        await scheduler.RunTickAsync(ct);

        bot.SentTexts(9016).Where(t => t.Contains("Reminder", StringComparison.Ordinal)).Should().ContainSingle();
    }

    [Test]
    public async Task ADeclinedGameNeverFiresAReminder()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var startsAt = new DateTimeOffset(2026, 3, 6, 19, 0, 0, TimeSpan.Zero);
        var team = await SeedTeamAsync(db, chatId: 9017, ct);
        var game = await SeedGameAsync(db, team, startsAt, capacity: 5, ct);
        game.DeclinedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var alice = await SeedPlayerAsync(db, 9017, ct);
        await SeedSignupAsync(db, game, alice, startsAt.AddDays(-3), ct);
        await SeedMembershipAsync(db, team, alice, ReminderChannel.Group, ct);
        var (scheduler, bot) = CreateScheduler(db, new DateTimeOffset(2026, 3, 5, 19, 0, 0, TimeSpan.Zero));

        await scheduler.RunTickAsync(ct);

        bot.SentTexts(9017).Where(t => t.Contains("Reminder", StringComparison.Ordinal)).Should().BeEmpty();
    }

    [Test]
    public async Task EveryTickRefreshesTheBoardEvenWithNoGames()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        _ = await SeedTeamAsync(db, chatId: 9018, ct);
        var (scheduler, bot) = CreateScheduler(db, DateTimeOffset.UtcNow);

        await scheduler.RunTickAsync(ct);

        bot.SentTexts(9018).Should().ContainSingle(t => t.Contains("No upcoming games yet", StringComparison.Ordinal));
    }

    [Test]
    public async Task ADialogUntouchedForTwentyMinutesIsExpired()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 9021, ct);
        var player = await SeedPlayerAsync(db, telegramUserId: 9021, ct);
        var now = DateTimeOffset.UtcNow;
        db.DialogStates.Add(
            new DialogState
            {
                TeamId = team.Id,
                PlayerId = player.Id,
                ChatId = team.ChatId,
                Kind = DialogKinds.NewFranchise,
                Step = "",
                Data = "{}",
                CreatedAt = now.AddMinutes(-25),
                UpdatedAt = now.AddMinutes(-21),
            }
        );
        await db.SaveChangesAsync(ct);
        var (scheduler, _) = CreateScheduler(db, now);

        await scheduler.RunTickAsync(ct);

        (await db.DialogStates.CountAsync(d => d.TeamId == team.Id, ct)).Should().Be(0);
    }

    [Test]
    public async Task ADialogUpdatedRecentlySurvivesTheTick()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 9022, ct);
        var player = await SeedPlayerAsync(db, telegramUserId: 9022, ct);
        var now = DateTimeOffset.UtcNow;
        db.DialogStates.Add(
            new DialogState
            {
                TeamId = team.Id,
                PlayerId = player.Id,
                ChatId = team.ChatId,
                Kind = DialogKinds.NewFranchise,
                Step = "",
                Data = "{}",
                CreatedAt = now.AddMinutes(-5),
                UpdatedAt = now.AddMinutes(-5),
            }
        );
        await db.SaveChangesAsync(ct);
        var (scheduler, _) = CreateScheduler(db, now);

        await scheduler.RunTickAsync(ct);

        (await db.DialogStates.CountAsync(d => d.TeamId == team.Id, ct)).Should().Be(1);
    }

    // Telegram permanently invalidates a group's chat id the moment it's upgraded to a
    // supergroup — every send against the old id then fails with exactly this, forever, with
    // no way to recover on its own unless the stored id gets corrected.
    [Test]
    public async Task ATeamsMigratedChatIdIsUpdatedInsteadOfFailingForever()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 9019, ct);
        var (scheduler, bot) = CreateScheduler(db, DateTimeOffset.UtcNow);
        bot.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(
                new ApiRequestException(
                    "Bad Request: group chat was upgraded to a supergroup chat",
                    400,
                    new ResponseParameters { MigrateToChatId = -1009999999999 }
                )
            );

        await scheduler.RunTickAsync(ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct))
            .ChatId.Should()
            .Be(new TelegramChatId(-1009999999999));
    }

    // The real failure mode observed in practice: Telegram's own my_chat_member "added"
    // event for the new chat id arrives and bootstraps a fresh team before this tick catches
    // up to the migration — Team.ChatId's unique index means a blind overwrite would just
    // fail a different way forever instead. The stale team is retired, not deleted
    // (invariant 7), so the fresh team keeps the new chat id it already legitimately owns.
    [Test]
    public async Task ATeamIsRetiredRatherThanCollidingWhenAFreshTeamAlreadyOwnsTheMigratedChatId()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var staleTeam = await SeedTeamAsync(db, chatId: 9020, ct);
        var freshTeam = await SeedTeamAsync(db, chatId: -1009999999998, ct);
        var (scheduler, bot) = CreateScheduler(db, DateTimeOffset.UtcNow);
        // Only the stale team's own send fails this way — Telegram never returns this error
        // for a chat id that's already current, which is exactly what makes freshTeam fresh.
        bot.SendRequest(Arg.Is<SendMessageRequest>(r => r.ChatId.Identifier == 9020), Arg.Any<CancellationToken>())
            .ThrowsAsync(
                new ApiRequestException(
                    "Bad Request: group chat was upgraded to a supergroup chat",
                    400,
                    new ResponseParameters { MigrateToChatId = -1009999999998 }
                )
            );

        await scheduler.RunTickAsync(ct);

        var refreshedStale = await db.Teams.AsNoTracking().SingleAsync(t => t.Id == staleTeam.Id, ct);
        refreshedStale.ChatId.Should().Be(new TelegramChatId(9020));
        refreshedStale.DeactivatedAt.Should().NotBeNull();
        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == freshTeam.Id, ct))
            .ChatId.Should()
            .Be(new TelegramChatId(-1009999999998));
    }

    // Mirrors MessageEditDebouncerTests' pattern: the debouncer flushes on a real timer, so an
    // assertion on an edit has to poll for it rather than check immediately after the tick.
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
        }
    }

    private static (SchedulerService Scheduler, ITelegramBotClient Bot) CreateScheduler(QuizrDb db, DateTimeOffset now)
    {
        var bot = TelegramBotClientTestHelper.Create();
        var clock = new FakeTimeProvider(now);
        // The debouncer runs on the real clock, not the fake business-time one, so an edit
        // (the Board, a finished announcement) actually flushes during the test instead of
        // waiting out a debounce window that never advances.
        var sender = new MessageSender(
            bot,
            new MessageEditDebouncer(bot, TimeProvider.System, NullLogger<MessageEditDebouncer>.Instance)
        );
        var strings = new Strings();
        var games = new GameService(db, clock);
        var announcements = new AnnouncementService(db, sender, strings);
        var board = new BoardService(db, sender, bot, strings, NullLogger<BoardService>.Instance);

        var scheduler = new SchedulerService(
            db,
            sender,
            strings,
            games,
            announcements,
            board,
            clock,
            NullLogger<SchedulerService>.Instance
        );

        return (scheduler, bot);
    }

    private static async Task<Team> SeedTeamAsync(QuizrDb db, long chatId, CancellationToken ct)
    {
        var team = new Team
        {
            ChatId = new TelegramChatId(chatId),
            Name = "Test team",
            TimeZoneId = "Europe/Berlin",
            Locale = "en",
            EveningBeforeAt = EveningBeforeAt,
            MorningOfAt = MorningOfAt,
            BeforeStartLead = BeforeStartLead,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return team;
    }

    private static async Task<Game> SeedGameAsync(
        QuizrDb db,
        Team team,
        DateTimeOffset startsAt,
        int capacity,
        CancellationToken ct
    )
    {
        var creator = new Player
        {
            TelegramUserId = new TelegramUserId(team.ChatId.Value * 1000),
            DisplayName = "Creator",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(creator);
        await db.SaveChangesAsync(ct);

        var game = new Game
        {
            TeamId = team.Id,
            Title = "Quiz Night",
            Venue = "The Pub",
            StartsAt = startsAt,
            Capacity = capacity,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = creator.Id,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);
        return game;
    }

    private static async Task<Player> SeedPlayerAsync(
        QuizrDb db,
        long telegramUserId,
        CancellationToken ct,
        bool dmEnabled = true,
        string? displayName = null
    )
    {
        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = displayName ?? $"Player{telegramUserId}",
            DmEnabled = dmEnabled,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);
        return player;
    }

    private static async Task SeedSignupAsync(
        QuizrDb db,
        Game game,
        Player player,
        DateTimeOffset createdAt,
        CancellationToken ct
    )
    {
        db.Signups.Add(
            new Signup
            {
                GameId = game.Id,
                PlayerId = player.Id,
                CreatedAt = createdAt,
            }
        );
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedMembershipAsync(
        QuizrDb db,
        Team team,
        Player player,
        ReminderChannel eveningBefore,
        CancellationToken ct,
        ReminderChannel? morningOf = null,
        ReminderChannel? beforeStart = null,
        bool remindWhenReserve = false
    )
    {
        db.Memberships.Add(
            new Membership
            {
                TeamId = team.Id,
                PlayerId = player.Id,
                EveningBefore = eveningBefore,
                MorningOf = morningOf ?? eveningBefore,
                BeforeStart = beforeStart ?? eveningBefore,
                RemindWhenReserve = remindWhenReserve,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);
    }
}
