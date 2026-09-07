using Quizr.Domain.Entities;

namespace Quizr.Domain.Extensions;

// Derived facts about a single Game. Game.cs already documents the principle: "Open and
// in-progress are not stored — they're derived from the clock against StartsAt and
// FinishedAt." None of these needs the clock: the first two read a stored timestamp, and
// EndsAt is arithmetic on another. Comparing any of them against now stays wherever the clock
// already lives (SignupService's registration guard, the scheduler's auto-finish check) rather
// than becoming a parameterless property that reaches for a TimeProvider it wasn't handed.
public static class GameExtensions
{
    // How long a game lasts. One number doing two jobs: when the scheduler finishes a game
    // nobody finished by hand (invariant 8), and how much of an evening a calendar event books
    // (docs/CALENDAR.md). They were two independently guessed numbers until the feed needed a
    // duration and found none to reuse, which is the way two constants drift until they
    // contradict each other somewhere a player can see.
    private static readonly TimeSpan Duration = TimeSpan.FromHours(3);

    extension(Game game)
    {
        public bool IsFinished => game.FinishedAt is not null;

        public bool IsDeclined => game.DeclinedAt is not null;

        // When a game stops being live, whether or not anyone has finished it. Not the same as
        // FinishedAt, which records that it actually was.
        public DateTimeOffset EndsAt => game.StartsAt + Duration;
    }
}
