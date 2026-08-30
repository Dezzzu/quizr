using AwesomeAssertions;
using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;

namespace Quizr.Domain.Tests.Extensions;

public class GameExtensionsTests
{
    [Fact]
    public void AGameWithNoFinishedAtOrDeclinedAtIsNeitherFinishedNorDeclined()
    {
        var game = Game();

        game.IsFinished.Should().BeFalse();
        game.IsDeclined.Should().BeFalse();
    }

    [Fact]
    public void AGameWithFinishedAtSetIsFinished()
    {
        var game = Game();
        game.FinishedAt = DateTimeOffset.UtcNow;

        game.IsFinished.Should().BeTrue();
        game.IsDeclined.Should().BeFalse();
    }

    [Fact]
    public void AGameWithDeclinedAtSetIsDeclined()
    {
        var game = Game();
        game.DeclinedAt = DateTimeOffset.UtcNow;

        game.IsDeclined.Should().BeTrue();
        game.IsFinished.Should().BeFalse();
    }

    private static Game Game() =>
        new()
        {
            Id = new GameId(1),
            TeamId = new TeamId(1),
            Title = "Quiz Night",
            Venue = "The Pub",
            StartsAt = DateTimeOffset.UtcNow.AddDays(1),
            Capacity = 10,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = new PlayerId(1),
        };
}
