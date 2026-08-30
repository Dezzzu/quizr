using AwesomeAssertions;
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
        var service = new FranchiseService(db, new FakeTimeProvider());
        var schedule = new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) };

        var franchise = (await service.CreateAsync(team.Id, "Квиз, плиз!", "The Pub", 20, 5.50m, schedule, ct)).Value;

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
        var service = new FranchiseService(db, new FakeTimeProvider());
        var franchise = (
            await service.CreateAsync(
                team.Id,
                "Original",
                "Original venue",
                10,
                null,
                new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) },
                ct
            )
        ).Value;

        await service.SetNameAsync(franchise, "Renamed", ct);
        await service.SetVenueAsync(franchise, "New venue", ct);
        await service.SetCapacityAsync(franchise, 25, ct);
        await service.SetPriceAsync(franchise, 7.5m, ct);

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
        var service = new FranchiseService(db, clock);
        var franchise = (
            await service.CreateAsync(
                team.Id,
                "Doomed",
                "Venue",
                10,
                null,
                new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) },
                ct
            )
        ).Value;

        await service.ArchiveAsync(franchise, ct);

        franchise.ArchivedAt.Should().Be(clock.GetUtcNow());
    }

    [Test]
    public async Task CreateAsyncRejectsANameAlreadyUsedByALiveFranchise()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6004, ct);
        var service = new FranchiseService(db, new FakeTimeProvider());
        var schedule = new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) };
        await service.CreateAsync(team.Id, "Квиз, плиз!", "The Pub", 20, null, schedule, ct);

        var result = await service.CreateAsync(team.Id, "Квиз, плиз!", "A different pub", 10, null, schedule, ct);

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
        var service = new FranchiseService(db, new FakeTimeProvider());
        var schedule = new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) };
        var original = (await service.CreateAsync(team.Id, "Квиз, плиз!", "The Pub", 20, null, schedule, ct)).Value;
        await service.ArchiveAsync(original, ct);

        var result = await service.CreateAsync(team.Id, "Квиз, плиз!", "A different pub", 10, null, schedule, ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().NotBe(original.Id);
    }

    [Test]
    public async Task SetNameAsyncRejectsANameAlreadyUsedByALiveFranchise()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 6006, ct);
        var service = new FranchiseService(db, new FakeTimeProvider());
        var schedule = new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) };
        await service.CreateAsync(team.Id, "Taken", "The Pub", 20, null, schedule, ct);
        var other = (await service.CreateAsync(team.Id, "Available", "The Pub", 20, null, schedule, ct)).Value;

        var result = await service.SetNameAsync(other, "Taken", ct);

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
        var service = new FranchiseService(db, new FakeTimeProvider());
        var schedule = new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) };
        var franchise = (await service.CreateAsync(team.Id, "Same", "The Pub", 20, null, schedule, ct)).Value;

        var result = await service.SetNameAsync(franchise, "Same", ct);

        result.IsSuccess.Should().BeTrue();
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
