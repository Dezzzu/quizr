using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Calendar;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.App.Telegram;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Quizr.App.Tests;

// /mycalendar and the two destructive buttons under it. The delivery rules are most of what
// there is to get wrong here: the link is a credential, so where it is allowed to appear
// depends on where it was asked for and whether a DM would land at all.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class UpdateRouterCalendarTests
{
    [Test]
    public async Task InAPrivateChatTheLinkIsSentStraightBack()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var player = await SeedPlayerAsync(db, telegramUserId: 8601, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(PrivateMessage(8601, "/mycalendar"), ct);

        var token = (await ReloadAsync(db, player, ct)).CalendarToken;
        token.Should().NotBeNull();
        bot.SentTexts(8601).Single().Should().Contain($"https://quizr.test/cal/{token}.ics");
    }

    // A group is the wrong place for a credential, so the preferred delivery is a DM and the
    // chat only learns that one was sent.
    [Test]
    public async Task InAGroupTheLinkGoesToTheDmAndTheChatOnlyGetsAPointer()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        // The group's chat id and the member's user id are deliberately different, so "went
        // to the DM" and "went to the group" are distinguishable assertions.
        var team = await SeedTeamAsync(db, chatId: 8602, ct);
        var player = await SeedMemberAsync(db, team, telegramUserId: 8612, dmEnabled: true, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(GroupMessage(8602, 8612, "/mycalendar"), ct);

        var token = (await ReloadAsync(db, player, ct)).CalendarToken;
        bot.SentTexts(8612).Single().Should().Contain($"https://quizr.test/cal/{token}.ics");
        bot.SentTexts(8602).Should().BeEmpty();
        bot.EphemeralTexts(8602).Single().Should().Contain("private chat");
        bot.EphemeralTexts(8602).Single().Should().NotContain(token!);
    }

    // A bot cannot message anyone who hasn't started it, and this is the feature most likely
    // to be tried by somebody who never has. An ephemeral message is visible to them alone, so
    // it beats a dead end.
    [Test]
    public async Task WithNoDmPossibleTheLinkArrivesEphemerallyInTheGroupInstead()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8603, ct);
        var player = await SeedMemberAsync(db, team, telegramUserId: 8613, dmEnabled: false, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(GroupMessage(8603, 8613, "/mycalendar"), ct);

        var token = (await ReloadAsync(db, player, ct)).CalendarToken;
        bot.SentTexts(8613).Should().BeEmpty();
        bot.SentTexts(8603).Should().BeEmpty();
        bot.EphemeralTexts(8603).Single().Should().Contain($"https://quizr.test/cal/{token}.ics");
    }

    [Test]
    public async Task AskingTwiceGivesTheSameLinkRatherThanANewOne()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var player = await SeedPlayerAsync(db, telegramUserId: 8604, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(PrivateMessage(8604, "/mycalendar"), ct);
        var first = (await ReloadAsync(db, player, ct)).CalendarToken;
        await router.RouteAsync(PrivateMessage(8604, "/mycalendar"), ct);

        (await ReloadAsync(db, player, ct)).CalendarToken.Should().Be(first);
    }

    // No public URL means no endpoint mapped, so issuing a token would mint a credential for
    // something that answers nothing.
    [Test]
    public async Task WithNoPublicUrlConfiguredNoTokenIsIssuedAtAll()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var player = await SeedPlayerAsync(db, telegramUserId: 8605, ct);
        var (router, bot) = CreateRouter(db, publicUrl: null);

        await router.RouteAsync(PrivateMessage(8605, "/mycalendar"), ct);

        (await ReloadAsync(db, player, ct)).CalendarToken.Should().BeNull();
        bot.SentTexts(8605).Single().Should().Contain("aren't set up");
    }

    // Replacing a link silently breaks every device already subscribed to the old one, so it
    // asks first — the same shape as dropping out of a game or declining one.
    [Test]
    public async Task ReplacingALinkAsksBeforeItDoesAnything()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var player = await SeedPlayerAsync(db, telegramUserId: 8606, ct);
        var (router, bot) = CreateRouter(db);
        await router.RouteAsync(PrivateMessage(8606, "/mycalendar"), ct);
        var before = (await ReloadAsync(db, player, ct)).CalendarToken;

        await router.RouteAsync(PrivateCallback(8606, CallbackData.RotateCalendar), ct);

        (await ReloadAsync(db, player, ct)).CalendarToken.Should().Be(before);
        bot.EditedTexts(8606).Single().Should().Contain("Replace your calendar link?");
    }

    [Test]
    public async Task ConfirmingAReplacementIssuesANewLinkAndShowsIt()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var player = await SeedPlayerAsync(db, telegramUserId: 8607, ct);
        var (router, bot) = CreateRouter(db);
        await router.RouteAsync(PrivateMessage(8607, "/mycalendar"), ct);
        var before = (await ReloadAsync(db, player, ct)).CalendarToken;

        await router.RouteAsync(PrivateCallback(8607, CallbackData.RotateCalendar), ct);
        await router.RouteAsync(PrivateCallback(8607, CallbackData.ConfirmRotateCalendar), ct);

        var after = (await ReloadAsync(db, player, ct)).CalendarToken;
        after.Should().NotBeNull().And.NotBe(before);
        bot.EditedTexts(8607)[^1].Should().Contain($"https://quizr.test/cal/{after}.ics");
    }

    // Cancelling is the only way out of a confirm prompt that doesn't destroy anything, so it
    // has to leave the token exactly where it was.
    [Test]
    public async Task CancellingAReplacementLeavesTheLinkAlone()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var player = await SeedPlayerAsync(db, telegramUserId: 8608, ct);
        var (router, bot) = CreateRouter(db);
        await router.RouteAsync(PrivateMessage(8608, "/mycalendar"), ct);
        var before = (await ReloadAsync(db, player, ct)).CalendarToken;

        await router.RouteAsync(PrivateCallback(8608, CallbackData.RotateCalendar), ct);
        await router.RouteAsync(PrivateCallback(8608, CallbackData.ShowCalendar), ct);

        (await ReloadAsync(db, player, ct)).CalendarToken.Should().Be(before);
        bot.EditedTexts(8608)[^1].Should().Contain($"https://quizr.test/cal/{before}.ics");
    }

    [Test]
    public async Task TurningOffAsksFirstAndThenClearsTheToken()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var player = await SeedPlayerAsync(db, telegramUserId: 8609, ct);
        var (router, bot) = CreateRouter(db);
        await router.RouteAsync(PrivateMessage(8609, "/mycalendar"), ct);

        await router.RouteAsync(PrivateCallback(8609, CallbackData.RevokeCalendar), ct);
        (await ReloadAsync(db, player, ct)).CalendarToken.Should().NotBeNull();

        await router.RouteAsync(PrivateCallback(8609, CallbackData.ConfirmRevokeCalendar), ct);

        var cleared = await ReloadAsync(db, player, ct);
        cleared.CalendarToken.Should().BeNull();
        cleared.CalendarTokenIssuedAt.Should().BeNull();
        bot.EditedTexts(8609)[^1].Should().Contain("turned off");
    }

    [Test]
    public async Task AskingAgainAfterTurningOffIssuesAFreshLink()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var player = await SeedPlayerAsync(db, telegramUserId: 8610, ct);
        var (router, _) = CreateRouter(db);
        await router.RouteAsync(PrivateMessage(8610, "/mycalendar"), ct);
        var before = (await ReloadAsync(db, player, ct)).CalendarToken;
        await router.RouteAsync(PrivateCallback(8610, CallbackData.ConfirmRevokeCalendar), ct);

        await router.RouteAsync(PrivateMessage(8610, "/mycalendar"), ct);

        (await ReloadAsync(db, player, ct)).CalendarToken.Should().NotBeNull().And.NotBe(before);
    }

    // The four things the onboarding text has to say, because each one is otherwise a question
    // somebody asks — and the Google one is a question they ask forever.
    [Test]
    public async Task TheOnboardingTextWarnsAboutGoogleMobileRefreshLagAndSharing()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        await SeedPlayerAsync(db, telegramUserId: 8611, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(PrivateMessage(8611, "/mycalendar"), ct);

        var text = bot.SentTexts(8611).Single();
        text.Should().Contain("desktop site").And.Contain("mobile app can't");
        text.Should().Contain("Apple Calendar");
        text.Should().Contain("8–24 hours");
        text.Should().Contain("don't post it anywhere");
    }

    [Test]
    public async Task TheCommandIsOfferedToEveryoneRatherThanOnlyCaptains()
    {
        CommandMenu.EveryoneCommands.Should().Contain(c => c.Command == "mycalendar");
        CommandMenu.CaptainOnlyCommands.Should().NotContain(c => c.Command == "mycalendar");
    }

    private readonly PostgresFixture _fixture;

    public UpdateRouterCalendarTests(PostgresFixture fixture) => _fixture = fixture;

    private static (UpdateRouter Router, ITelegramBotClient Bot) CreateRouter(
        QuizrDb db,
        string? publicUrl = "https://quizr.test"
    )
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
        var teamGuard = new TeamGuard(db, bot);

        var router = new UpdateRouter(
            db,
            sender,
            bot,
            strings,
            new TeamBootstrapService(db, sender, strings, clock),
            new PlayerBootstrapService(db, clock),
            new TeamService(db, teamGuard, clock),
            new DialogService(db, teamGuard, clock),
            new SignupService(db, teamGuard, clock),
            new FranchiseService(db, teamGuard, clock),
            new GameService(db, teamGuard, clock),
            new ParticipationService(db, teamGuard, clock),
            new AnnouncementService(db, sender, strings),
            new BoardService(db, sender, bot, strings, NullLogger<BoardService>.Instance),
            new MyScheduleService(db),
            new CalendarSubscriptionService(db, clock),
            new CalendarUrls(publicUrl),
            clock,
            NullLogger<UpdateRouter>.Instance
        );

        return (router, bot);
    }

    private static Task<Player> ReloadAsync(QuizrDb db, Player player, CancellationToken ct) =>
        db.Players.AsNoTracking().SingleAsync(p => p.Id == player.Id, ct);

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

    private static async Task<Player> SeedPlayerAsync(
        QuizrDb db,
        long telegramUserId,
        CancellationToken ct,
        bool dmEnabled = true
    )
    {
        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = $"Player {telegramUserId}",
            DmEnabled = dmEnabled,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);
        return player;
    }

    private static async Task<Player> SeedMemberAsync(
        QuizrDb db,
        Team team,
        long telegramUserId,
        bool dmEnabled,
        CancellationToken ct
    )
    {
        var player = await SeedPlayerAsync(db, telegramUserId, ct, dmEnabled);
        db.Memberships.Add(
            new Membership
            {
                TeamId = team.Id,
                PlayerId = player.Id,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);
        return player;
    }

    private static Update PrivateMessage(long telegramUserId, string text) =>
        new()
        {
            Id = 1,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = telegramUserId, Type = ChatType.Private },
                From = new User { Id = telegramUserId, FirstName = "Test" },
                Text = text,
                Date = DateTime.UtcNow,
            },
        };

    private static Update GroupMessage(long chatId, long telegramUserId, string text) =>
        new()
        {
            Id = 1,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = chatId, Type = ChatType.Supergroup },
                From = new User { Id = telegramUserId, FirstName = "Test" },
                Text = text,
                Date = DateTime.UtcNow,
            },
        };

    private static Update PrivateCallback(long telegramUserId, char verb) =>
        new()
        {
            Id = 1,
            CallbackQuery = new CallbackQuery
            {
                Id = "cq1",
                From = new User { Id = telegramUserId, FirstName = "Test" },
                Data = CallbackData.Format(verb, 0L),
                Message = new Message
                {
                    Id = 1,
                    Chat = new Chat { Id = telegramUserId, Type = ChatType.Private },
                    Date = DateTime.UtcNow,
                },
            },
        };
}
