using Quizr.Domain.Entities;

namespace Quizr.Domain;

// Reserve promotion is automatic and notifies the promoted person — see
// CLAUDE.md invariant 6. The diff between two roster snapshots says who that
// is: whoever is in the new Playing list but wasn't in the old one.
public static class Promotion
{
    public static IReadOnlyList<Signup> Promoted(RosterSplit before, RosterSplit after)
    {
        var wasPlaying = before.Playing.Select(s => s.Id).ToHashSet();

        return after.Playing.Where(s => !wasPlaying.Contains(s.Id)).ToList();
    }
}
