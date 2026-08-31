using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Data;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Tests;

[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class GameServiceTests
{
    private readonly PostgresFixture _fixture;

    public GameServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public void NextCandidateDatesOnlyReturnsDaysTheScheduleRuns()
    {
        var schedule = new Dictionary<DayOfWeek, TimeOnly>
        {
            [DayOfWeek.Monday] = new TimeOnly(19, 0),
            [DayOfWeek.Thursday] = new TimeOnly(19, 0),
        };
        // 2026-08-31 is a Monday.
        var from = new DateOnly(2026, 8, 31);

        var dates = GameService.NextCandidateDates(from, schedule, 4);

        dates
            .Should()
            .Equal(
                new DateOnly(2026, 8, 31),
                new DateOnly(2026, 9, 3),
                new DateOnly(2026, 9, 7),
                new DateOnly(2026, 9, 10)
            );
    }

    // Regression: an empty schedule used to loop until DateOnly overflowed, since no day of
    // the week could ever match.
    [Test]
    public void NextCandidateDatesReturnsNoneForAnEmptySchedule()
    {
        var dates = GameService.NextCandidateDates(new DateOnly(2026, 8, 31), new Dictionary<DayOfWeek, TimeOnly>(), 8);

        dates.Should().BeEmpty();
    }

    [Test]
    public async Task PreviewFranchiseTitleAsyncNumbersSequentially()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (team, franchise, captain) = await SeedFranchiseAsync(db, chatId: 6101, ct);
        var service = new GameService(db, captain.Guard, new FakeTimeProvider());

        (await service.PreviewFranchiseTitleAsync(franchise, ct)).Should().Be($"{franchise.Name} #1");

        await service.CreateFromFranchiseAsync(
            team,
            captain.Actor,
            franchise,
            $"{franchise.Name} #1",
            new DateOnly(2026, 9, 7),
            franchise.Schedule[DayOfWeek.Monday],
            franchise.DefaultVenue!,
            franchise.DefaultCapacity!.Value,
            franchise.DefaultPrice,
            null,
            [],
            ct
        );

        (await service.PreviewFranchiseTitleAsync(franchise, ct)).Should().Be($"{franchise.Name} #2");
    }

    [Test]
    public async Task CreateFromFranchiseAsyncComputesStartsAtFromTheScheduleTime()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (team, franchise, captain) = await SeedFranchiseAsync(db, chatId: 6102, ct);
        var service = new GameService(db, captain.Guard, new FakeTimeProvider());

        // 2026-09-07 is a Monday — franchise plays Mondays at 19:00 Europe/Berlin (CEST, +2).
        var game = (
            await service.CreateFromFranchiseAsync(
                team,
                captain.Actor,
                franchise,
                "Квиз, плиз! #1",
                new DateOnly(2026, 9, 7),
                franchise.Schedule[DayOfWeek.Monday],
                franchise.DefaultVenue!,
                franchise.DefaultCapacity!.Value,
                franchise.DefaultPrice,
                null,
                [],
                ct
            )
        ).Value;

        game.FranchiseId.Should().Be(franchise.Id);
        game.StartsAt.Should().Be(new DateTimeOffset(2026, 9, 7, 17, 0, 0, TimeSpan.Zero));
    }

    // Regression: CreateFromFranchiseAsync used to look the time up from the franchise's own
    // schedule by day of week, which threw for any custom date/time a captain typed in that
    // the schedule doesn't cover (invariant: an absent day is one the franchise doesn't run).
    [Test]
    public async Task CreateFromFranchiseAsyncAcceptsATimeTheScheduleDoesNotCover()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (team, franchise, captain) = await SeedFranchiseAsync(db, chatId: 6111, ct);
        var service = new GameService(db, captain.Guard, new FakeTimeProvider());

        // 2026-09-08 is a Tuesday — the franchise only plays Mondays.
        var game = (
            await service.CreateFromFranchiseAsync(
                team,
                captain.Actor,
                franchise,
                "Special Tuesday edition",
                new DateOnly(2026, 9, 8),
                new TimeOnly(20, 30),
                franchise.DefaultVenue!,
                franchise.DefaultCapacity!.Value,
                franchise.DefaultPrice,
                null,
                [],
                ct
            )
        ).Value;

        game.StartsAt.Should().Be(new DateTimeOffset(2026, 9, 8, 18, 30, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task CreateOneOffAsyncLeavesFranchiseIdNull()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6103, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: 6103, ct);
        var service = new GameService(db, captain.Guard, new FakeTimeProvider());

        var game = (
            await service.CreateOneOffAsync(
                team,
                captain.Actor,
                "One-off quiz",
                "The Pub",
                new DateOnly(2026, 9, 12),
                new TimeOnly(19, 0),
                10,
                null,
                ct
            )
        ).Value;

        game.FranchiseId.Should().BeNull();
        game.Title.Should().Be("One-off quiz");
    }

    [Test]
    public async Task SetCapacityAsyncPromotesAReserveAndRecordsANotification()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6104, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: 6104, ct);
        var gameService = new GameService(db, captain.Guard, new FakeTimeProvider());
        var game = (
            await gameService.CreateOneOffAsync(
                team,
                captain.Actor,
                "Quiz",
                "The Pub",
                new DateOnly(2026, 9, 12),
                new TimeOnly(19, 0),
                1,
                null,
                ct
            )
        ).Value;

        var signups = new SignupService(db, captain.Guard, new FakeTimeProvider());
        var playing = await SeedPlayerAsync(db, telegramUserId: 61041, ct);
        var reserve = await SeedPlayerAsync(db, telegramUserId: 61042, ct);
        await signups.JoinAsync(game, playing.Id, ct);
        var reserveSignup = (await signups.JoinAsync(game, reserve.Id, ct)).Value;

        var promoted = (await gameService.SetCapacityAsync(game, team, captain.Actor, 2, ct)).Value;

        promoted.Select(s => s.Id).Should().ContainSingle(id => id == reserveSignup.Id);
        game.Capacity.Should().Be(2);
        (await db.Notifications.CountAsync(n => n.SignupId == reserveSignup.Id, ct)).Should().Be(1);
    }

    // Nudge's target list: CLAUDE.md/VISION.md says it pings people who signed up and are
    // late, not people who never signed up — so a member with no signup at all must never
    // appear, and neither should someone bumped to the reserve (invariant 2's derived split):
    // they aren't confirmed to play, so "late" doesn't apply to them either.
    [Test]
    public async Task LoadPlayingMembersAsyncIncludesOnlyThePlayingRoster()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6105, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: 6105, ct);
        var gameService = new GameService(db, captain.Guard, new FakeTimeProvider());
        var game = (
            await gameService.CreateOneOffAsync(
                team,
                captain.Actor,
                "Quiz",
                "The Pub",
                new DateOnly(2026, 9, 12),
                new TimeOnly(19, 0),
                1,
                null,
                ct
            )
        ).Value;

        var playing = await SeedMemberAsync(db, team.Id, telegramUserId: 61051, ct);
        var reserve = await SeedMemberAsync(db, team.Id, telegramUserId: 61052, ct);
        var neverSignedUp = await SeedMemberAsync(db, team.Id, telegramUserId: 61053, ct);
        var signups = new SignupService(db, captain.Guard, new FakeTimeProvider());
        await signups.JoinAsync(game, playing.Id, ct);
        await signups.JoinAsync(game, reserve.Id, ct);

        var playingMembers = await gameService.LoadPlayingMembersAsync(game, ct);

        playingMembers.Select(m => m.PlayerId).Should().Equal(playing.Id);
        playingMembers.Select(m => m.PlayerId).Should().NotContain(reserve.Id);
        playingMembers.Select(m => m.PlayerId).Should().NotContain(neverSignedUp.Id);
    }

    [Test]
    public async Task TryNudgeAsyncStampsLastNudgedAtOnFirstCallAndBlocksASecondWithinTheCooldown()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6106, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: 6106, ct);
        var clock = new FakeTimeProvider();
        var gameService = new GameService(db, captain.Guard, clock);
        var game = (
            await gameService.CreateOneOffAsync(
                team,
                captain.Actor,
                "Quiz",
                "The Pub",
                new DateOnly(2026, 9, 12),
                new TimeOnly(19, 0),
                10,
                null,
                ct
            )
        ).Value;

        var first = await gameService.TryNudgeAsync(game, ct);
        first.IsSuccess.Should().BeTrue();
        game.LastNudgedAt.Should().Be(clock.GetUtcNow());

        var second = await gameService.TryNudgeAsync(game, ct);
        second.IsSuccess.Should().BeFalse();
        second.Error.Should().BeOfType<BusinessError.NudgeOnCooldown>();

        clock.Advance(TimeSpan.FromMinutes(5));
        var third = await gameService.TryNudgeAsync(game, ct);
        third.IsSuccess.Should().BeTrue();
    }

    [Test]
    public async Task DeclineAsyncStampsDeclinedAtAndRecordsAnAuditEntry()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6107, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: 6107, ct);
        var clock = new FakeTimeProvider();
        var gameService = new GameService(db, captain.Guard, clock);
        var game = (
            await gameService.CreateOneOffAsync(
                team,
                captain.Actor,
                "Quiz",
                "The Pub",
                new DateOnly(2026, 9, 12),
                new TimeOnly(19, 0),
                10,
                null,
                ct
            )
        ).Value;

        await gameService.DeclineAsync(game, team, captain.Actor, ct);

        game.DeclinedAt.Should().Be(clock.GetUtcNow());
        var entry = await db.AuditEntries.SingleAsync(e => e.GameId == game.Id, ct);
        entry.Action.Should().Be(AuditActions.GameDeclined);
        entry.ActorPlayerId.Should().Be(captain.PlayerId);
        entry.TeamId.Should().Be(team.Id);
    }

    [Test]
    public async Task FinishAsyncMaterializesParticipationAndRecordsAnAuditEntry()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6108, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: 6108, ct);
        var clock = new FakeTimeProvider();
        var gameService = new GameService(db, captain.Guard, clock);
        var game = (
            await gameService.CreateOneOffAsync(
                team,
                captain.Actor,
                "Quiz",
                "The Pub",
                new DateOnly(2026, 9, 12),
                new TimeOnly(19, 0),
                1,
                null,
                ct
            )
        ).Value;

        var signups = new SignupService(db, captain.Guard, clock);
        var playing = await SeedPlayerAsync(db, telegramUserId: 61081, ct);
        var reserve = await SeedPlayerAsync(db, telegramUserId: 61082, ct);
        await signups.JoinAsync(game, playing.Id, ct);
        await signups.JoinAsync(game, reserve.Id, ct);

        // A captain finishing the game manually — the actor is set, unlike the scheduler's own
        // auto-finish call which passes null and so skips the captain check too.
        await gameService.FinishAsync(game, team, captain.Actor, ct);

        game.FinishedAt.Should().Be(clock.GetUtcNow());
        var participations = await db.Participations.Where(p => p.GameId == game.Id).ToListAsync(ct);
        participations.Should().HaveCount(2);
        participations.Single(p => p.PlayerId == playing.Id).Played.Should().BeTrue();
        participations.Single(p => p.PlayerId == reserve.Id).Played.Should().BeFalse();

        var entry = await db.AuditEntries.SingleAsync(e => e.GameId == game.Id, ct);
        entry.Action.Should().Be(AuditActions.GameFinished);
        entry.ActorPlayerId.Should().Be(captain.PlayerId);
    }

    [Test]
    public async Task SetTagsAsyncReplacesTheTagList()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6109, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: 6109, ct);
        var gameService = new GameService(db, captain.Guard, new FakeTimeProvider());
        var game = (
            await gameService.CreateOneOffAsync(
                team,
                captain.Actor,
                "Quiz",
                "The Pub",
                new DateOnly(2026, 9, 12),
                new TimeOnly(19, 0),
                10,
                null,
                ct
            )
        ).Value;

        await gameService.SetTagsAsync(game, team, captain.Actor, ["music", "detective"], ct);

        game.Tags.Should().Equal("music", "detective");
    }

    [Test]
    public async Task LoadMemberStatusesAsyncReflectsWhoIsSignedUp()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6110, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: 6110, ct);
        var gameService = new GameService(db, captain.Guard, new FakeTimeProvider());
        var game = (
            await gameService.CreateOneOffAsync(
                team,
                captain.Actor,
                "Quiz",
                "The Pub",
                new DateOnly(2026, 9, 12),
                new TimeOnly(19, 0),
                10,
                null,
                ct
            )
        ).Value;

        var signedUp = await SeedMemberAsync(db, team.Id, telegramUserId: 61101, ct);
        var notSignedUp = await SeedMemberAsync(db, team.Id, telegramUserId: 61102, ct);
        var signups = new SignupService(db, captain.Guard, new FakeTimeProvider());
        await signups.JoinAsync(game, signedUp.Id, ct);

        var statuses = (await gameService.LoadMemberStatusesAsync(game, team, captain.Actor, ct)).Value;

        statuses.Single(s => s.Membership.PlayerId == signedUp.Id).IsSignedUp.Should().BeTrue();
        statuses.Single(s => s.Membership.PlayerId == notSignedUp.Id).IsSignedUp.Should().BeFalse();
    }

    private static async Task<(Team Team, Franchise Franchise, TestCaptain Captain)> SeedFranchiseAsync(
        QuizrDb db,
        long chatId,
        CancellationToken ct
    )
    {
        var team = await SeedTeamAsync(db, chatId, ct);
        var creator = await SeedPlayerAsync(db, telegramUserId: chatId, ct);
        var franchise = new Franchise
        {
            TeamId = team.Id,
            Name = "Квиз, плиз!",
            DefaultVenue = "The Pub",
            DefaultCapacity = 20,
            DefaultPrice = 5m,
            Schedule = new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) },
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Franchises.Add(franchise);
        await db.SaveChangesAsync(ct);
        return (team, franchise, await TestCaptain.PromoteAsync(db, team, creator, ct));
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

    // Editing a live game is captain-only, and the check belongs to the operation rather than
    // to whichever screen reached it (STYLE.md) — a stale keyboard, or phase 2's HTTP endpoint,
    // must hit the same refusal the update router does.
    [Test]
    public async Task SettersRejectSomeoneWhoIsNotACaptain()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6112, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: 6112, ct);
        var gameService = new GameService(db, captain.Guard, new FakeTimeProvider());
        var game = (
            await gameService.CreateOneOffAsync(
                team,
                captain.Actor,
                "Quiz",
                "The Pub",
                new DateOnly(2026, 9, 12),
                new TimeOnly(19, 0),
                10,
                null,
                ct
            )
        ).Value;

        // TelegramBotClientTestHelper's default GetChatMember response is a plain member, so
        // this actor is neither an explicit grant nor a chat admin.
        var outsider = await SeedMemberAsync(db, team.Id, telegramUserId: 61121, ct);
        var outsiderActor = new Actor(outsider.Id, new TelegramUserId(61121));

        var renamed = await gameService.SetTitleAsync(game, team, outsiderActor, "Hijacked", ct);
        var resized = await gameService.SetCapacityAsync(game, team, outsiderActor, 99, ct);
        var moved = await gameService.SetStartTimeAsync(game, team, outsiderActor, new TimeOnly(9, 0), ct);

        renamed.Error.Should().BeOfType<BusinessError.NotCaptain>();
        resized.Error.Should().BeOfType<BusinessError.NotCaptain>();
        moved.Error.Should().BeOfType<BusinessError.NotCaptain>();

        var unchanged = await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct);
        unchanged.Title.Should().Be("Quiz");
        unchanged.Capacity.Should().Be(10);
    }

    private static async Task<Player> SeedPlayerAsync(QuizrDb db, long telegramUserId, CancellationToken ct)
    {
        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = $"Player {telegramUserId}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);
        return player;
    }

    private static async Task<Player> SeedMemberAsync(
        QuizrDb db,
        TeamId teamId,
        long telegramUserId,
        CancellationToken ct
    )
    {
        var player = await SeedPlayerAsync(db, telegramUserId, ct);
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
}
