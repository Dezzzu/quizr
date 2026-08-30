using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.App.Telegram;
using Quizr.Domain;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Quizr.App.Tests;

[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class TeamBootstrapServiceTests
{
    private static readonly User BotUser = new() { Id = 999, FirstName = "Quizr" };

    private readonly PostgresFixture _fixture;

    public TeamBootstrapServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task AddingTheBotAsAPlainMemberCreatesATeamWithConfirmedDefaultsAndWarnsAboutAdminRights()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        var (service, db, bot) = CreateService();
        var chatId = new TelegramChatId(1001);

        await service.HandleMyChatMemberAsync(
            AddedTo(chatId.Value, "Beer Quiz Crew", new ChatMemberMember { User = BotUser }, languageCode: "ru"),
            ct
        );

        var team = await db.Teams.SingleAsync(t => t.ChatId == chatId, ct);
        team.Name.Should().Be("Beer Quiz Crew");
        team.Locale.Should().Be("ru");
        team.TimeZoneId.Should().BeNull();
        team.EveningBeforeAt.Should().Be(new TimeOnly(20, 0));
        team.MorningOfAt.Should().Be(new TimeOnly(9, 0));
        team.BeforeStartLead.Should().Be(TimeSpan.FromHours(2));
        team.DeactivatedAt.Should().BeNull();

        bot.SentTexts().Should().HaveCount(2);
    }

    [Test]
    public async Task AddingTheBotAsAnAdminSendsOnlyTheWelcomeMessage()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        var (service, db, bot) = CreateService();
        var chatId = new TelegramChatId(1002);

        await service.HandleMyChatMemberAsync(
            AddedTo(chatId.Value, "Admin Team", new ChatMemberAdministrator { User = BotUser }),
            ct
        );

        (await db.Teams.SingleAsync(t => t.ChatId == chatId, ct)).Should().NotBeNull();
        bot.SentTexts().Should().HaveCount(1);
    }

    [Test]
    public async Task RemovingTheBotDeactivatesItsTeamWithoutDeletingIt()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        var (service, db, _) = CreateService();
        var chatId = new TelegramChatId(1003);

        await service.HandleMyChatMemberAsync(
            AddedTo(chatId.Value, "Team", new ChatMemberMember { User = BotUser }),
            ct
        );
        await service.HandleMyChatMemberAsync(RemovedFrom(chatId.Value), ct);

        var team = await db.Teams.SingleAsync(t => t.ChatId == chatId, ct);
        team.DeactivatedAt.Should().NotBeNull();
    }

    [Test]
    public async Task ReAddingTheBotClearsDeactivatedAtWithoutTouchingOtherFields()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        var (service, db, _) = CreateService();
        var chatId = new TelegramChatId(1004);

        await service.HandleMyChatMemberAsync(
            AddedTo(chatId.Value, "Persistent Team", new ChatMemberMember { User = BotUser }),
            ct
        );
        await service.HandleMyChatMemberAsync(RemovedFrom(chatId.Value), ct);
        await service.HandleMyChatMemberAsync(
            AddedTo(chatId.Value, "ignored on re-add", new ChatMemberMember { User = BotUser }),
            ct
        );

        var team = await db.Teams.SingleAsync(t => t.ChatId == chatId, ct);
        team.DeactivatedAt.Should().BeNull();
        team.Name.Should().Be("Persistent Team");
    }

    private (TeamBootstrapService Service, QuizrDb Db, ITelegramBotClient Bot) CreateService()
    {
        var db = _fixture.CreateContext();
        var bot = TelegramBotClientTestHelper.Create();
        var clock = new FakeTimeProvider();
        var sender = new MessageSender(bot, new MessageEditDebouncer(bot, clock));

        return (new TeamBootstrapService(db, sender, new Strings(), clock), db, bot);
    }

    private static ChatMemberUpdated AddedTo(
        long chatId,
        string title,
        ChatMember newStatus,
        string? languageCode = null
    ) =>
        new()
        {
            Chat = new Chat { Id = chatId, Title = title },
            From = new User
            {
                Id = 1,
                FirstName = "Adder",
                LanguageCode = languageCode,
            },
            Date = DateTime.UtcNow,
            OldChatMember = new ChatMemberLeft { User = BotUser },
            NewChatMember = newStatus,
        };

    private static ChatMemberUpdated RemovedFrom(long chatId) =>
        new()
        {
            Chat = new Chat { Id = chatId },
            From = new User { Id = 1, FirstName = "Remover" },
            Date = DateTime.UtcNow,
            OldChatMember = new ChatMemberMember { User = BotUser },
            NewChatMember = new ChatMemberLeft { User = BotUser },
        };
}
