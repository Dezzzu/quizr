using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.App.Rendering;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;

namespace Quizr.App.Calendar;

// The read behind one person's .ics feed. A sibling of MyScheduleService rather than a reuse
// of it: that one answers "where am I going next" and stops at the present, this one carries a
// month of history so a calendar has something to show where the events already happened.
public sealed class CalendarFeedService
{
    // Roughly a month back and a year forward, in whole UTC days. Never the whole history: a
    // subscription feed is re-fetched in full every time, so its size is a running cost rather
    // than a one-off. Whole days matter beyond tidiness — the boundary moving is what makes
    // the date part of the cache key correct, since nothing else would tell a client that a
    // game has crossed into view.
    private static readonly int PastDays = 30;
    private static readonly int FutureMonths = 12;

    private readonly QuizrDb _db;
    private readonly TimeProvider _clock;

    public CalendarFeedService(QuizrDb db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<CalendarFeed> LoadAsync(PlayerId playerId, CancellationToken ct)
    {
        var today = _clock.GetUtcNow().UtcDateTime.Date;
        var from = new DateTimeOffset(today.AddDays(-PastDays), TimeSpan.Zero);
        var until = new DateTimeOffset(today.AddMonths(FutureMonths), TimeSpan.Zero);

        // Teams first, like MyScheduleService: TeamConfiguration's global filter is what drops
        // the ones the bot was removed from, and asking for them directly is what applies it.
        var teams = await _db
            .Teams.AsNoTracking()
            .Where(t => t.Memberships.Any(m => m.PlayerId == playerId))
            .ToListAsync(ct);

        if (teams.Count == 0)
        {
            return new CalendarFeed(null, []);
        }

        var teamIds = teams.Select(t => t.Id).ToList();

        // Two ways a game reaches this feed, and they are deliberately not the same test.
        // A live game is one this person holds a live signup for. A finished one is read off
        // its participation rows instead — invariant 10 says statistics read those and never
        // the signups, and this is the same claim: once a game is finished, what happened is
        // what the participation says happened, including a captain's later corrections.
        // A declined game is in neither, and needs no tombstone: dropping it from the next
        // response is how a subscription feed removes anything.
        var games = await _db
            .Games.AsNoTracking()
            .Include(g => g.Franchise)
            .Include(g => g.Signups)
            .Include(g => g.Participations)
            .Where(g =>
                teamIds.Contains(g.TeamId)
                && g.DeclinedAt == null
                && g.StartsAt >= from
                && g.StartsAt < until
                && (
                    g.FinishedAt == null
                        ? g.Signups.Any(s => s.PlayerId == playerId && s.CancelledAt == null)
                        : g.Participations.Any(p => p.PlayerId == playerId)
                )
            )
            .OrderBy(g => g.StartsAt)
            .ThenBy(g => g.Id)
            // Two collection Includes on one query multiply each other: a game with 20 signups
            // and 20 participations comes back as 400 rows, all but 40 of them duplicated.
            // One query per collection instead — EF warns about exactly this otherwise.
            .AsSplitQuery()
            .ToListAsync(ct);

        var teamsById = teams.ToDictionary(t => t.Id);
        var entries = games.Select(game => ToEntry(game, teamsById[game.TeamId], playerId)).ToList();

        var sharedTeamName = entries.Select(e => e.TeamId).Distinct().Count() == 1 ? entries[0].TeamName : null;

        return new CalendarFeed(sharedTeamName, entries);
    }

    private static CalendarEntry ToEntry(Game game, Team team, PlayerId playerId)
    {
        var (status, reservePosition) = game.IsFinished ? Finished(game, playerId) : Live(game, playerId);

        return new CalendarEntry(
            game.Id,
            team.Id,
            team.Name,
            game.Title,
            game.Franchise?.Name,
            game.Venue,
            game.StartsAt,
            game.EndsAt,
            game.RevisedAt,
            game.Revision,
            team.TimeZoneId!,
            status,
            reservePosition,
            game.Price,
            game.Notes,
            game.Tags,
            game.IsFinished ? 0 : game.Signups.Count(s => s.InvitedByPlayerId == playerId && s.CancelledAt == null),
            AnnouncementLink.Build(team.ChatId, game.AnnouncementMessageId)
        );
    }

    private static (CalendarEntryStatus Status, int ReservePosition) Live(Game game, PlayerId playerId)
    {
        var split = Roster.Split(game.Signups, game.Capacity);

        // At most one live signup per game and player, enforced by SignupService — so a second
        // one is a broken invariant rather than a row to choose between (STYLE.md).
        var own = game.Signups.Single(s => s.PlayerId == playerId && s.CancelledAt == null);
        var placement =
            Roster.Locate(split, own.Id)
            ?? throw new InvalidOperationException(
                $"Live signup {own.Id.Value} is missing from game {game.Id.Value}'s own roster."
            );

        return placement.IsPlaying
            ? (CalendarEntryStatus.Playing, 0)
            : (CalendarEntryStatus.Reserve, placement.Position);
    }

    private static (CalendarEntryStatus Status, int ReservePosition) Finished(Game game, PlayerId playerId)
    {
        // Finishing writes one row per person (invariant 10), so a finished game reached
        // through the query above always has this person's row.
        var participation = game.Participations.Single(p => p.PlayerId == playerId);

        return (participation.Played ? CalendarEntryStatus.Played : CalendarEntryStatus.DidNotPlay, 0);
    }
}
