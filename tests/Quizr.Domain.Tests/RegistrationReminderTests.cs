using AwesomeAssertions;
using Quizr.Domain.Entities;

namespace Quizr.Domain.Tests;

public class RegistrationReminderTests
{
    private static readonly DateTimeOffset StartsAt = new(2026, 9, 4, 19, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = StartsAt.AddDays(-2);

    [Test]
    public void ThresholdIsFourPlayers()
    {
        RegistrationReminder.Threshold.Should().Be(4);
    }

    [Test]
    public void ThreeSignupsAreNotEnough()
    {
        RegistrationReminder.IsDue(Game(), liveSignups: 3, Now).Should().BeFalse();
    }

    [Test]
    public void TheFourthSignupMakesItDue()
    {
        RegistrationReminder.IsDue(Game(), liveSignups: 4, Now).Should().BeTrue();
    }

    [Test]
    public void MoreThanFourStaysDueUntilSomethingSettlesIt()
    {
        RegistrationReminder.IsDue(Game(), liveSignups: 18, Now).Should().BeTrue();
    }

    [Test]
    public void ACaptainWhoSaidTheTeamIsRegisteredEndsIt()
    {
        var game = Game();
        game.TeamRegisteredAt = Now.AddHours(-1);

        RegistrationReminder.IsDue(game, liveSignups: 4, Now).Should().BeFalse();
    }

    [Test]
    public void AReminderAlreadySentIsNotDueAgain()
    {
        var game = Game();
        game.RegistrationReminderSentAt = Now.AddHours(-1);

        RegistrationReminder.IsDue(game, liveSignups: 4, Now).Should().BeFalse();
    }

    [Test]
    public void AFinishedGameIsNotDue()
    {
        var game = Game();
        game.FinishedAt = Now;

        RegistrationReminder.IsDue(game, liveSignups: 4, Now).Should().BeFalse();
    }

    [Test]
    public void ADeclinedGameIsNotDue()
    {
        var game = Game();
        game.DeclinedAt = Now;

        RegistrationReminder.IsDue(game, liveSignups: 4, Now).Should().BeFalse();
    }

    [Test]
    public void AGameThatHasStartedIsNotDue()
    {
        RegistrationReminder.IsDue(Game(), liveSignups: 4, StartsAt).Should().BeFalse();
        RegistrationReminder.IsDue(Game(), liveSignups: 4, StartsAt.AddMinutes(-1)).Should().BeTrue();
    }

    private static Game Game() =>
        new()
        {
            TeamId = new TeamId(1),
            Title = "Quiz Night",
            Venue = "The Pub",
            StartsAt = StartsAt,
            Capacity = 18,
            CreatedByPlayerId = new PlayerId(1),
        };
}
