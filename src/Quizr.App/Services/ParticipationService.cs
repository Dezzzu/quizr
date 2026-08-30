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
public interface IParticipationService
{
    Task<Participation> ToggleAttendedAsync(Participation participation, CancellationToken ct);

    Task<Participation> TogglePlayedAsync(Participation participation, CancellationToken ct);

    Task<Result<Participation>> AddVenueAssignedAsync(Game game, string name, CancellationToken ct);
}

public sealed class ParticipationService : IParticipationService
{
    private readonly QuizrDb _db;
    private readonly TimeProvider _clock;

    public ParticipationService(QuizrDb db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<Participation> ToggleAttendedAsync(Participation participation, CancellationToken ct)
    {
        participation.Attended = !participation.Attended;
        await _db.SaveChangesAsync(ct);

        return participation;
    }

    public async Task<Participation> TogglePlayedAsync(Participation participation, CancellationToken ct)
    {
        participation.Played = !participation.Played;
        await _db.SaveChangesAsync(ct);

        return participation;
    }

    public async Task<Result<Participation>> AddVenueAssignedAsync(Game game, string name, CancellationToken ct)
    {
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
            Attended = true,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.Participations.Add(participation);
        await _db.SaveChangesAsync(ct);

        return participation;
    }
}
