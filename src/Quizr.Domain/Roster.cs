using Quizr.Domain.Entities;

namespace Quizr.Domain;

public sealed record RosterSplit(IReadOnlyList<Signup> Playing, IReadOnlyList<Signup> Reserve);

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
        var queue = signups
            .Where(s => s.CancelledAt is null)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id.Value)
            .ToList();

        return new RosterSplit(queue.Take(capacity).ToList(), queue.Skip(capacity).ToList());
    }
}
