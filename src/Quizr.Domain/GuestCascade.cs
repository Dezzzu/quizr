using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;

namespace Quizr.Domain;

public sealed record GuestCascadeSplit(IReadOnlyList<Signup> AutoCancel, IReadOnlyList<Signup> NeedsChoice);

// When an inviter drops, CLAUDE.md invariant 5 decides what happens to their guests: an
// unnamed guest is nobody the door staff can identify, so it cancels along with the
// inviter automatically; a named guest may stay, so it's surfaced instead of decided here.
public static class GuestCascade
{
    public static GuestCascadeSplit ForInviterDrop(IEnumerable<Signup> signups, PlayerId inviterId)
    {
        var liveGuests = signups.Where(s => s.IsLive && s.InvitedByPlayerId == inviterId).ToList();

        return new GuestCascadeSplit(
            liveGuests.Where(s => s.GuestName is null).ToList(),
            liveGuests.Where(s => s.GuestName is not null).ToList()
        );
    }
}
