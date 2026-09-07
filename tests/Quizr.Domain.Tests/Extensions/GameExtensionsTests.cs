using AwesomeAssertions;
using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;

namespace Quizr.Domain.Tests.Extensions;

public class GameExtensionsTests
{
    [Test]
    public void AGameWithNoFinishedAtOrDeclinedAtIsNeitherFinishedNorDeclined()
    {
        var game = Game();

        game.IsFinished.Should().BeFalse();
        game.IsDeclined.Should().BeFalse();
    }

    [Test]
    public void AGameWithFinishedAtSetIsFinished()
    {
        var game = Game();
        game.FinishedAt = DateTimeOffset.UtcNow;

        game.IsFinished.Should().BeTrue();
        game.IsDeclined.Should().BeFalse();
    }

    [Test]
    public void AGameWithDeclinedAtSetIsDeclined()
    {
        var game = Game();
        game.DeclinedAt = DateTimeOffset.UtcNow;

        game.IsDeclined.Should().BeTrue();
        game.IsFinished.Should().BeFalse();
    }

    // The one number the scheduler's auto-finish and the calendar feed's DTEND both read
    // (docs/CALENDAR.md). Pinned here so changing it is a deliberate edit to a failing test
    // rather than a quiet adjustment nobody notices in two places at once.
    [Test]
    public void AGameEndsThreeHoursAfterItStarts()
    {
        var game = Game();

        game.EndsAt.Should().Be(game.StartsAt.AddHours(3));
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
