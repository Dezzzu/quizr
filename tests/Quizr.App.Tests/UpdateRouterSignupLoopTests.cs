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

[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class UpdateRouterSignupLoopTests
{
    private static readonly TimeSpan WindowPastDebounce = TimeSpan.FromSeconds(2);

    private readonly PostgresFixture _fixture;

    public UpdateRouterSignupLoopTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task JoiningRewritesTheAnnouncementWithThePlayersName()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
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

    [Test]
    public async Task JoiningTwiceIsRejectedWithAnAlert()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
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

    [Test]
    public async Task BringingAGuestStartsANamingDialogThatANamedReplyResolves()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7003, capacity: 5, ct);
        var (router, bot, clock) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(
                7003,
                7003,
                "Alice",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
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

        // Scoped to this chat — PostgresFixture shares one database across every test in the
        // class, so an unscoped count picks up other tests' dialogs too.
        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(7003), ct))
            .Should()
            .Be(0);
        var guest = await db.Signups.AsNoTracking().SingleAsync(s => s.GameId == game.Id && s.PlayerId == null, ct);
        guest.GuestName.Should().Be("Sasha");
        bot.EditedTexts()
            .Should()
            .ContainSingle(text => text != null && text.Contains("Sasha", StringComparison.Ordinal));
    }

    [Test]
    public async Task SkippingTheGuestNamePromptLeavesTheGuestAnonymous()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7004, capacity: 5, ct);
        var (router, _, _) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(
                7004,
                7004,
                "Alice",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
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

        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(7004), ct)).Should().Be(0);
        (await db.Signups.AsNoTracking().SingleAsync(s => s.Id == guest.Id, ct)).GuestName.Should().BeNull();
    }

    [Test]
    public async Task DroppingRequiresConfirmationBeforeCancellingTheSignup()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
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

    [Test]
    public async Task StayingKeepsTheSignupLive()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
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

    [Test]
    public async Task ConfirmingADropPromotesTheReserveAndSendsAPromotionMessage()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
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

    // A capacity increase can promote several reserves at once — this is what makes that a
    // single "🎉 X, Y moved up!" message rather than one send per person.
    [Test]
    public async Task IncreasingCapacityPromotesMultipleReservesInOneMessage()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7050, capacity: 1, ct);
        var captainId = 7050 * 1000;
        db.Memberships.Add(
            new Membership
            {
                TeamId = game.TeamId,
                PlayerId = game.CreatedByPlayerId,
                IsCaptain = true,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);
        var (router, bot, _) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(
                7050,
                70501,
                "Alice",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(
                7050,
                70502,
                "Bob",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(
                7050,
                70503,
                "Carol",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );

        await router.RouteAsync(MessageUpdate(7050, captainId, "Creator", "/editgame"), ct);
        await router.RouteAsync(
            CallbackUpdate(
                7050,
                captainId,
                "Creator",
                CallbackData.Format(CallbackData.PickGameToEdit, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(
                7050,
                captainId,
                "Creator",
                CallbackData.Format(CallbackData.EditField, EditGameDialogData.Capacity),
                announcementMessageId: 1
            ),
            ct
        );
        await router.RouteAsync(MessageUpdate(7050, captainId, "Creator", "3"), ct);

        var promotionMessages = bot.SentTexts(7050)
            .Where(text => text.Contains("moved up", StringComparison.Ordinal))
            .ToList();
        promotionMessages.Should().ContainSingle();
        promotionMessages[0].Should().Contain("Bob").And.Contain("Carol");
    }

    // RenderEditGameFieldPicker had no Done button at all — unlike the analogous Franchise
    // field picker, there was no way to dismiss it once shown.
    [Test]
    public async Task DoneClearsTheEditGameFieldPickerKeyboard()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7053, capacity: 5, ct);
        var captainId = 7053 * 1000;
        db.Memberships.Add(
            new Membership
            {
                TeamId = game.TeamId,
                PlayerId = game.CreatedByPlayerId,
                IsCaptain = true,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);
        var (router, bot, _) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(7053, captainId, "Creator", "/editgame"), ct);
        await router.RouteAsync(
            CallbackUpdate(
                7053,
                captainId,
                "Creator",
                CallbackData.Format(CallbackData.PickGameToEdit, game.Id),
                announcementMessageId: 1
            ),
            ct
        );

        await router.RouteAsync(
            CallbackUpdate(
                7053,
                captainId,
                "Creator",
                CallbackData.Format(CallbackData.CloseView, 0L),
                announcementMessageId: 2
            ),
            ct
        );

        bot.ClearedKeyboards().Should().ContainSingle(r => r.MessageId == 2);
        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(7053), ct)).Should().Be(0);
    }

    // A promoted signup can be an anonymous guest (invariant 5 only requires a live inviter,
    // not a name) — the fallback label must come from the strings table like everything else
    // user-visible, not a bare English literal.
    [Test]
    public async Task PromotingAnAnonymousGuestUsesTheLocalizedFallbackLabel()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        // Capacity 2: Alice and Bob both play, so Bob's guest lands in reserve — when Alice
        // drops, the guest is next in queue order ahead of Bob, who's already playing.
        var game = await SeedGameAsync(db, chatId: 7051, capacity: 2, ct);
        var (router, bot, _) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(
                7051,
                70511,
                "Alice",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(
                7051,
                70512,
                "Bob",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(
                7051,
                70512,
                "Bob",
                CallbackData.Format(CallbackData.Guest, game.Id),
                announcementMessageId: 1
            ),
            ct
        );

        await router.RouteAsync(
            CallbackUpdate(
                7051,
                70511,
                "Alice",
                CallbackData.Format(CallbackData.Drop, game.Id),
                announcementMessageId: 2
            ),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(
                7051,
                70511,
                "Alice",
                CallbackData.Format(CallbackData.ConfirmDrop, game.Id),
                announcementMessageId: 2
            ),
            ct
        );

        var promotionMessages = bot.SentTexts(7051)
            .Where(text => text.Contains("moved up", StringComparison.Ordinal))
            .ToList();
        promotionMessages.Should().ContainSingle();
        promotionMessages[0].Should().Contain("Guest");
    }

    [Test]
    public async Task ConfirmingADropSurfacesANamedGuestChoiceThatCanBeKept()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
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

    [Test]
    public async Task AGuestCanBeRemovedWithoutDroppingTheInviter()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7010, capacity: 5, ct);
        var (router, bot, _) = CreateRouter(db);
        await router.RouteAsync(
            CallbackUpdate(
                7010,
                7010,
                "Alice",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(
                7010,
                7010,
                "Alice",
                CallbackData.Format(CallbackData.Guest, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        var guest = await db.Signups.AsNoTracking().SingleAsync(s => s.GameId == game.Id && s.PlayerId == null, ct);

        await router.RouteAsync(
            CallbackUpdate(
                7010,
                7010,
                "Alice",
                CallbackData.Format(CallbackData.MyGuests, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        bot.SentTexts().Should().Contain(text => text.Contains("Your guests", StringComparison.Ordinal));

        await router.RouteAsync(
            CallbackUpdate(
                7010,
                7010,
                "Alice",
                CallbackData.Format(CallbackData.RemoveGuest, guest.Id),
                announcementMessageId: 3
            ),
            ct
        );

        (await db.Signups.AsNoTracking().SingleAsync(s => s.Id == guest.Id, ct)).CancelledAt.Should().NotBeNull();
        (await db.Signups.AsNoTracking().SingleAsync(s => s.GameId == game.Id && s.PlayerId != null, ct))
            .CancelledAt.Should()
            .BeNull();
    }

    // The Manage guests view previously had no way to end the interaction — removing a guest
    // just re-rendered the same "remove one, or add another" menu forever.
    [Test]
    public async Task DoneClearsTheMyGuestsKeyboardWithoutTouchingTheMessageText()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7052, capacity: 5, ct);
        var (router, bot, _) = CreateRouter(db);
        await router.RouteAsync(
            CallbackUpdate(
                7052,
                7052,
                "Alice",
                CallbackData.Format(CallbackData.MyGuests, game.Id),
                announcementMessageId: 1
            ),
            ct
        );

        await router.RouteAsync(
            CallbackUpdate(
                7052,
                7052,
                "Alice",
                CallbackData.Format(CallbackData.CloseView, 0L),
                announcementMessageId: 2
            ),
            ct
        );

        bot.ClearedKeyboards().Should().ContainSingle(r => r.MessageId == 2);
        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(7052), ct)).Should().Be(0);
    }

    [Test]
    public async Task AddingASecondGuestFromTheMyGuestsViewWorksJustLikeTheAnnouncementButton()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 7011, capacity: 5, ct);
        var (router, _, _) = CreateRouter(db);
        await router.RouteAsync(
            CallbackUpdate(
                7011,
                7011,
                "Alice",
                CallbackData.Format(CallbackData.Join, game.Id),
                announcementMessageId: 1
            ),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(
                7011,
                7011,
                "Alice",
                CallbackData.Format(CallbackData.Guest, game.Id),
                announcementMessageId: 1
            ),
            ct
        );

        // The "Add another guest" button on the My guests view reuses the same Guest verb.
        await router.RouteAsync(
            CallbackUpdate(
                7011,
                7011,
                "Alice",
                CallbackData.Format(CallbackData.Guest, game.Id),
                announcementMessageId: 1
            ),
            ct
        );

        (await db.Signups.AsNoTracking().CountAsync(s => s.GameId == game.Id && s.PlayerId == null, ct)).Should().Be(2);
    }

    private static (UpdateRouter Router, ITelegramBotClient Bot, FakeTimeProvider Clock) CreateRouter(QuizrDb db)
    {
        var bot = TelegramBotClientTestHelper.Create();
        var clock = new FakeTimeProvider();
        var sender = new MessageSender(
            bot,
            new MessageEditDebouncer(
                bot,
                clock,
                TelegramBotClientTestHelper.NullScopeFactory(),
                NullLogger<MessageEditDebouncer>.Instance
            )
        );
        var strings = new Strings();
        var teamBootstrap = new TeamBootstrapService(db, sender, strings, clock);
        var playerBootstrap = new PlayerBootstrapService(db, clock);
        var teamGuard = new TeamGuard(db, bot);
        var teams = new TeamService(db, teamGuard, clock);
        var dialogs = new DialogService(db, teamGuard, clock);
        var signups = new SignupService(db, teamGuard, clock);
        var franchises = new FranchiseService(db, teamGuard, clock);
        var games = new GameService(db, teamGuard, clock);
        var participations = new ParticipationService(db, teamGuard, clock);
        var announcements = new AnnouncementService(db, sender, strings);
        var board = new BoardService(db, sender, bot, strings, NullLogger<BoardService>.Instance);

        var router = new UpdateRouter(
            db,
            sender,
            bot,
            strings,
            teamBootstrap,
            playerBootstrap,
            teams,
            dialogs,
            signups,
            franchises,
            games,
            participations,
            announcements,
            board,
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
