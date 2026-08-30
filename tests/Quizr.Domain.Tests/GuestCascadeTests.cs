using AwesomeAssertions;

namespace Quizr.Domain.Tests;

public class GuestCascadeTests
{
    private static readonly GameId GameId = new(1);
    private static readonly PlayerId Inviter = new(1);
    private static readonly PlayerId SomeoneElse = new(2);

    [Fact]
    public void AnUnnamedGuestAutoCancelsWithTheirInviter()
    {
        var guest = new SignupBuilder(GameId).AsGuest().InvitedBy(Inviter.Value).Build();

        var split = GuestCascade.ForInviterDrop([guest], Inviter);

        split.AutoCancel.Should().Equal(guest);
        split.NeedsChoice.Should().BeEmpty();
    }

    [Fact]
    public void ANamedGuestNeedsAnExplicitChoiceInsteadOfAutoCancelling()
    {
        var guest = new SignupBuilder(GameId).AsGuest().InvitedBy(Inviter.Value).Named("Sasha").Build();

        var split = GuestCascade.ForInviterDrop([guest], Inviter);

        split.AutoCancel.Should().BeEmpty();
        split.NeedsChoice.Should().Equal(guest);
    }

    [Fact]
    public void AGuestAlreadyCancelledIsIgnored()
    {
        var start = DateTimeOffset.UtcNow;
        var guest = new SignupBuilder(GameId)
            .AsGuest()
            .InvitedBy(Inviter.Value)
            .Named("Sasha")
            .Cancelled(start)
            .Build();

        var split = GuestCascade.ForInviterDrop([guest], Inviter);

        split.AutoCancel.Should().BeEmpty();
        split.NeedsChoice.Should().BeEmpty();
    }

    [Fact]
    public void OnlyGuestsOfTheDroppingPlayerAreConsidered()
    {
        var mine = new SignupBuilder(GameId).AsGuest().InvitedBy(Inviter.Value).Build();
        var someoneElses = new SignupBuilder(GameId).AsGuest().InvitedBy(SomeoneElse.Value).Build();

        var split = GuestCascade.ForInviterDrop([mine, someoneElses], Inviter);

        split.AutoCancel.Should().Equal(mine);
    }

    [Fact]
    public void AMemberSignupIsNeverTreatedAsAGuest()
    {
        var member = new SignupBuilder(GameId).ByPlayer(Inviter.Value).Build();

        var split = GuestCascade.ForInviterDrop([member], Inviter);

        split.AutoCancel.Should().BeEmpty();
        split.NeedsChoice.Should().BeEmpty();
    }
}
