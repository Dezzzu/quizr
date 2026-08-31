using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;

namespace Quizr.Domain;

public sealed record RosterSplit(IReadOnlyList<Signup> Playing, IReadOnlyList<Signup> Reserve);

// Where a signup landed after a split — 1-based, within its own list. Lets a
// confirmation message say "you're in" versus "you're #3 on the reserve list"
// without the caller re-deriving the index itself.
public sealed record SignupPlacement(bool IsPlaying, int Position);

// Playing versus reserve is derived, never stored — see CLAUDE.md invariant 2.
// A signup records who, which game and when; rendering any message loads the
// whole ordered roster anyway, so the split is Take(capacity)/Skip(capacity)
// over that list.
public static class Roster
{
    public static RosterSplit Split(IEnumerable<Signup> signups, int capacity)
    {
        // Id breaks ties on identical CreatedAt: it's assigned in insertion
        // order, so it recovers the ordering a timestamp alone can't.
        var queue = signups.Where(s => s.IsLive).OrderBy(s => s.CreatedAt).ThenBy(s => s.Id.Value).ToList();

        return new RosterSplit(queue.Take(capacity).ToList(), queue.Skip(capacity).ToList());
    }

    // The count form of Take(capacity), for callers that only need "how many seats are taken"
    // and would otherwise load a whole roster to count it — the Board, listing every upcoming
    // game at once. Lives here rather than at the call site so the capacity rule stays in one
    // place with Split itself.
    public static int PlayingCount(int liveSignupCount, int capacity) => Math.Min(liveSignupCount, capacity);

    // The matching count form of Skip(capacity). Zero rather than negative for a game with
    // seats to spare, so a caller can treat "is anyone waiting" as ReserveCount > 0.
    public static int ReserveCount(int liveSignupCount, int capacity) => Math.Max(0, liveSignupCount - capacity);

    public static SignupPlacement? Locate(RosterSplit split, SignupId signupId)
    {
        var playingIndex = IndexOf(split.Playing, signupId);
        if (playingIndex >= 0)
        {
            return new SignupPlacement(true, playingIndex + 1);
        }

        var reserveIndex = IndexOf(split.Reserve, signupId);
        return reserveIndex >= 0 ? new SignupPlacement(false, reserveIndex + 1) : null;
    }

    private static int IndexOf(IReadOnlyList<Signup> signups, SignupId signupId)
    {
        for (var i = 0; i < signups.Count; i++)
        {
            if (signups[i].Id == signupId)
            {
                return i;
            }
        }

        return -1;
    }
}
