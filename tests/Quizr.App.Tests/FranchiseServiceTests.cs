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

        var franchise = await service.CreateAsync(team.Id, "Квиз, плиз!", "The Pub", 20, 5.50m, schedule, ct);

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
        var franchise = await service.CreateAsync(
            team.Id,
            "Original",
            "Original venue",
            10,
            null,
            new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) },
            ct
        );

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
        var franchise = await service.CreateAsync(
            team.Id,
            "Doomed",
            "Venue",
            10,
            null,
            new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new TimeOnly(19, 0) },
            ct
        );

        await service.ArchiveAsync(franchise, ct);

        franchise.ArchivedAt.Should().Be(clock.GetUtcNow());
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
