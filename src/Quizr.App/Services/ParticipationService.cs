using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;

namespace Quizr.App.Services;

// Editing a finished game's roster (invariant 11's second half): once a game finishes,
// Participation rows are what a captain edits, not signups — those become immutable
// history. Toggling takes an already-loaded row (same convention as ISignupService/
// IGameService taking Game, not GameId) — a stale/bogus id is the caller's lookup to handle,
// same as HandleGameCallbackAsync already does for a missing Game. Only adding a row is
// guarded here, since that's the one case this service alone can tell is invalid: before a
// game finishes (invariant 10), there's nothing yet to add to.
//
// Every method here is reachable only through a captain-gated handler (unlike
// ISignupService.JoinAsync/DropAsync, which self-service also calls), so the audit write —
// invariant 13 — lives inside the service itself rather than at each call site.
public interface IParticipationService
{
    // Captain-only: the manage-roster view for a finished game (invariant 11).
    Task<Result<IReadOnlyList<Participation>>> LoadRosterAsync(Game game, Team team, Actor actor, CancellationToken ct);

    Task<Result<Participation>> TogglePlayedAsync(
        Participation participation,
        Team team,
        Actor actor,
        CancellationToken ct
    );

    Task<Result<Participation>> AddVenueAssignedAsync(
        Game game,
        Team team,
        Actor actor,
        string name,
        CancellationToken ct
    );
}

public sealed class ParticipationService : IParticipationService
{
    private readonly QuizrDb _db;
    private readonly TeamGuard _guard;
    private readonly TimeProvider _clock;

    public ParticipationService(QuizrDb db, TeamGuard guard, TimeProvider clock)
    {
        _db = db;
        _guard = guard;
        _clock = clock;
    }

    public async Task<Result<IReadOnlyList<Participation>>> LoadRosterAsync(
        Game game,
        Team team,
        Actor actor,
        CancellationToken ct
    )
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        return await _db
            .Participations.AsNoTracking()
            .Include(p => p.Player)
            .Where(p => p.GameId == game.Id)
            // Id breaks ties on identical CreatedAt (FinishAsync stamps a whole batch with the
            // same instant) — same reasoning as Roster.Split.
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .ToListAsync(ct);
    }

    public async Task<Result<Participation>> TogglePlayedAsync(
        Participation participation,
        Team team,
        Actor actor,
        CancellationToken ct
    )
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        participation.Played = !participation.Played;

        AuditRecorder.Record(
            _db,
            participation.Game.TeamId,
            participation.GameId,
            actor.PlayerId,
            AuditActions.ParticipationPlayedToggled,
            new { ParticipationId = participation.Id.Value, participation.Played },
            _clock
        );

        await _db.SaveChangesAsync(ct);

        return participation;
    }

    public async Task<Result<Participation>> AddVenueAssignedAsync(
        Game game,
        Team team,
        Actor actor,
        string name,
        CancellationToken ct
    )
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        if (!game.IsFinished)
        {
            return new BusinessError.GameNotFinished();
        }

        var participation = new Participation
        {
            GameId = game.Id,
            Name = name,
            Kind = ParticipationKind.VenueAssigned,
            Played = true,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.Participations.Add(participation);

        AuditRecorder.Record(
            _db,
            game.TeamId,
            game.Id,
            actor.PlayerId,
            AuditActions.VenuePlayerAdded,
            new { Name = name },
            _clock
        );

        await _db.SaveChangesAsync(ct);

        return participation;
    }
}
