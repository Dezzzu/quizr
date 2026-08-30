using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.App.Telegram;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot;
using Telegram.Bot.Types;
using Game = Quizr.Domain.Entities.Game;

namespace Quizr.App.Tests;

// M9: reminders, act-on-behalf, decline, finish, captain grant/revoke — one full-flow test per
// feature, matching CLAUDE.md invariant 13's audit trail everywhere a captain acts on someone
// else's behalf.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class UpdateRouterM9Tests
{
    private readonly PostgresFixture _fixture;

    public UpdateRouterM9Tests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task MyRemindersCyclesAChannelThroughOffGroupDmAndBack()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8001, ct);
        var player = await SeedMemberAsync(db, team.Id, telegramUserId: 8001, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(8001, 8001, "/myreminders"), ct);

        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == player.Id, ct))
            .EveningBefore.Should()
            .Be(ReminderChannel.Off);

        await router.RouteAsync(
            CallbackUpdate(8001, 8001, CallbackData.Format(CallbackData.CycleReminderChannel, 0L)),
            ct
        );
        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == player.Id, ct))
            .EveningBefore.Should()
            .Be(ReminderChannel.Group);

        await router.RouteAsync(
            CallbackUpdate(8001, 8001, CallbackData.Format(CallbackData.CycleReminderChannel, 0L)),
            ct
        );
        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == player.Id, ct))
            .EveningBefore.Should()
            .Be(ReminderChannel.Dm);

        await router.RouteAsync(
            CallbackUpdate(8001, 8001, CallbackData.Format(CallbackData.CycleReminderChannel, 0L)),
            ct
        );
        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == player.Id, ct))
            .EveningBefore.Should()
            .Be(ReminderChannel.Off);
    }

    [Test]
    public async Task MyRemindersTogglesTheReserveReminderFlag()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8002, ct);
        var player = await SeedMemberAsync(db, team.Id, telegramUserId: 8002, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(8002, 8002, "/myreminders"), ct);
        await router.RouteAsync(
            CallbackUpdate(8002, 8002, CallbackData.Format(CallbackData.ToggleReserveReminder, 0L)),
            ct
        );

        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == player.Id, ct))
            .RemindWhenReserve.Should()
            .BeTrue();
    }

    [Test]
    public async Task ManagePlayersRegistersAndThenDropsAMemberOnTheirBehalfWithAnAuditTrail()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8003, capacity: 5, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80031, ct);
        var target = await SeedMemberAsync(db, game.TeamId, telegramUserId: 80032, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8003, 80031, CallbackData.Format(CallbackData.ManagePlayers, game.Id)),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(8003, 80031, CallbackData.Format(CallbackData.TogglePlayerSignup, target.Id)),
            ct
        );

        (
            await db
                .Signups.AsNoTracking()
                .CountAsync(s => s.GameId == game.Id && s.PlayerId == target.Id && s.CancelledAt == null, ct)
        )
            .Should()
            .Be(1);
        var registerEntry = await db.AuditEntries.SingleAsync(
            e => e.GameId == game.Id && e.Action == AuditActions.PlayerRegisteredOnBehalf,
            ct
        );
        registerEntry.ActorPlayerId.Should().Be(captain.Id);

        await router.RouteAsync(
            CallbackUpdate(8003, 80031, CallbackData.Format(CallbackData.TogglePlayerSignup, target.Id)),
            ct
        );

        (
            await db
                .Signups.AsNoTracking()
                .CountAsync(s => s.GameId == game.Id && s.PlayerId == target.Id && s.CancelledAt == null, ct)
        )
            .Should()
            .Be(0);
        var dropEntry = await db.AuditEntries.SingleAsync(
            e => e.GameId == game.Id && e.Action == AuditActions.PlayerDroppedOnBehalf,
            ct
        );
        dropEntry.ActorPlayerId.Should().Be(captain.Id);
    }

    [Test]
    public async Task NonCaptainsCannotOpenManagePlayers()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8004, capacity: 5, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8004, 80041, CallbackData.Format(CallbackData.ManagePlayers, game.Id)),
            ct
        );

        bot.AnsweredCallbackAlerts().Should().ContainSingle();
        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(8004), ct)).Should().Be(0);
    }

    [Test]
    public async Task DeclineRequiresConfirmationAndRecordsAnAuditEntry()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8005, capacity: 5, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80051, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8005, 80051, CallbackData.Format(CallbackData.DeclineGame, game.Id)),
            ct
        );
        (await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct)).DeclinedAt.Should().BeNull();

        await router.RouteAsync(
            CallbackUpdate(8005, 80051, CallbackData.Format(CallbackData.ConfirmDecline, game.Id)),
            ct
        );

        (await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct)).DeclinedAt.Should().NotBeNull();
        var entry = await db.AuditEntries.SingleAsync(
            e => e.GameId == game.Id && e.Action == AuditActions.GameDeclined,
            ct
        );
        entry.ActorPlayerId.Should().Be(captain.Id);
    }

    [Test]
    public async Task CancellingTheDeclinePromptLeavesTheGameUntouched()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8006, capacity: 5, ct);
        await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80061, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8006, 80061, CallbackData.Format(CallbackData.DeclineGame, game.Id)),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(8006, 80061, CallbackData.Format(CallbackData.CancelDecline, game.Id)),
            ct
        );

        (await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct)).DeclinedAt.Should().BeNull();
        (await db.AuditEntries.CountAsync(e => e.GameId == game.Id, ct)).Should().Be(0);
    }

    [Test]
    public async Task FinishButtonMaterializesParticipationAndRecordsAnAuditEntry()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8007, capacity: 5, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80071, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(CallbackUpdate(8007, 80071, CallbackData.Format(CallbackData.FinishGame, game.Id)), ct);

        (await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct)).FinishedAt.Should().NotBeNull();
        var entry = await db.AuditEntries.SingleAsync(
            e => e.GameId == game.Id && e.Action == AuditActions.GameFinished,
            ct
        );
        entry.ActorPlayerId.Should().Be(captain.Id);
    }

    [Test]
    public async Task ManageCaptainsGrantsAndThenRevokesCaptaincyWithAnAuditTrail()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8008, ct);
        var captain = await SeedCaptainAsync(db, team.Id, telegramUserId: 80081, ct);
        var target = await SeedMemberAsync(db, team.Id, telegramUserId: 80082, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(8008, 80081, "/managecaptains"), ct);
        await router.RouteAsync(
            CallbackUpdate(8008, 80081, CallbackData.Format(CallbackData.ToggleCaptain, target.Id)),
            ct
        );

        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == target.Id, ct)).IsCaptain.Should().BeTrue();
        var grantEntry = await db.AuditEntries.SingleAsync(e => e.Action == AuditActions.CaptainGranted, ct);
        grantEntry.ActorPlayerId.Should().Be(captain.Id);

        await router.RouteAsync(
            CallbackUpdate(8008, 80081, CallbackData.Format(CallbackData.ToggleCaptain, target.Id)),
            ct
        );

        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == target.Id, ct))
            .IsCaptain.Should()
            .BeFalse();
        var revokeEntry = await db.AuditEntries.SingleAsync(e => e.Action == AuditActions.CaptainRevoked, ct);
        revokeEntry.ActorPlayerId.Should().Be(captain.Id);
    }

    [Test]
    public async Task NonCaptainsCannotManageCaptains()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8009, ct);
        await SeedMemberAsync(db, team.Id, telegramUserId: 8009, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(8009, 8009, "/managecaptains"), ct);

        bot.SentTexts().Should().ContainSingle(text => text.Contains("captain", StringComparison.OrdinalIgnoreCase));
    }

    // Editing a finished game's roster (invariant 11's second half) is also a captain action
    // that affects someone else's record — added to invariant 13's list after the first pass
    // at audit logging turned out to have missed it.
    [Test]
    public async Task TogglingAttendedOnAFinishedGamesRosterRecordsAnAuditEntry()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (game, participation) = await SeedFinishedGameWithParticipationAsync(db, chatId: 8010, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80101, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8010, 80101, CallbackData.Format(CallbackData.ToggleAttended, participation.Id)),
            ct
        );

        (await db.Participations.AsNoTracking().SingleAsync(p => p.Id == participation.Id, ct))
            .Attended.Should()
            .BeFalse();
        var entry = await db.AuditEntries.SingleAsync(e => e.GameId == game.Id, ct);
        entry.Action.Should().Be(AuditActions.ParticipationAttendedToggled);
        entry.ActorPlayerId.Should().Be(captain.Id);
    }

    [Test]
    public async Task AddingAVenuePlayerRecordsAnAuditEntry()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (game, _) = await SeedFinishedGameWithParticipationAsync(db, chatId: 8011, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80111, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(CallbackUpdate(8011, 80111, CallbackData.Format(CallbackData.AddPlayer, game.Id)), ct);
        await router.RouteAsync(MessageUpdate(8011, 80111, "Walk-in Wendy"), ct);

        var added = await db.Participations.AsNoTracking().SingleAsync(p => p.Name == "Walk-in Wendy", ct);
        added.Kind.Should().Be(ParticipationKind.VenueAssigned);
        var entry = await db.AuditEntries.SingleAsync(
            e => e.GameId == game.Id && e.Action == AuditActions.VenuePlayerAdded,
            ct
        );
        entry.ActorPlayerId.Should().Be(captain.Id);
    }

    private static (UpdateRouter Router, ITelegramBotClient Bot) CreateRouter(QuizrDb db)
    {
        var bot = TelegramBotClientTestHelper.Create();
        var clock = new FakeTimeProvider();
        var sender = new MessageSender(bot, new MessageEditDebouncer(bot, clock));
        var strings = new Strings();
        var teamBootstrap = new TeamBootstrapService(db, sender, strings, clock);
        var playerBootstrap = new PlayerBootstrapService(db, clock);
        var teamGuard = new TeamGuard(db, bot);
        var signups = new SignupService(db, clock);
        var franchises = new FranchiseService(db, clock);
        var games = new GameService(db, clock);
        var participations = new ParticipationService(db, clock);
        var announcements = new AnnouncementService(db, sender, strings);
        var board = new BoardService(db, sender, bot, strings);

        var router = new UpdateRouter(
            db,
            sender,
            bot,
            strings,
            teamBootstrap,
            playerBootstrap,
            teamGuard,
            signups,
            franchises,
            games,
            participations,
            announcements,
            board,
            clock,
            NullLogger<UpdateRouter>.Instance
        );

        return (router, bot);
    }

    private static async Task<Team> SeedTeamAsync(QuizrDb db, long chatId, CancellationToken ct)
    {
        var team = new Team
        {
            ChatId = new TelegramChatId(chatId),
            Name = "Test team",
            TimeZoneId = "Europe/Berlin",
            Locale = "en",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return team;
    }

    private static async Task<Player> SeedMemberAsync(
        QuizrDb db,
        TeamId teamId,
        long telegramUserId,
        CancellationToken ct
    )
    {
        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = $"Player {telegramUserId}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);

        db.Memberships.Add(
            new Membership
            {
                TeamId = teamId,
                PlayerId = player.Id,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);
        return player;
    }

    private static async Task<Player> SeedCaptainAsync(
        QuizrDb db,
        TeamId teamId,
        long telegramUserId,
        CancellationToken ct
    )
    {
        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = $"Captain {telegramUserId}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);

        db.Memberships.Add(
            new Membership
            {
                TeamId = teamId,
                PlayerId = player.Id,
                IsCaptain = true,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);
        return player;
    }

    private static async Task<Game> SeedGameAsync(QuizrDb db, long chatId, int capacity, CancellationToken ct)
    {
        var team = await SeedTeamAsync(db, chatId, ct);
        var creator = await SeedMemberAsync(db, team.Id, telegramUserId: chatId * 1000, ct);

        var game = new Game
        {
            TeamId = team.Id,
            Title = "Quiz Night",
            Venue = "The Pub",
            StartsAt = DateTimeOffset.UtcNow.AddDays(1),
            Capacity = capacity,
            AnnouncementMessageId = new TelegramMessageId(1),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = creator.Id,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);

        return game;
    }

    private static async Task<(Game Game, Participation Participation)> SeedFinishedGameWithParticipationAsync(
        QuizrDb db,
        long chatId,
        CancellationToken ct
    )
    {
        var team = await SeedTeamAsync(db, chatId, ct);
        var creator = await SeedMemberAsync(db, team.Id, telegramUserId: chatId * 1000, ct);

        var game = new Game
        {
            TeamId = team.Id,
            Title = "Quiz Night",
            Venue = "The Pub",
            StartsAt = DateTimeOffset.UtcNow.AddHours(-5),
            FinishedAt = DateTimeOffset.UtcNow,
            Capacity = 10,
            AnnouncementMessageId = new TelegramMessageId(1),
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-5),
            CreatedByPlayerId = creator.Id,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);

        var participation = new Participation
        {
            GameId = game.Id,
            PlayerId = creator.Id,
            Kind = ParticipationKind.Member,
            Played = true,
            Attended = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Participations.Add(participation);
        await db.SaveChangesAsync(ct);

        return (game, participation);
    }

    private static Update CallbackUpdate(long chatId, long telegramUserId, string data) =>
        new()
        {
            Id = 1,
            CallbackQuery = new CallbackQuery
            {
                Id = "cq1",
                From = new User { Id = telegramUserId, FirstName = "Test" },
                Data = data,
                Message = new Message
                {
                    Id = 1,
                    Chat = new Chat { Id = chatId },
                    Date = DateTime.UtcNow,
                },
            },
        };

    private static Update MessageUpdate(long chatId, long telegramUserId, string text) =>
        new()
        {
            Id = 1,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = chatId },
                From = new User { Id = telegramUserId, FirstName = "Test" },
                Text = text,
                Date = DateTime.UtcNow,
            },
        };
}
