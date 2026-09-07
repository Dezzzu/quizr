using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Data;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Game = Quizr.Domain.Entities.Game;

namespace Quizr.App.Tests;

// The interceptor is the whole reason a stale feed can't happen quietly, so these tests are
// about who gets bumped rather than about how. Each one seeds its own team, game and people —
// PostgresFixture is shared and TUnit runs a class's tests in parallel, so every chat id,
// Telegram user id and calendar token here is unique to its own test.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class CalendarVersionInterceptorTests
{
    private readonly PostgresFixture _fixture;

    public CalendarVersionInterceptorTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task JoiningAGameThroughTheRealServiceBumpsTheJoiner()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7101, ct);
        var game = await SeedGameAsync(db, team, ct);
        var joiner = await SeedSubscriberAsync(db, telegramUserId: 7101, token: "tok-7101", ct);
        var before = await VersionAsync(db, joiner.Id, ct);
        var signups = new SignupService(
            db,
            new TeamGuard(db, TelegramBotClientTestHelper.Create()),
            new FakeTimeProvider()
        );

        await signups.JoinAsync(game, joiner.Id, ct);

        (await VersionAsync(db, joiner.Id, ct)).Should().Be(before + 1);
    }

    // The signup being inserted isn't in the database yet, so the roster query can't see it.
    // The joiner is picked up from the change tracker instead, which is the case this pins.
    [Test]
    public async Task ANewSignupBumpsBothTheJoinerAndEveryoneAlreadyOnTheRoster()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7102, ct);
        var game = await SeedGameAsync(db, team, ct);
        var sittingThere = await SeedSubscriberAsync(db, telegramUserId: 7102, token: "tok-7102", ct);
        var joiner = await SeedSubscriberAsync(db, telegramUserId: 7103, token: "tok-7103", ct);
        await SeedSignupAsync(db, game, sittingThere, ct);
        var sittingThereBefore = await VersionAsync(db, sittingThere.Id, ct);
        var joinerBefore = await VersionAsync(db, joiner.Id, ct);

        await SeedSignupAsync(db, game, joiner, ct);

        (await VersionAsync(db, sittingThere.Id, ct)).Should().Be(sittingThereBefore + 1);
        (await VersionAsync(db, joiner.Id, ct)).Should().Be(joinerBefore + 1);
    }

    // Invariant 3 — dropping cancels the signup, which moves everyone below across the split.
    [Test]
    public async Task DroppingOutBumpsTheRosterThatWasLeft()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7104, ct);
        var game = await SeedGameAsync(db, team, ct);
        var leaving = await SeedSubscriberAsync(db, telegramUserId: 7104, token: "tok-7104", ct);
        var staying = await SeedSubscriberAsync(db, telegramUserId: 7105, token: "tok-7105", ct);
        var signup = await SeedSignupAsync(db, game, leaving, ct);
        await SeedSignupAsync(db, game, staying, ct);
        var before = await VersionAsync(db, staying.Id, ct);

        signup.CancelledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        (await VersionAsync(db, staying.Id, ct)).Should().Be(before + 1);
    }

    // A guest takes a seat and shows up in their inviter's own event description, so the
    // inviter is bumped even though the signup is not theirs.
    [Test]
    public async Task BringingAGuestBumpsTheMemberWhoBroughtThem()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7106, ct);
        var game = await SeedGameAsync(db, team, ct);
        var inviter = await SeedSubscriberAsync(db, telegramUserId: 7106, token: "tok-7106", ct);
        var before = await VersionAsync(db, inviter.Id, ct);

        db.Signups.Add(
            new Signup
            {
                GameId = game.Id,
                InvitedByPlayerId = inviter.Id,
                CreatedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);

        (await VersionAsync(db, inviter.Id, ct)).Should().Be(before + 1);
    }

    [Test]
    public async Task EditingAFeedVisibleFieldRevisesTheGameAndBumpsItsRoster()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        await using var db = _fixture.CreateContext(clock);
        var team = await SeedTeamAsync(db, chatId: 7107, ct);
        var game = await SeedGameAsync(db, team, ct);
        var player = await SeedSubscriberAsync(db, telegramUserId: 7107, token: "tok-7107", ct);
        await SeedSignupAsync(db, game, player, ct);
        var before = await VersionAsync(db, player.Id, ct);

        game.Venue = "Somewhere else";
        await db.SaveChangesAsync(ct);

        var revised = await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct);
        revised.Revision.Should().Be(1);
        revised.RevisedAt.Should().Be(clock.GetUtcNow());
        (await VersionAsync(db, player.Id, ct)).Should().Be(before + 1);
    }

    // The counterpart, and the one that stops SEQUENCE from being meaningless: a nudge writes
    // to the game every time anyone taps it, and none of it reaches a calendar.
    [Test]
    public async Task NudgingAGameRevisesNothingAndBumpsNobody()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7108, ct);
        var game = await SeedGameAsync(db, team, ct);
        var player = await SeedSubscriberAsync(db, telegramUserId: 7108, token: "tok-7108", ct);
        await SeedSignupAsync(db, game, player, ct);
        var before = await VersionAsync(db, player.Id, ct);

        game.LastNudgedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var unrevised = await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct);
        unrevised.Revision.Should().Be(0);
        (await VersionAsync(db, player.Id, ct)).Should().Be(before);
    }

    [Test]
    public async Task ANewGameIsUnrevisedAndStampedWithTheMomentItWasCreated()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7109, ct);

        var game = await SeedGameAsync(db, team, ct);

        var stored = await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct);
        stored.Revision.Should().Be(0);
        stored.RevisedAt.Should().Be(stored.CreatedAt);
    }

    [Test]
    public async Task FinishingAGameBumpsItsRoster()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7110, ct);
        var game = await SeedGameAsync(db, team, ct);
        var player = await SeedSubscriberAsync(db, telegramUserId: 7110, token: "tok-7110", ct);
        await SeedSignupAsync(db, game, player, ct);
        var before = await VersionAsync(db, player.Id, ct);

        game.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        (await VersionAsync(db, player.Id, ct)).Should().Be(before + 1);
    }

    // Invariant 11 — a captain correcting who played rewrites what the feed says about a game
    // that has already happened.
    [Test]
    public async Task CorrectingWhoPlayedBumpsTheRoster()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7111, ct);
        var game = await SeedGameAsync(db, team, ct);
        var player = await SeedSubscriberAsync(db, telegramUserId: 7111, token: "tok-7111", ct);
        await SeedSignupAsync(db, game, player, ct);
        var participation = new Participation
        {
            GameId = game.Id,
            PlayerId = player.Id,
            Kind = ParticipationKind.Member,
            Played = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Participations.Add(participation);
        await db.SaveChangesAsync(ct);
        var before = await VersionAsync(db, player.Id, ct);

        participation.Played = false;
        await db.SaveChangesAsync(ct);

        (await VersionAsync(db, player.Id, ct)).Should().Be(before + 1);
    }

    // The team's zone is the clock the venue-time line is read on, so moving it rewrites every
    // member's feed — including anyone not currently signed up to anything.
    [Test]
    public async Task ChangingATeamsTimeZoneBumpsEveryMember()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7112, ct);
        var member = await SeedSubscriberAsync(db, telegramUserId: 7112, token: "tok-7112", ct);
        await SeedMembershipAsync(db, team, member, ct);
        var before = await VersionAsync(db, member.Id, ct);

        team.TimeZoneId = "Europe/Madrid";
        await db.SaveChangesAsync(ct);

        (await VersionAsync(db, member.Id, ct)).Should().Be(before + 1);
    }

    [Test]
    public async Task ChangingYourOwnLanguageBumpsOnlyYou()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7113, ct);
        var game = await SeedGameAsync(db, team, ct);
        var switcher = await SeedSubscriberAsync(db, telegramUserId: 7113, token: "tok-7113", ct);
        var teammate = await SeedSubscriberAsync(db, telegramUserId: 7114, token: "tok-7114", ct);
        await SeedSignupAsync(db, game, switcher, ct);
        await SeedSignupAsync(db, game, teammate, ct);
        var switcherBefore = await VersionAsync(db, switcher.Id, ct);
        var teammateBefore = await VersionAsync(db, teammate.Id, ct);

        switcher.Locale = "de";
        await db.SaveChangesAsync(ct);

        (await VersionAsync(db, switcher.Id, ct)).Should().Be(switcherBefore + 1);
        (await VersionAsync(db, teammate.Id, ct)).Should().Be(teammateBefore);
    }

    // The filter that keeps this free for a team where nobody subscribes: a version nobody can
    // read is a version not worth writing.
    [Test]
    public async Task APlayerWithNoTokenIsNeverBumped()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7115, ct);
        var game = await SeedGameAsync(db, team, ct);
        var unsubscribed = await SeedPlayerAsync(db, telegramUserId: 7115, ct);
        await SeedSignupAsync(db, game, unsubscribed, ct);

        game.Venue = "Somewhere else";
        await db.SaveChangesAsync(ct);

        (await VersionAsync(db, unsubscribed.Id, ct)).Should().Be(0);
    }

    [Test]
    public async Task SomebodyElsesGameLeavesASubscriberAlone()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7116, ct);
        var mine = await SeedGameAsync(db, team, ct);
        var theirs = await SeedGameAsync(db, team, ct);
        var me = await SeedSubscriberAsync(db, telegramUserId: 7116, token: "tok-7116", ct);
        var them = await SeedSubscriberAsync(db, telegramUserId: 7117, token: "tok-7117", ct);
        await SeedSignupAsync(db, mine, me, ct);
        await SeedSignupAsync(db, theirs, them, ct);
        var before = await VersionAsync(db, me.Id, ct);

        theirs.Venue = "Somewhere else";
        await db.SaveChangesAsync(ct);

        (await VersionAsync(db, me.Id, ct)).Should().Be(before);
    }

    private static async Task<long> VersionAsync(QuizrDb db, PlayerId playerId, CancellationToken ct) =>
        await db.Players.AsNoTracking().Where(p => p.Id == playerId).Select(p => p.CalendarVersion).SingleAsync(ct);

    private static async Task<Team> SeedTeamAsync(QuizrDb db, long chatId, CancellationToken ct)
    {
        var team = new Team
        {
            ChatId = new TelegramChatId(chatId),
            Name = "Team",
            TimeZoneId = "Europe/Berlin",
            Locale = "en",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return team;
    }

    private static async Task<Game> SeedGameAsync(QuizrDb db, Team team, CancellationToken ct)
    {
        var game = new Game
        {
            TeamId = team.Id,
            Title = "Quiz",
            Venue = "The Pub",
            StartsAt = DateTimeOffset.UtcNow.AddDays(3),
            Capacity = 6,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = new PlayerId(1),
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);
        return game;
    }

    private static Task<Player> SeedSubscriberAsync(
        QuizrDb db,
        long telegramUserId,
        string token,
        CancellationToken ct
    ) => SeedPlayerAsync(db, telegramUserId, ct, token);

    private static async Task<Player> SeedPlayerAsync(
        QuizrDb db,
        long telegramUserId,
        CancellationToken ct,
        string? token = null
    )
    {
        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = "Player",
            CalendarToken = token,
            CalendarTokenIssuedAt = token is null ? null : DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);
        return player;
    }

    private static async Task<Signup> SeedSignupAsync(QuizrDb db, Game game, Player player, CancellationToken ct)
    {
        var signup = new Signup
        {
            GameId = game.Id,
            PlayerId = player.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Signups.Add(signup);
        await db.SaveChangesAsync(ct);
        return signup;
    }

    private static async Task SeedMembershipAsync(QuizrDb db, Team team, Player player, CancellationToken ct)
    {
        db.Memberships.Add(
            new Membership
            {
                TeamId = team.Id,
                PlayerId = player.Id,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);
    }
}
