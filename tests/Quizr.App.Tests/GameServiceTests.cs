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

    [Test]
    public async Task PreviewFranchiseTitleAsyncNumbersSequentially()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (team, franchise, creator) = await SeedFranchiseAsync(db, chatId: 6101, ct);
        var service = new GameService(db, new FakeTimeProvider());

        (await service.PreviewFranchiseTitleAsync(franchise, ct)).Should().Be($"{franchise.Name} #1");

        await service.CreateFromFranchiseAsync(
            franchise,
            $"{franchise.Name} #1",
            new DateOnly(2026, 9, 7),
            franchise.DefaultVenue,
            franchise.DefaultCapacity,
            franchise.DefaultPrice,
            null,
            creator.Id,
            team.TimeZoneId!,
            ct
        );

        (await service.PreviewFranchiseTitleAsync(franchise, ct)).Should().Be($"{franchise.Name} #2");
    }

    [Test]
    public async Task CreateFromFranchiseAsyncComputesStartsAtFromTheScheduleTime()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (team, franchise, creator) = await SeedFranchiseAsync(db, chatId: 6102, ct);
        var service = new GameService(db, new FakeTimeProvider());

        // 2026-09-07 is a Monday — franchise plays Mondays at 19:00 Europe/Berlin (CEST, +2).
        var game = await service.CreateFromFranchiseAsync(
            franchise,
            "Квиз, плиз! #1",
            new DateOnly(2026, 9, 7),
            franchise.DefaultVenue,
            franchise.DefaultCapacity,
            franchise.DefaultPrice,
            null,
            creator.Id,
            team.TimeZoneId!,
            ct
        );

        game.FranchiseId.Should().Be(franchise.Id);
        game.StartsAt.Should().Be(new DateTimeOffset(2026, 9, 7, 17, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task CreateOneOffAsyncLeavesFranchiseIdNull()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6103, ct);
        var creator = await SeedPlayerAsync(db, telegramUserId: 6103, ct);
        var service = new GameService(db, new FakeTimeProvider());

        var game = await service.CreateOneOffAsync(
            team.Id,
            "One-off quiz",
            "The Pub",
            new DateOnly(2026, 9, 12),
            new TimeOnly(19, 0),
            10,
            null,
            creator.Id,
            team.TimeZoneId!,
            ct
        );

        game.FranchiseId.Should().BeNull();
        game.Title.Should().Be("One-off quiz");
    }

    [Test]
    public async Task SetCapacityAsyncPromotesAReserveAndRecordsANotification()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6104, ct);
        var creator = await SeedPlayerAsync(db, telegramUserId: 6104, ct);
        var gameService = new GameService(db, new FakeTimeProvider());
        var game = await gameService.CreateOneOffAsync(
            team.Id,
            "Quiz",
            "The Pub",
            new DateOnly(2026, 9, 12),
            new TimeOnly(19, 0),
            1,
            null,
            creator.Id,
            team.TimeZoneId!,
            ct
        );

        var signups = new SignupService(db, new FakeTimeProvider());
        var playing = await SeedPlayerAsync(db, telegramUserId: 61041, ct);
        var reserve = await SeedPlayerAsync(db, telegramUserId: 61042, ct);
        await signups.JoinAsync(game, playing.Id, ct);
        var reserveSignup = (await signups.JoinAsync(game, reserve.Id, ct)).Value;

        var promoted = await gameService.SetCapacityAsync(game, 2, ct);

        promoted.Select(s => s.Id).Should().ContainSingle(id => id == reserveSignup.Id);
        game.Capacity.Should().Be(2);
        (await db.Notifications.CountAsync(n => n.SignupId == reserveSignup.Id, ct)).Should().Be(1);
    }

    [Test]
    public async Task LoadMissingMembersAsyncExcludesEveryoneAlreadySignedUp()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6105, ct);
        var creator = await SeedPlayerAsync(db, telegramUserId: 6105, ct);
        var gameService = new GameService(db, new FakeTimeProvider());
        var game = await gameService.CreateOneOffAsync(
            team.Id,
            "Quiz",
            "The Pub",
            new DateOnly(2026, 9, 12),
            new TimeOnly(19, 0),
            10,
            null,
            creator.Id,
            team.TimeZoneId!,
            ct
        );

        var signedUp = await SeedMemberAsync(db, team.Id, telegramUserId: 61051, ct);
        var missing = await SeedMemberAsync(db, team.Id, telegramUserId: 61052, ct);
        var signups = new SignupService(db, new FakeTimeProvider());
        await signups.JoinAsync(game, signedUp.Id, ct);

        var missingMembers = await gameService.LoadMissingMembersAsync(game, ct);

        missingMembers.Select(m => m.PlayerId).Should().Equal(missing.Id);
    }

    [Test]
    public async Task TryNudgeAsyncStampsLastNudgedAtOnFirstCallAndBlocksASecondWithinTheCooldown()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6106, ct);
        var creator = await SeedPlayerAsync(db, telegramUserId: 6106, ct);
        var clock = new FakeTimeProvider();
        var gameService = new GameService(db, clock);
        var game = await gameService.CreateOneOffAsync(
            team.Id,
            "Quiz",
            "The Pub",
            new DateOnly(2026, 9, 12),
            new TimeOnly(19, 0),
            10,
            null,
            creator.Id,
            team.TimeZoneId!,
            ct
        );

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

    private static async Task<(Team Team, Franchise Franchise, Player Creator)> SeedFranchiseAsync(
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
        return (team, franchise, creator);
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
