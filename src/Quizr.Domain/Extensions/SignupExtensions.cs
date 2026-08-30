using Quizr.Domain.Entities;

namespace Quizr.Domain.Extensions;

// Derived facts about a single Signup — never stored, and always read from the scalar ids,
// never from whether a navigation property happens to be populated (see the comment on
// Signup.Player: a member whose Player wasn't Included would otherwise read as a guest).
// Collection-level derived logic (the queue split, the guest cascade) stays in Roster.cs /
// GuestCascade.cs — this is only ever about one signup at a time.
public static class SignupExtensions
{
    extension(Signup signup)
    {
        public bool IsMember => signup.PlayerId is not null;

        public bool IsGuest => signup.PlayerId is null;

        // The ownerless subset of IsGuest — CLAUDE.md: a team guest is "a guest who stays
        // after their inviter drops out. Has no owner." Invariant 5 means one is always named.
        public bool IsTeamGuest => signup.PlayerId is null && signup.InvitedByPlayerId is null;

        public bool IsLive => signup.CancelledAt is null;

        public bool IsCancelled => signup.CancelledAt is not null;
    }
}
