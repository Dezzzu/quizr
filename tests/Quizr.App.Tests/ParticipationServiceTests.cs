using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Data;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Tests;

public class ParticipationServiceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public ParticipationServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ToggleAttendedAsyncFlipsAttended()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (_, participation) = await SeedFinishedGameWithParticipationAsync(db, chatId: 6201, ct);
        var service = new ParticipationService(db, new FakeTimeProvider());

        var toggledOff = await service.ToggleAttendedAsync(participation, ct);
        toggledOff.Attended.Should().BeFalse();

        var toggledOn = await service.ToggleAttendedAsync(participation, ct);
        toggledOn.Attended.Should().BeTrue();
    }

    [Fact]
    public async Task TogglePlayedAsyncFlipsPlayed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (_, participation) = await SeedFinishedGameWithParticipationAsync(db, chatId: 6202, ct);
        var service = new ParticipationService(db, new FakeTimeProvider());

        var toggledOff = await service.TogglePlayedAsync(participation, ct);
        toggledOff.Played.Should().BeFalse();
    }

    [Fact]
    public async Task AddVenueAssignedAsyncInsertsARowOnAFinishedGame()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (game, _) = await SeedFinishedGameWithParticipationAsync(db, chatId: 6203, ct);
        var service = new ParticipationService(db, new FakeTimeProvider());

        var result = await service.AddVenueAssignedAsync(game, "Walk-in Wendy", ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Kind.Should().Be(ParticipationKind.VenueAssigned);
        result.Value.Name.Should().Be("Walk-in Wendy");
        result.Value.Played.Should().BeTrue();
        result.Value.Attended.Should().BeTrue();
    }

    [Fact]
    public async Task AddVenueAssignedAsyncRejectsAGameThatHasNotFinished()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = new Team
        {
            ChatId = new TelegramChatId(6204),
            Name = "Test team",
            TimeZoneId = "Europe/Berlin",
            Locale = "en",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Teams.Add(team);
        var creator = new Player
        {
            TelegramUserId = new TelegramUserId(6204),
            DisplayName = "Creator",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(creator);
        await db.SaveChangesAsync(ct);
        var game = new Game
        {
            TeamId = team.Id,
            Title = "Still live",
            Venue = "The Pub",
            StartsAt = DateTimeOffset.UtcNow.AddHours(1),
            Capacity = 10,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = creator.Id,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);
        var service = new ParticipationService(db, new FakeTimeProvider());

        var result = await service.AddVenueAssignedAsync(game, "Too soon", ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<BusinessError.GameNotFinished>();
    }

    private static async Task<(Game Game, Participation Participation)> SeedFinishedGameWithParticipationAsync(
        QuizrDb db,
        long chatId,
        CancellationToken ct
    )
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
        var creator = new Player
        {
            TelegramUserId = new TelegramUserId(chatId),
            DisplayName = "Creator",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(creator);
        await db.SaveChangesAsync(ct);

        var game = new Game
        {
            TeamId = team.Id,
            Title = "Finished quiz",
            Venue = "The Pub",
            StartsAt = DateTimeOffset.UtcNow.AddHours(-5),
            FinishedAt = DateTimeOffset.UtcNow,
            Capacity = 10,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-5),
            CreatedByPlayerId = creator.Id,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);

        var participation = new Participation
        {
            GameId = game.Id,
            PlayerId = creator.Id,
            Kind = ParticipationKind.Member,
            Played = true,
            Attended = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Participations.Add(participation);
        await db.SaveChangesAsync(ct);

        return (game, participation);
    }
}
