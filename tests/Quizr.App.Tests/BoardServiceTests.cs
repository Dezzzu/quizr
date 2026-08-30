using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
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
using Telegram.Bot.Types.Enums;
using Game = Quizr.Domain.Entities.Game;

namespace Quizr.App.Tests;

public class BoardServiceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public BoardServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RefreshAsyncPostsAndPinsTheBoardWhenThereIsNoneYet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8001, ct);
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(db, new MessageSender(bot, NoDebounce(bot)), bot, new Strings());

        await service.RefreshAsync(team, ct);

        bot.SentTexts()
            .Should()
            .ContainSingle(text => text.Contains("No upcoming games yet", StringComparison.Ordinal));
        bot.PinCallCount().Should().Be(1);
        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).BoardMessageId.Should().NotBeNull();
    }

    [Fact]
    public async Task RefreshAsyncEditsTheExistingBoardInPlaceRatherThanPostingANewOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8002, ct);
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(db, new MessageSender(bot, NoDebounce(bot)), bot, new Strings());
        await service.RefreshAsync(team, ct);
        var boardMessageId = team.BoardMessageId!.Value;
        StubGetChat(bot, pinnedMessageId: (int)boardMessageId.Value);
        await SeedGameAsync(db, team, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1), ct);

        await service.RefreshAsync(team, ct);

        bot.EditedTexts()
            .Should()
            .ContainSingle(text => text != null && text.Contains("Quiz Night", StringComparison.Ordinal));
        team.BoardMessageId!.Value.Should().Be(boardMessageId);
    }

    [Fact]
    public async Task RefreshAsyncRePinsWhenSomethingElseHasBeenPinnedOverTheBoard()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8003, ct);
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(db, new MessageSender(bot, NoDebounce(bot)), bot, new Strings());
        await service.RefreshAsync(team, ct);
        StubGetChat(bot, pinnedMessageId: 999999); // some other message is now pinned

        await service.RefreshAsync(team, ct);

        bot.PinCallCount().Should().Be(2);
    }

    [Fact]
    public async Task RefreshAsyncDoesNotRePinWhenAlreadyCorrectlyPinned()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8004, ct);
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(db, new MessageSender(bot, NoDebounce(bot)), bot, new Strings());
        await service.RefreshAsync(team, ct);
        StubGetChat(bot, pinnedMessageId: (int)team.BoardMessageId!.Value.Value);

        await service.RefreshAsync(team, ct);

        bot.PinCallCount().Should().Be(1);
    }

    [Fact]
    public async Task RefreshAsyncRepostsAndRePinsWhenTheBoardMessageIsGone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8005, ct);
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(db, new MessageSender(bot, NoDebounce(bot)), bot, new Strings());
        await service.RefreshAsync(team, ct);
        var originalMessageId = team.BoardMessageId!.Value;
        bot.SendRequest(
                Arg.Is<EditMessageTextRequest>(r => r.MessageId == (int)originalMessageId.Value),
                Arg.Any<CancellationToken>()
            )
            .ThrowsAsync(new ApiRequestException("Bad Request: message to edit not found", 400));

        await service.RefreshAsync(team, ct);

        team.BoardMessageId!.Value.Should().NotBe(originalMessageId);
        bot.PinCallCount().Should().Be(2);
    }

    [Fact]
    public async Task RefreshAsyncListsOnlyUpcomingGames()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8006, ct);
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(db, new MessageSender(bot, NoDebounce(bot)), bot, new Strings());
        var upcoming = await SeedGameAsync(db, team, "Upcoming Quiz", DateTimeOffset.UtcNow.AddDays(1), ct);
        var finished = await SeedGameAsync(db, team, "Finished Quiz", DateTimeOffset.UtcNow.AddDays(-1), ct);
        finished.FinishedAt = DateTimeOffset.UtcNow;
        var declined = await SeedGameAsync(db, team, "Declined Quiz", DateTimeOffset.UtcNow.AddDays(2), ct);
        declined.DeclinedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await service.RefreshAsync(team, ct);

        var text = bot.SentTexts().Single();
        text.Should().Contain(upcoming.Title);
        text.Should().NotContain("Finished Quiz");
        text.Should().NotContain("Declined Quiz");
    }

    // The debouncer's own delay isn't what's under test here; a zero-window instance still
    // exercises the real coalescing code path.
    private static MessageEditDebouncer NoDebounce(ITelegramBotClient bot) => new(bot, TimeProvider.System);

    private static void StubGetChat(ITelegramBotClient bot, int pinnedMessageId) =>
        bot.SendRequest(Arg.Any<GetChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatFullInfo
                {
                    Id = 1,
                    Type = ChatType.Supergroup,
                    PinnedMessage = new Message { Id = pinnedMessageId },
                }
            );

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

    private static async Task<Game> SeedGameAsync(
        QuizrDb db,
        Team team,
        string title,
        DateTimeOffset startsAt,
        CancellationToken ct
    )
    {
        var creator = new Player
        {
            TelegramUserId = new TelegramUserId(team.ChatId.Value * 1000 + startsAt.Ticks % 1000),
            DisplayName = "Creator",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(creator);
        await db.SaveChangesAsync(ct);

        var game = new Game
        {
            TeamId = team.Id,
            Title = title,
            Venue = "The Pub",
            StartsAt = startsAt,
            Capacity = 10,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = creator.Id,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);
        return game;
    }
}
