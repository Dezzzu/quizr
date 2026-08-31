using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Data;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Tests;

[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class FranchiseServiceTests
{
    private readonly PostgresFixture _fixture;

    public FranchiseServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task CreateAsyncPersistsEveryField()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6001, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: team.ChatId.Value, ct);
        var service = new FranchiseService(db, captain.Guard, new FakeTimeProvider());
        var schedule = new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) };

        var franchise = (
            await service.CreateAsync(team, captain.Actor, "Квиз, плиз!", "The Pub", 20, 5.50m, schedule, ct)
        ).Value;

        franchise.TeamId.Should().Be(team.Id);
        franchise.Name.Should().Be("Квиз, плиз!");
        franchise.DefaultVenue.Should().Be("The Pub");
        franchise.DefaultCapacity.Should().Be(20);
        franchise.DefaultPrice.Should().Be(5.50m);
        franchise.Schedule.Should().BeEquivalentTo(schedule);
        franchise.ArchivedAt.Should().BeNull();
    }

    [Test]
    public async Task SettersUpdateOnlyTheirOwnField()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6002, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: team.ChatId.Value, ct);
        var service = new FranchiseService(db, captain.Guard, new FakeTimeProvider());
        var franchise = (
            await service.CreateAsync(
                team,
                captain.Actor,
                "Original",
                "Original venue",
                10,
                null,
                new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) },
                ct
            )
        ).Value;

        await service.SetNameAsync(franchise, team, captain.Actor, "Renamed", ct);
        await service.SetVenueAsync(franchise, team, captain.Actor, "New venue", ct);
        await service.SetCapacityAsync(franchise, team, captain.Actor, 25, ct);
        await service.SetPriceAsync(franchise, team, captain.Actor, 7.5m, ct);

        franchise.Name.Should().Be("Renamed");
        franchise.DefaultVenue.Should().Be("New venue");
        franchise.DefaultCapacity.Should().Be(25);
        franchise.DefaultPrice.Should().Be(7.5m);
    }

    [Test]
    public async Task ArchiveAsyncStampsArchivedAt()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6003, ct);
        var clock = new FakeTimeProvider();
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: team.ChatId.Value, ct);
        var service = new FranchiseService(db, captain.Guard, clock);
        var franchise = (
            await service.CreateAsync(
                team,
                captain.Actor,
                "Doomed",
                "Venue",
                10,
                null,
                new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) },
                ct
            )
        ).Value;

        await service.ArchiveAsync(franchise, team, captain.Actor, ct);

        franchise.ArchivedAt.Should().Be(clock.GetUtcNow());
    }

    [Test]
    public async Task CreateAsyncRejectsANameAlreadyUsedByALiveFranchise()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6004, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: team.ChatId.Value, ct);
        var service = new FranchiseService(db, captain.Guard, new FakeTimeProvider());
        var schedule = new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) };
        await service.CreateAsync(team, captain.Actor, "Квиз, плиз!", "The Pub", 20, null, schedule, ct);

        var result = await service.CreateAsync(
            team,
            captain.Actor,
            "Квиз, плиз!",
            "A different pub",
            10,
            null,
            schedule,
            ct
        );

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<BusinessError.FranchiseNameTaken>();
    }

    // The filtered unique index (ArchivedAt IS NULL) is the point of this test — an archived
    // franchise's name must not block a brand-new one from reusing it.
    [Test]
    public async Task CreateAsyncAllowsReusingAnArchivedFranchisesName()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6005, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: team.ChatId.Value, ct);
        var service = new FranchiseService(db, captain.Guard, new FakeTimeProvider());
        var schedule = new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) };
        var original = (
            await service.CreateAsync(team, captain.Actor, "Квиз, плиз!", "The Pub", 20, null, schedule, ct)
        ).Value;
        await service.ArchiveAsync(original, team, captain.Actor, ct);

        var result = await service.CreateAsync(
            team,
            captain.Actor,
            "Квиз, плиз!",
            "A different pub",
            10,
            null,
            schedule,
            ct
        );

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().NotBe(original.Id);
    }

    [Test]
    public async Task SetNameAsyncRejectsANameAlreadyUsedByALiveFranchise()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6006, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: team.ChatId.Value, ct);
        var service = new FranchiseService(db, captain.Guard, new FakeTimeProvider());
        var schedule = new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) };
        await service.CreateAsync(team, captain.Actor, "Taken", "The Pub", 20, null, schedule, ct);
        var other = (
            await service.CreateAsync(team, captain.Actor, "Available", "The Pub", 20, null, schedule, ct)
        ).Value;

        var result = await service.SetNameAsync(other, team, captain.Actor, "Taken", ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<BusinessError.FranchiseNameTaken>();
        other.Name.Should().Be("Available");
    }

    [Test]
    public async Task SetNameAsyncAllowsRenamingToItsOwnCurrentName()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6007, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: team.ChatId.Value, ct);
        var service = new FranchiseService(db, captain.Guard, new FakeTimeProvider());
        var schedule = new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) };
        var franchise = (
            await service.CreateAsync(team, captain.Actor, "Same", "The Pub", 20, null, schedule, ct)
        ).Value;

        var result = await service.SetNameAsync(franchise, team, captain.Actor, "Same", ct);

        result.IsSuccess.Should().BeTrue();
    }

    // Managing franchises is captain-only throughout, and the check lives on the operation
    // rather than on the screen that reached it (STYLE.md).
    [Test]
    public async Task SettersAndArchiveRejectSomeoneWhoIsNotACaptain()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6008, ct);
        var captain = await TestCaptain.SeedAsync(db, team, telegramUserId: 6008, ct);
        var service = new FranchiseService(db, captain.Guard, new FakeTimeProvider());
        var schedule = new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) };
        var franchise = (
            await service.CreateAsync(team, captain.Actor, "Квиз, плиз!", "The Pub", 20, null, schedule, ct)
        ).Value;

        // TelegramBotClientTestHelper's default GetChatMember response is a plain member, so
        // this actor is neither an explicit grant nor a chat admin.
        var outsider = new Player
        {
            TelegramUserId = new TelegramUserId(60081),
            DisplayName = "Outsider",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(outsider);
        await db.SaveChangesAsync(ct);
        var outsiderActor = new Actor(outsider.Id, outsider.TelegramUserId);

        var renamed = await service.SetNameAsync(franchise, team, outsiderActor, "Hijacked", ct);
        var revenued = await service.SetVenueAsync(franchise, team, outsiderActor, "Elsewhere", ct);
        var archived = await service.ArchiveAsync(franchise, team, outsiderActor, ct);

        renamed.Error.Should().BeOfType<BusinessError.NotCaptain>();
        revenued.Error.Should().BeOfType<BusinessError.NotCaptain>();
        archived.Error.Should().BeOfType<BusinessError.NotCaptain>();

        var unchanged = await db.Franchises.AsNoTracking().SingleAsync(f => f.Id == franchise.Id, ct);
        unchanged.Name.Should().Be("Квиз, плиз!");
        unchanged.DefaultVenue.Should().Be("The Pub");
        unchanged.ArchivedAt.Should().BeNull();
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
}
