using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;

namespace Quizr.Domain;

// When captains get asked to register the team for a game. Pure, like Roster: the caller
// loads the live signup count, so the rule stays testable with no database.
public static class RegistrationReminder
{
    // Guests occupy seats (invariant 4), so the count is every live signup rather than members
    // only — four people is four people at the door, whoever brought them.
    public const int DefaultThreshold = 4;

    public static int ThresholdFor(Game game) => game.RegistrationThreshold ?? DefaultThreshold;

    // Not after kickoff: registering a team for a quiz that has already started is moot, and a
    // roster that reaches the threshold mid-game is a late arrival, not a team that needs
    // signing up.
    public static bool IsDue(Game game, int liveSignups, DateTimeOffset now) =>
        liveSignups >= ThresholdFor(game)
        && game is { TeamRegisteredAt: null, RegistrationReminderSentAt: null }
        && !game.IsFinished
        && !game.IsDeclined
        && now < game.StartsAt;
}
