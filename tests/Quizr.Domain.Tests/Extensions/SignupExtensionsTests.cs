using AwesomeAssertions;
using Quizr.Domain.Extensions;

namespace Quizr.Domain.Tests.Extensions;

public class SignupExtensionsTests
{
    private static readonly GameId GameId = new(1);

    [Test]
    public void AMemberSignupIsMemberOnly()
    {
        var signup = new SignupBuilder(GameId).ByPlayer(1).Build();

        signup.IsMember.Should().BeTrue();
        signup.IsGuest.Should().BeFalse();
        signup.IsTeamGuest.Should().BeFalse();
    }

    [Test]
    public void AGuestWithAnInviterIsGuestButNotTeamGuest()
    {
        var signup = new SignupBuilder(GameId).AsGuest().InvitedBy(1).Build();

        signup.IsMember.Should().BeFalse();
        signup.IsGuest.Should().BeTrue();
        signup.IsTeamGuest.Should().BeFalse();
        signup.HasInviter.Should().BeTrue();
    }

    [Test]
    public void AGuestWithNoInviterIsATeamGuest()
    {
        var signup = new SignupBuilder(GameId).AsGuest().Named("Sasha").Build();

        signup.IsGuest.Should().BeTrue();
        signup.IsTeamGuest.Should().BeTrue();
        signup.HasInviter.Should().BeFalse();
    }

    [Test]
    public void ALiveSignupIsNotCancelled()
    {
        var signup = new SignupBuilder(GameId).Build();

        signup.IsLive.Should().BeTrue();
        signup.IsCancelled.Should().BeFalse();
    }

    [Test]
    public void ACancelledSignupIsNotLive()
    {
        var signup = new SignupBuilder(GameId).Cancelled(DateTimeOffset.UtcNow).Build();

        signup.IsLive.Should().BeFalse();
        signup.IsCancelled.Should().BeTrue();
    }
}
