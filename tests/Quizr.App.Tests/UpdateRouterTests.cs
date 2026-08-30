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

namespace Quizr.App.Tests;

public class UpdateRouterTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public UpdateRouterTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SetTimeZoneRejectsAnUnrecognisedId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4001, telegramUserId: 4001, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4001, 4001, "/settimezone Nowhere/Fake"), ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).TimeZoneId.Should().BeNull();
        bot.SentTexts().Should().ContainSingle(text => text.Contains("Nowhere/Fake", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetTimeZoneAcceptsAValidIanaId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4002, telegramUserId: 4002, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4002, 4002, "/settimezone Europe/Berlin"), ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).TimeZoneId.Should().Be("Europe/Berlin");
        bot.SentTexts().Should().ContainSingle(text => text.Contains("Europe/Berlin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewGameIsRefusedBeforeATimeZoneIsSet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        await SeedCaptainedTeamAsync(db, chatId: 4003, telegramUserId: 4003, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4003, 4003, "/newgame"), ct);

        bot.SentTexts().Should().ContainSingle(text => text.Contains("timezone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NewGameOffersTheBranchChoiceOnceATimeZoneIsSet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4004, telegramUserId: 4004, ct);
        team.TimeZoneId = "Europe/Berlin";
        await db.SaveChangesAsync(ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4004, 4004, "/newgame"), ct);

        bot.SentTexts().Should().ContainSingle(text => text.Contains("franchise", StringComparison.OrdinalIgnoreCase));
        (await db.DialogStates.SingleOrDefaultAsync(d => d.ChatId == new TelegramChatId(4004), ct))
            .Should()
            .NotBeNull();
    }

    [Fact]
    public async Task SetLanguageRejectsAnUnsupportedCode()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4010, telegramUserId: 4010, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4010, 4010, "/setlanguage fr"), ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).Locale.Should().Be("en");
        bot.SentTexts().Should().ContainSingle(text => text.Contains("fr", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetLanguageAcceptsASupportedCodeAndConfirmsInTheNewLanguage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4011, telegramUserId: 4011, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4011, 4011, "/setlanguage ru"), ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).Locale.Should().Be("ru");
        bot.SentTexts().Should().ContainSingle(text => text.Contains("ru", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonCaptainsCannotSetTheLanguage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 4012, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4012, 1, "/setlanguage ru"), ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).Locale.Should().Be("en");
        bot.SentTexts().Should().ContainSingle(text => text.Contains("captain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MyLanguageSetsThePlayersOwnLocaleWithoutTouchingTheTeams()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 4013, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4013, 4013, "/mylanguage de"), ct);

        (await db.Players.AsNoTracking().SingleAsync(p => p.TelegramUserId == new TelegramUserId(4013), ct))
            .Locale.Should()
            .Be("de");
        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).Locale.Should().Be("en");
        bot.SentTexts().Should().ContainSingle();
    }

    [Fact]
    public async Task MyLanguageRejectsAnUnsupportedCode()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        await SeedTeamAsync(db, chatId: 4014, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4014, 4014, "/mylanguage klingon"), ct);

        (await db.Players.AsNoTracking().SingleAsync(p => p.TelegramUserId == new TelegramUserId(4014), ct))
            .Locale.Should()
            .BeNull();
        bot.SentTexts().Should().ContainSingle(text => text.Contains("klingon", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonCaptainsCannotSetTheTimeZone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        await SeedTeamAsync(db, chatId: 4005, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4005, 1, "/settimezone Europe/Berlin"), ct);

        bot.SentTexts().Should().ContainSingle(text => text.Contains("captain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartGreetsAndLazilyCreatesThePlayer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        await SeedTeamAsync(db, chatId: 4006, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4006, 42, "/start"), ct);

        bot.SentTexts().Should().ContainSingle();
        (await db.Players.SingleOrDefaultAsync(p => p.TelegramUserId == new TelegramUserId(42), ct))
            .Should()
            .NotBeNull();
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
            Locale = "en",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return team;
    }

    private static async Task<Team> SeedCaptainedTeamAsync(
        QuizrDb db,
        long chatId,
        long telegramUserId,
        CancellationToken ct
    )
    {
        var team = await SeedTeamAsync(db, chatId, ct);

        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = "Captain",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);

        db.Memberships.Add(
            new Membership
            {
                TeamId = team.Id,
                PlayerId = player.Id,
                IsCaptain = true,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);

        return team;
    }

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
