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

public class UpdateRouterSignupLoopTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan WindowPastDebounce = TimeSpan.FromSeconds(2);

    private readonly PostgresFixture _fixture;

    public UpdateRouterSignupLoopTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task JoiningRewritesTheAnnouncementWithThePlayersName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7001, capacity: 5, ct);
        var (router, bot, clock) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(
                7001,
                7001,
                "Alice",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        await FlushDebouncedEditsAsync(bot, clock, ct);

        bot.EditedTexts()
            .Should()
            .ContainSingle(text => text != null && text.Contains("Alice", StringComparison.Ordinal));
        (await db.Signups.AsNoTracking().CountAsync(s => s.GameId == game.Id && s.CancelledAt == null, ct))
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task JoiningTwiceIsRejectedWithAnAlert()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7002, capacity: 5, ct);
        var (router, bot, _) = CreateRouter(db);
        var joinData = CallbackData.Format(CallbackData.Join, game.Id);

        await router.RouteAsync(CallbackUpdate(7002, 7002, "Alice", joinData, announcementMessageId: 1), ct);
        await router.RouteAsync(CallbackUpdate(7002, 7002, "Alice", joinData, announcementMessageId: 1), ct);

        (await db.Signups.AsNoTracking().CountAsync(s => s.GameId == game.Id && s.CancelledAt == null, ct))
            .Should()
            .Be(1);
        bot.AnsweredCallbackAlerts().Should().ContainSingle();
    }

    [Fact]
    public async Task BringingAGuestStartsANamingDialogThatANamedReplyResolves()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7003, capacity: 5, ct);
        var (router, bot, clock) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(
                7003,
                7003,
                "Alice",
                CallbackData.Format(CallbackData.Guest, game.Id),
                announcementMessageId: 1
            ),
            ct
        );

        var dialog = await db.DialogStates.SingleAsync(d => d.ChatId == new TelegramChatId(7003), ct);
        dialog.Kind.Should().Be(DialogKinds.NameGuest);

        await router.RouteAsync(MessageUpdate(7003, 7003, "Alice", "Sasha"), ct);
        await FlushDebouncedEditsAsync(bot, clock, ct);

        (await db.DialogStates.CountAsync(ct)).Should().Be(0);
        var guest = await db.Signups.AsNoTracking().SingleAsync(s => s.GameId == game.Id && s.PlayerId == null, ct);
        guest.GuestName.Should().Be("Sasha");
        bot.EditedTexts()
            .Should()
            .ContainSingle(text => text != null && text.Contains("Sasha", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SkippingTheGuestNamePromptLeavesTheGuestAnonymous()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7004, capacity: 5, ct);
        var (router, _, _) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(
                7004,
                7004,
                "Alice",
                CallbackData.Format(CallbackData.Guest, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        var guest = await db.Signups.AsNoTracking().SingleAsync(s => s.GameId == game.Id && s.PlayerId == null, ct);

        await router.RouteAsync(
            CallbackUpdate(
                7004,
                7004,
                "Alice",
                CallbackData.Format(CallbackData.SkipGuestName, guest.Id),
                announcementMessageId: 2
            ),
            ct
        );

        (await db.DialogStates.CountAsync(ct)).Should().Be(0);
        (await db.Signups.AsNoTracking().SingleAsync(s => s.Id == guest.Id, ct)).GuestName.Should().BeNull();
    }

    [Fact]
    public async Task DroppingRequiresConfirmationBeforeCancellingTheSignup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7005, capacity: 5, ct);
        var (router, _, _) = CreateRouter(db);
        await router.RouteAsync(
            CallbackUpdate(
                7005,
                7005,
                "Alice",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );

        await router.RouteAsync(
            CallbackUpdate(
                7005,
                7005,
                "Alice",
                CallbackData.Format(CallbackData.Drop, game.Id),
                announcementMessageId: 1
            ),
            ct
        );

        // Tapping "Can't make it" only prompts — the signup must still be live.
        (await db.Signups.AsNoTracking().CountAsync(s => s.GameId == game.Id && s.CancelledAt == null, ct))
            .Should()
            .Be(1);

        await router.RouteAsync(
            CallbackUpdate(
                7005,
                7005,
                "Alice",
                CallbackData.Format(CallbackData.ConfirmDrop, game.Id),
                announcementMessageId: 2
            ),
            ct
        );

        (await db.Signups.AsNoTracking().CountAsync(s => s.GameId == game.Id && s.CancelledAt == null, ct))
            .Should()
            .Be(0);
    }

    [Fact]
    public async Task StayingKeepsTheSignupLive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7006, capacity: 5, ct);
        var (router, _, _) = CreateRouter(db);
        await router.RouteAsync(
            CallbackUpdate(
                7006,
                7006,
                "Alice",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );

        await router.RouteAsync(
            CallbackUpdate(
                7006,
                7006,
                "Alice",
                CallbackData.Format(CallbackData.Stay, game.Id),
                announcementMessageId: 2
            ),
            ct
        );

        (await db.Signups.AsNoTracking().CountAsync(s => s.GameId == game.Id && s.CancelledAt == null, ct))
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task ConfirmingADropPromotesTheReserveAndSendsAPromotionMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7007, capacity: 1, ct);
        var (router, bot, _) = CreateRouter(db);
        await router.RouteAsync(
            CallbackUpdate(
                7007,
                7007,
                "Alice",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(
                7007,
                7008,
                "Bob",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );

        await router.RouteAsync(
            CallbackUpdate(
                7007,
                7007,
                "Alice",
                CallbackData.Format(CallbackData.ConfirmDrop, game.Id),
                announcementMessageId: 2
            ),
            ct
        );

        bot.SentTexts().Should().Contain(text => text.Contains("Bob", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfirmingADropSurfacesANamedGuestChoiceThatCanBeKept()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7009, capacity: 5, ct);
        var (router, _, _) = CreateRouter(db);
        await router.RouteAsync(
            CallbackUpdate(
                7009,
                7009,
                "Alice",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(
                7009,
                7009,
                "Alice",
                CallbackData.Format(CallbackData.Guest, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        var guest = await db.Signups.AsNoTracking().SingleAsync(s => s.GameId == game.Id && s.PlayerId == null, ct);
        await router.RouteAsync(MessageUpdate(7009, 7009, "Alice", "Sasha"), ct);

        await router.RouteAsync(
            CallbackUpdate(
                7009,
                7009,
                "Alice",
                CallbackData.Format(CallbackData.ConfirmDrop, game.Id),
                announcementMessageId: 2
            ),
            ct
        );

        (await db.Signups.AsNoTracking().SingleAsync(s => s.Id == guest.Id, ct)).CancelledAt.Should().BeNull();

        await router.RouteAsync(
            CallbackUpdate(
                7009,
                7009,
                "Alice",
                CallbackData.Format(CallbackData.KeepGuest, guest.Id),
                announcementMessageId: 3
            ),
            ct
        );

        var kept = await db.Signups.AsNoTracking().SingleAsync(s => s.Id == guest.Id, ct);
        kept.CancelledAt.Should().BeNull();
        kept.InvitedByPlayerId.Should().BeNull();
    }

    private static (UpdateRouter Router, ITelegramBotClient Bot, FakeTimeProvider Clock) CreateRouter(QuizrDb db)
    {
        var bot = TelegramBotClientTestHelper.Create();
        var clock = new FakeTimeProvider();
        var sender = new MessageSender(bot, new MessageEditDebouncer(bot, clock));
        var strings = new Strings();
        var teamBootstrap = new TeamBootstrapService(db, sender, strings, clock);
        var playerBootstrap = new PlayerBootstrapService(db, clock);
        var teamGuard = new TeamGuard(db, bot);
        var signups = new SignupService(db, clock);
        var announcements = new AnnouncementService(db, sender, strings);

        var router = new UpdateRouter(
            db,
            sender,
            bot,
            strings,
            teamBootstrap,
            playerBootstrap,
            teamGuard,
            signups,
            announcements,
            clock,
            NullLogger<UpdateRouter>.Instance
        );

        return (router, bot, clock);
    }

    // The announcement edit goes through MessageEditDebouncer's fire-and-forget flush —
    // advance the fake clock past the debounce window, then poll until it lands, mirroring
    // MessageEditDebouncerTests.
    private static async Task FlushDebouncedEditsAsync(
        ITelegramBotClient bot,
        FakeTimeProvider clock,
        CancellationToken ct
    )
    {
        clock.Advance(WindowPastDebounce);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (bot.EditedTexts().Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
        }
    }

    private static async Task<Game> SeedGameAsync(QuizrDb db, long chatId, int capacity, CancellationToken ct)
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

        var creator = new Player
        {
            TelegramUserId = new TelegramUserId(chatId * 1000),
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

    private static Update CallbackUpdate(
        long chatId,
        long telegramUserId,
        string firstName,
        string data,
        int announcementMessageId
    ) =>
        new()
        {
            Id = 1,
            CallbackQuery = new CallbackQuery
            {
                Id = "cq1",
                From = new User { Id = telegramUserId, FirstName = firstName },
                Data = data,
                Message = new Message
                {
                    Id = announcementMessageId,
                    Chat = new Chat { Id = chatId },
                    Date = DateTime.UtcNow,
                },
            },
        };

    private static Update MessageUpdate(long chatId, long telegramUserId, string firstName, string text) =>
        new()
        {
            Id = 1,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = chatId },
                From = new User { Id = telegramUserId, FirstName = firstName },
                Text = text,
                Date = DateTime.UtcNow,
            },
        };
}
