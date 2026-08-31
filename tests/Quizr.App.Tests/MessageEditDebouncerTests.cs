using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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

[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class MessageEditDebouncerTests
{
    private static readonly TimeSpan WindowPastDebounce = TimeSpan.FromSeconds(2);

    private readonly PostgresFixture _fixture;

    public MessageEditDebouncerTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task CoalescesRapidEditsToTheSameMessageIntoOneCallWithTheLatestText()
    {
        var timeProvider = new FakeTimeProvider();
        var bot = TelegramBotClientTestHelper.Create();
        var debouncer = new MessageEditDebouncer(
            bot,
            timeProvider,
            TelegramBotClientTestHelper.NullScopeFactory(),
            NullLogger<MessageEditDebouncer>.Instance
        );
        var chatId = new TelegramChatId(1);
        var messageId = new TelegramMessageId(100);
        var ct = TestContext.Current!.Execution.CancellationToken;

        await debouncer.ScheduleAsync(chatId, messageId, "first", null, ct);
        await debouncer.ScheduleAsync(chatId, messageId, "second", null, ct);
        await debouncer.ScheduleAsync(chatId, messageId, "third", null, ct);

        timeProvider.Advance(WindowPastDebounce);
        await WaitUntilAsync(() => bot.EditedTexts().Count > 0, ct);

        bot.EditedTexts().Should().Equal("third");
    }

    [Test]
    public async Task DoesNotCoalesceEditsToDifferentMessages()
    {
        var timeProvider = new FakeTimeProvider();
        var bot = TelegramBotClientTestHelper.Create();
        var debouncer = new MessageEditDebouncer(
            bot,
            timeProvider,
            TelegramBotClientTestHelper.NullScopeFactory(),
            NullLogger<MessageEditDebouncer>.Instance
        );
        var chatId = new TelegramChatId(1);
        var ct = TestContext.Current!.Execution.CancellationToken;

        await debouncer.ScheduleAsync(chatId, new TelegramMessageId(100), "a", null, ct);
        await debouncer.ScheduleAsync(chatId, new TelegramMessageId(200), "b", null, ct);

        timeProvider.Advance(WindowPastDebounce);
        await WaitUntilAsync(() => bot.EditedTexts().Count >= 2, ct);

        bot.EditedTexts().Should().BeEquivalentTo(["a", "b"]);
    }

    // Telegram rejects a no-op edit as a 400 rather than a silent success (BoardService hits
    // this on every scheduler tick that finds nothing changed) — the flush runs detached from
    // any caller, so this failure has to be swallowed here or it vanishes with no trace and,
    // worse, could leave the debouncer's internal state stuck for this message.
    [Test]
    public async Task ANoOpEditDoesNotPreventALaterEditToTheSameMessage()
    {
        var timeProvider = new FakeTimeProvider();
        var bot = TelegramBotClientTestHelper.Create();
        bot.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: message is not modified", 400));
        var debouncer = new MessageEditDebouncer(
            bot,
            timeProvider,
            TelegramBotClientTestHelper.NullScopeFactory(),
            NullLogger<MessageEditDebouncer>.Instance
        );
        var chatId = new TelegramChatId(1);
        var messageId = new TelegramMessageId(100);
        var ct = TestContext.Current!.Execution.CancellationToken;

        await debouncer.ScheduleAsync(chatId, messageId, "same as before", null, ct);
        timeProvider.Advance(WindowPastDebounce);
        await WaitUntilAsync(() => bot.EditedTexts().Count >= 1, ct);

        bot.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Message { Id = 1 });
        await debouncer.ScheduleAsync(chatId, messageId, "a real change", null, ct);
        timeProvider.Advance(WindowPastDebounce);
        await WaitUntilAsync(() => bot.EditedTexts().Contains("a real change"), ct);

        bot.EditedTexts().Should().Contain("a real change");
    }

    // A deleted announcement is the one failure this class recovers from itself, rather than
    // just logging (CLAUDE.md: chat messages are generated views, so a missing one should come
    // back from the database) — exercised with a real DI scope, since RepostAnnouncementAsync
    // resolves its own QuizrDb and AnnouncementService independently of whatever scope
    // scheduled the original edit.
    [Test]
    public async Task ADeletedAnnouncementIsRepostedAndTheGameUpdatedToPointAtIt()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var seedDb = _fixture.CreateContext();
        var team = new Team
        {
            ChatId = new TelegramChatId(9401),
            Name = "Test team",
            Locale = "en",
            TimeZoneId = "Europe/Berlin",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        seedDb.Teams.Add(team);
        var creator = new Player
        {
            TelegramUserId = new TelegramUserId(9401),
            DisplayName = "Creator",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        seedDb.Players.Add(creator);
        await seedDb.SaveChangesAsync(ct);
        var game = new Game
        {
            TeamId = team.Id,
            Title = "Quiz Night",
            Venue = "The Pub",
            StartsAt = DateTimeOffset.UtcNow.AddDays(1),
            Capacity = 5,
            AnnouncementMessageId = new TelegramMessageId(100),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = creator.Id,
        };
        seedDb.Games.Add(game);
        await seedDb.SaveChangesAsync(ct);

        var timeProvider = new FakeTimeProvider();
        var bot = TelegramBotClientTestHelper.Create();
        bot.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: message to edit not found", 400));
        var debouncer = new MessageEditDebouncer(
            bot,
            timeProvider,
            RealScopeFactory(bot),
            NullLogger<MessageEditDebouncer>.Instance
        );

        await debouncer.ScheduleAsync(team.ChatId, new TelegramMessageId(100), "Quiz Night", null, ct);
        timeProvider.Advance(WindowPastDebounce);
        await WaitUntilAsync(() => bot.SentTexts(team.ChatId.Value).Count > 0, ct);

        var refreshed = await seedDb.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct);
        refreshed.AnnouncementMessageId.Should().NotBe(new TelegramMessageId(100));
        bot.SentTexts(team.ChatId.Value)
            .Should()
            .ContainSingle(t => t.Contains("Quiz Night", StringComparison.Ordinal));
    }

    private IServiceScopeFactory RealScopeFactory(ITelegramBotClient bot)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMessageSender>(new MessageSender(bot, Substitute.For<IMessageEditDebouncer>()));
        services.AddSingleton<IStrings>(new Strings());
        services.AddScoped(_ => _fixture.CreateContext());
        services.AddScoped<AnnouncementService>();

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
        }
    }
}
