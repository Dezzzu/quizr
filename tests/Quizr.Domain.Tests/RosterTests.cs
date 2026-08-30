using AwesomeAssertions;

namespace Quizr.Domain.Tests;

public class RosterTests
{
    private static readonly GameId GameId = new(1);

    [Test]
    public void EveryoneUpToCapacityIsPlaying()
    {
        var signups = SignupBuilder.Queue(GameId, count: 3);

        var roster = Roster.Split(signups, capacity: 5);

        roster.Playing.Should().Equal(signups);
        roster.Reserve.Should().BeEmpty();
    }

    [Test]
    public void EveryoneBeyondCapacityIsReserve()
    {
        var signups = SignupBuilder.Queue(GameId, count: 5);

        var roster = Roster.Split(signups, capacity: 3);

        roster.Playing.Should().Equal(signups.Take(3));
        roster.Reserve.Should().Equal(signups.Skip(3));
    }

    [Test]
    public void ZeroCapacityPutsEveryoneInReserve()
    {
        var signups = SignupBuilder.Queue(GameId, count: 2);

        var roster = Roster.Split(signups, capacity: 0);

        roster.Playing.Should().BeEmpty();
        roster.Reserve.Should().Equal(signups);
    }

    [Test]
    public void TheSeatAtExactlyCapacityIsTheLastPlayingSeat()
    {
        var signups = SignupBuilder.Queue(GameId, count: 4);

        var roster = Roster.Split(signups, capacity: 4);

        roster.Playing.Should().Equal(signups);
        roster.Reserve.Should().BeEmpty();
    }

    [Test]
    public void GuestsOccupySeatsInTheirOwnQueuePosition()
    {
        var start = DateTimeOffset.UtcNow;
        var member = new SignupBuilder(GameId).ByPlayer(1).At(start).Build();
        var guest = new SignupBuilder(GameId).AsGuest().At(start.AddMinutes(1)).Build();
        var another = new SignupBuilder(GameId).ByPlayer(2).At(start.AddMinutes(2)).Build();

        var roster = Roster.Split([member, guest, another], capacity: 2);

        roster.Playing.Should().Equal(member, guest);
        roster.Reserve.Should().Equal(another);
    }

    [Test]
    public void ACancelledSignupFreesItsSeatForTheNextReserve()
    {
        var start = DateTimeOffset.UtcNow;
        var first = new SignupBuilder(GameId).At(start).Cancelled(start.AddMinutes(5)).Build();
        var second = new SignupBuilder(GameId).At(start.AddMinutes(1)).Build();
        var third = new SignupBuilder(GameId).At(start.AddMinutes(2)).Build();

        var roster = Roster.Split([first, second, third], capacity: 1);

        roster.Playing.Should().Equal(second);
        roster.Reserve.Should().Equal(third);
    }

    [Test]
    public void TiesOnCreatedAtBreakByInsertionOrderAndAreStableAcrossCalls()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var earlierInsert = new SignupBuilder(GameId).WithId(1).At(timestamp).Build();
        var laterInsert = new SignupBuilder(GameId).WithId(2).At(timestamp).Build();

        // Deliberately fed out of insertion order — the split must still put
        // the lower id first, and do so the same way every time it's called.
        var first = Roster.Split([laterInsert, earlierInsert], capacity: 1);
        var second = Roster.Split([laterInsert, earlierInsert], capacity: 1);

        first.Playing.Should().Equal(earlierInsert);
        second.Playing.Should().Equal(earlierInsert);
    }

    [Test]
    public void LocatesAPlayingSignupByItsOneBasedPosition()
    {
        var signups = SignupBuilder.Queue(GameId, count: 3);
        var roster = Roster.Split(signups, capacity: 3);

        var placement = Roster.Locate(roster, signups[1].Id);

        placement.Should().Be(new SignupPlacement(true, 2));
    }

    [Test]
    public void LocatesAReserveSignupByItsOneBasedPosition()
    {
        var signups = SignupBuilder.Queue(GameId, count: 3);
        var roster = Roster.Split(signups, capacity: 1);

        var placement = Roster.Locate(roster, signups[2].Id);

        placement.Should().Be(new SignupPlacement(false, 2));
    }

    [Test]
    public void LocatingAnUnknownSignupReturnsNull()
    {
        var signups = SignupBuilder.Queue(GameId, count: 2);
        var roster = Roster.Split(signups, capacity: 1);

        Roster.Locate(roster, new SignupId(999)).Should().BeNull();
    }
}
