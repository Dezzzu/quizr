using Quizr.Domain.Entities;

namespace Quizr.Domain.Extensions;

// Derived facts about a single Participation. Unlike Signup, membership here is read from
// Kind — the field the schema already gives this exact job — not re-derived from whether
// PlayerId happens to be set, though the two always agree in practice.
public static class ParticipationExtensions
{
    extension(Participation participation)
    {
        public bool IsMember => participation.Kind == ParticipationKind.Member;
    }
}
