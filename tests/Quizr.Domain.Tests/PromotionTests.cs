using AwesomeAssertions;
using Quizr.Domain.Entities;

namespace Quizr.Domain.Tests;

public class PromotionTests
{
    private static readonly GameId GameId = new(1);

    [Test]
    public void ADropCausesExactlyOneReservePromotion()
    {
        var start = DateTimeOffset.UtcNow;
        var playing = new SignupBuilder(GameId).WithId(1).At(start).Build();
        var firstReserve = new SignupBuilder(GameId).WithId(2).At(start.AddMinutes(1)).Build();
        var secondReserve = new SignupBuilder(GameId).WithId(3).At(start.AddMinutes(2)).Build();

        var before = Roster.Split([playing, firstReserve, secondReserve], capacity: 1);

        var dropped = new Signup
        {
            Id = playing.Id,
            GameId = GameId,
            CreatedAt = playing.CreatedAt,
            CancelledAt = start.AddMinutes(5),
        };
        var after = Roster.Split([dropped, firstReserve, secondReserve], capacity: 1);

        var promoted = Promotion.Promoted(before, after);

        promoted.Should().Equal(firstReserve);
    }

    [Test]
    public void NobodyIsPromotedWhenTheRosterIsUnchanged()
    {
        var signups = SignupBuilder.Queue(GameId, count: 3);

        var before = Roster.Split(signups, capacity: 2);
        var after = Roster.Split(signups, capacity: 2);

        Promotion.Promoted(before, after).Should().BeEmpty();
    }

    [Test]
    public void RaisingCapacityPromotesEveryoneNewlyInRange()
    {
        var signups = SignupBuilder.Queue(GameId, count: 4);

        var before = Roster.Split(signups, capacity: 1);
        var after = Roster.Split(signups, capacity: 3);

        Promotion.Promoted(before, after).Should().Equal(signups[1], signups[2]);
    }
}
