using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
using Game = Quizr.Domain.Entities.Game;

namespace Quizr.App.Tests;

[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class BoardServiceTests
{
    private readonly PostgresFixture _fixture;

    public BoardServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task RefreshAsyncPostsAndPinsTheBoardWhenThereIsNoneYet()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8001, ct);
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(
            db,
            new MessageSender(bot, NoDebounce(bot)),
            bot,
            new Strings(),
            NullLogger<BoardService>.Instance
        );

        await service.RefreshAsync(team, ct);

        bot.SentTexts()
            .Should()
            .ContainSingle(text => text.Contains("No upcoming games yet", StringComparison.Ordinal));
        bot.PinCallCount().Should().Be(1);
        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).BoardMessageId.Should().NotBeNull();
    }

    [Test]
    public async Task RefreshAsyncEditsTheExistingBoardInPlaceRatherThanPostingANewOne()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8002, ct);
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(
            db,
            new MessageSender(bot, NoDebounce(bot)),
            bot,
            new Strings(),
            NullLogger<BoardService>.Instance
        );
        await service.RefreshAsync(team, ct);
        var boardMessageId = team.BoardMessageId!.Value;
        await SeedGameAsync(db, team, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1), ct);

        await service.RefreshAsync(team, ct);

        bot.EditedTexts()
            .Should()
            .ContainSingle(text => text != null && text.Contains("Quiz Night", StringComparison.Ordinal));
        team.BoardMessageId!.Value.Should().Be(boardMessageId);
    }

    // Telegram's getChat doesn't reliably report an unpin performed by anyone other than the
    // bot (confirmed against a live chat), so there's nothing trustworthy to check before
    // deciding whether to re-pin — every tick just does it unconditionally, whether the board
    // is already correctly pinned, displaced by something else, or genuinely unpinned. This
    // replaces what used to be two separate tests (re-pin-when-displaced,
    // skip-when-already-pinned) — that distinction no longer exists in the code.
    [Test]
    public async Task RefreshAsyncPinsOnEveryCallRegardlessOfCurrentPinState()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8003, ct);
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(
            db,
            new MessageSender(bot, NoDebounce(bot)),
            bot,
            new Strings(),
            NullLogger<BoardService>.Instance
        );

        await service.RefreshAsync(team, ct);
        await service.RefreshAsync(team, ct);
        await service.RefreshAsync(team, ct);

        bot.PinCallCount().Should().Be(3);
    }

    // Telegram rejects a redundant pin of the already-pinned message with a 400 rather than a
    // silent success — the common case now that every tick pins unconditionally — and that
    // must not be mistaken for a genuine pin failure.
    [Test]
    public async Task RefreshAsyncDoesNotThrowWhenTheMessageIsAlreadyPinned()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8010, ct);
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(
            db,
            new MessageSender(bot, NoDebounce(bot)),
            bot,
            new Strings(),
            NullLogger<BoardService>.Instance
        );
        await service.RefreshAsync(team, ct);
        bot.SendRequest(Arg.Any<PinChatMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: CHAT_NOT_MODIFIED", 400));

        await service.RefreshAsync(team, ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).BoardMessageId.Should().NotBeNull();
    }

    [Test]
    public async Task RefreshAsyncRepostsAndRePinsWhenTheBoardMessageIsGone()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8005, ct);
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(
            db,
            new MessageSender(bot, NoDebounce(bot)),
            bot,
            new Strings(),
            NullLogger<BoardService>.Instance
        );
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

    // A tick that finds nothing changed since the last one is the ordinary case, not an
    // error — Telegram rejects a no-op edit as a 400 rather than a silent success, so this
    // must not be mistaken for the board message having disappeared and reposted over it.
    [Test]
    public async Task RefreshAsyncDoesNotRepostWhenTelegramReportsNothingChanged()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8009, ct);
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(
            db,
            new MessageSender(bot, NoDebounce(bot)),
            bot,
            new Strings(),
            NullLogger<BoardService>.Instance
        );
        await service.RefreshAsync(team, ct);
        var originalMessageId = team.BoardMessageId!.Value;
        bot.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(
                new ApiRequestException(
                    "Bad Request: message is not modified: specified new message content and reply markup are exactly the same as a current content and reply markup of the message",
                    400
                )
            );
        await service.RefreshAsync(team, ct);

        team.BoardMessageId!.Value.Should().Be(originalMessageId);
        bot.SentTexts().Should().ContainSingle();
        bot.PinCallCount().Should().Be(2); // pins unconditionally on both calls, not just once
    }

    // Invariant 12: "the bot verifies the pin and restores it silently." A missing-rights pin
    // failure — the ordinary state before a captain promotes the bot to admin — must not
    // throw: uncaught, it would trip UpdateDispatcher's unhandled-exception alert and apology
    // message for an expected, self-healing condition instead of staying silent.
    [Test]
    public async Task RefreshAsyncDoesNotThrowWhenTheBotIsNotYetAnAdmin()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8007, ct);
        var bot = TelegramBotClientTestHelper.Create();
        bot.SendRequest(Arg.Any<PinChatMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: not enough rights to pin a message", 400));
        var service = new BoardService(
            db,
            new MessageSender(bot, NoDebounce(bot)),
            bot,
            new Strings(),
            NullLogger<BoardService>.Instance
        );

        await service.RefreshAsync(team, ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).BoardMessageId.Should().NotBeNull();
    }

    // The scheduler ticks every 30 seconds regardless of the previous tick's outcome — this
    // is what makes the pin actually happen once a captain promotes the bot, with nothing
    // else needed from anyone.
    [Test]
    public async Task RefreshAsyncPinsOnALaterCallOnceTheBotBecomesAnAdmin()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8008, ct);
        var bot = TelegramBotClientTestHelper.Create();
        bot.SendRequest(Arg.Any<PinChatMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: not enough rights to pin a message", 400));
        var service = new BoardService(
            db,
            new MessageSender(bot, NoDebounce(bot)),
            bot,
            new Strings(),
            NullLogger<BoardService>.Instance
        );
        await service.RefreshAsync(team, ct);

        bot.SendRequest(Arg.Any<PinChatMessageRequest>(), Arg.Any<CancellationToken>()).Returns(true);
        var teamOnNextTick = await db.Teams.SingleAsync(t => t.Id == team.Id, ct);

        await service.RefreshAsync(teamOnNextTick, ct);

        bot.PinCallCount().Should().Be(2);
    }

    [Test]
    public async Task RefreshAsyncListsOnlyUpcomingGames()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8006, ct);
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(
            db,
            new MessageSender(bot, NoDebounce(bot)),
            bot,
            new Strings(),
            NullLogger<BoardService>.Instance
        );
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

    // BoardRendererTests already proves the pure rendering function links correctly given the
    // right inputs — this proves the inputs actually arrive that way through a real save and
    // a fresh, untracked reload, the same round trip UpdateRouter.HandleConfirmNewGameAsync
    // does: post the announcement, persist AnnouncementMessageId, then refresh the board from
    // a query that never touches the in-memory Game instance that was just written to.
    [Test]
    public async Task RefreshAsyncLinksToTheAnnouncementOncePersistedInARealSupergroup()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: -1009998887771, ct); // real Telegram supergroup ids start with -100
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(
            db,
            new MessageSender(bot, NoDebounce(bot)),
            bot,
            new Strings(),
            NullLogger<BoardService>.Instance
        );
        var game = await SeedGameAsync(db, team, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1), ct);
        game.AnnouncementMessageId = new TelegramMessageId(777);
        await db.SaveChangesAsync(ct);

        await service.RefreshAsync(team, ct);

        bot.SentTexts()
            .Should()
            .ContainSingle(text =>
                text.Contains("""<a href="https://t.me/c/9998887771/777">""", StringComparison.Ordinal)
            );
    }

    // Telegram has no message-link scheme for a basic (non-super) group at all — confirmed
    // through the same real save-and-reload round trip as the supergroup case above, so a
    // team whose chat never got upgraded doesn't quietly get a broken link instead of none.
    [Test]
    public async Task RefreshAsyncDoesNotLinkInARealBasicGroup()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: -998887772, ct); // basic groups: no "-100" prefix
        var bot = TelegramBotClientTestHelper.Create();
        var service = new BoardService(
            db,
            new MessageSender(bot, NoDebounce(bot)),
            bot,
            new Strings(),
            NullLogger<BoardService>.Instance
        );
        var game = await SeedGameAsync(db, team, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1), ct);
        game.AnnouncementMessageId = new TelegramMessageId(778);
        await db.SaveChangesAsync(ct);

        await service.RefreshAsync(team, ct);

        bot.SentTexts().Should().ContainSingle(text => text.Contains("Quiz Night", StringComparison.Ordinal));
        bot.SentTexts().Should().ContainSingle(text => !text.Contains("<a href", StringComparison.Ordinal));
    }

    // The debouncer's own delay isn't what's under test here; a zero-window instance still
    // exercises the real coalescing code path.
    private static MessageEditDebouncer NoDebounce(ITelegramBotClient bot) =>
        new(
            bot,
            TimeProvider.System,
            TelegramBotClientTestHelper.NullScopeFactory(),
            NullLogger<MessageEditDebouncer>.Instance
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

    // A test can seed several games in one call, so the creator's id can't be derived from
    // wall-clock time — under load two calls a few instructions apart can read the same
    // DateTimeOffset.UtcNow. A monotonic counter is unique regardless of timing or the
    // parallel execution TUnit runs tests under.
    private static long _creatorIdSequence = 9_000_000;

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
            TelegramUserId = new TelegramUserId(Interlocked.Increment(ref _creatorIdSequence)),
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
