using Quizr.Domain.Entities;

namespace Quizr.Domain.Extensions;

// Derived facts about a single Game. Game.cs already documents the principle: "Open and
// in-progress are not stored — they're derived from the clock against StartsAt and
// FinishedAt." These two don't need the clock at all; anything time-relative (open,
// in-progress) still belongs wherever it's computed today (SignupService's registration
// guard, the scheduler's auto-finish check) rather than becoming a parameterless property.
public static class GameExtensions
{
    extension(Game game)
    {
        public bool IsFinished => game.FinishedAt is not null;

        public bool IsDeclined => game.DeclinedAt is not null;
    }
}
