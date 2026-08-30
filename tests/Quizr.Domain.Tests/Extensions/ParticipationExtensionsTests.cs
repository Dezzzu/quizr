using AwesomeAssertions;
using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;

namespace Quizr.Domain.Tests.Extensions;

public class ParticipationExtensionsTests
{
    [Test]
    public void AMemberParticipationIsAMember()
    {
        Participation(ParticipationKind.Member).IsMember.Should().BeTrue();
    }

    [Test]
    [Arguments(ParticipationKind.Guest)]
    [Arguments(ParticipationKind.TeamGuest)]
    [Arguments(ParticipationKind.VenueAssigned)]
    public void ANonMemberParticipationIsNotAMember(ParticipationKind kind)
    {
        Participation(kind).IsMember.Should().BeFalse();
    }

    private static Participation Participation(ParticipationKind kind) =>
        new()
        {
            Id = new ParticipationId(1),
            GameId = new GameId(1),
            Kind = kind,
            Played = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
}
