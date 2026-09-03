using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Services;

// One upcoming game a person is signed up to, as their own schedule shows it. The Team comes
// along whole because every part of it is needed to render the line: its name tells two teams
// apart, its chat id addresses the announcement, and its timezone is the clock the start time
// is read on.
//
// Placement is derived, never stored (invariant 2) — and unlike the Board, which only needs
// how full a game is, a personal view needs the position inside the split, which no count can
// answer.
//
// GuestCount is only the guests this person brought to this game: what they owe at the door,
// and what invariant 5 decides the fate of if they drop.
public sealed record MyScheduleEntry(
    Game Game,
    Team Team,
    string? FranchiseName,
    SignupPlacement Placement,
    int GuestCount
);

// The read behind /myschedule. Separate from BoardService because the two answer different
// questions from the same rows: the Board is one team's calendar and belongs to the team, this
// is one person's and crosses teams — a person in two of them still only has one Friday night.
public sealed class MyScheduleService
{
    private readonly QuizrDb _db;

    public MyScheduleService(QuizrDb db) => _db = db;

    // Telegram only ever mints a Team from a group (TeamBootstrapService), so a DM has no chat
    // id to look one up by and membership is the only handle on "whose games are these". Teams
    // the bot has been removed from are excluded by TeamConfiguration's own query filter.
    public async Task<IReadOnlyList<Team>> LoadTeamsAsync(PlayerId playerId, CancellationToken ct) =>
        await _db.Teams.AsNoTracking().Where(t => t.Memberships.Any(m => m.PlayerId == playerId)).ToListAsync(ct);

    public async Task<IReadOnlyList<MyScheduleEntry>> LoadAsync(
        PlayerId playerId,
        IReadOnlyList<Team> teams,
        CancellationToken ct
    )
    {
        if (teams.Count == 0)
        {
            return [];
        }

        var teamIds = teams.Select(t => t.Id).ToList();

        // Whole rosters, unlike the Board's one aggregate count: invariant 2 puts the
        // playing/reserve split in C#, and the position within it is the entire point of this
        // view. Bounded by the games this one person is actually signed up to, which is a
        // handful — not by how many the teams are running.
        //
        // Franchise comes along unfiltered for the same reason the Board loads it: an archived
        // franchise still names the games already built from it.
        var games = await _db
            .Games.AsNoTracking()
            .Include(g => g.Franchise)
            .Include(g => g.Signups)
            .Where(g =>
                teamIds.Contains(g.TeamId)
                && g.FinishedAt == null
                && g.DeclinedAt == null
                && g.Signups.Any(s => s.PlayerId == playerId && s.CancelledAt == null)
            )
            .OrderBy(g => g.StartsAt)
            .ToListAsync(ct);

        var teamsById = teams.ToDictionary(t => t.Id);

        return games.Select(game => ToEntry(game, teamsById[game.TeamId], playerId)).ToList();
    }

    private static MyScheduleEntry ToEntry(Game game, Team team, PlayerId playerId)
    {
        var split = Roster.Split(game.Signups, game.Capacity);

        // At most one live signup per game and player — enforced by SignupService, so a second
        // one is a broken invariant rather than a row to pick between (STYLE.md).
        var own = game.Signups.Single(s => s.PlayerId == playerId && s.CancelledAt == null);

        // Locate is nullable for callers holding a signup that may have been cancelled since;
        // this one came out of the same list Split just ordered, so a miss is a bug in one of
        // the two, not a case to render around.
        var placement =
            Roster.Locate(split, own.Id)
            ?? throw new InvalidOperationException(
                $"Live signup {own.Id.Value} is missing from game {game.Id.Value}'s own roster."
            );

        var guestCount = game.Signups.Count(s => s.InvitedByPlayerId == playerId && s.CancelledAt == null);

        return new MyScheduleEntry(game, team, game.Franchise?.Name, placement, guestCount);
    }
}
