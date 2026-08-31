using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Services;

// Team-level settings and captaincy. These used to be direct `team.Locale = ...` writes in the
// update router with the captain check alongside them, which is exactly the arrangement
// STYLE.md rules out: the check belongs to the operation, not to whichever front door reached
// it. Input parsing stays at the boundary — a bad timezone string is a rendering concern with
// its own message, not a business failure.
public interface ITeamService
{
    Task<Result<Unit>> SetTimeZoneAsync(Team team, Actor actor, string timeZoneId, CancellationToken ct);

    Task<Result<Unit>> SetLocaleAsync(Team team, Actor actor, string locale, CancellationToken ct);

    Task<Result<Unit>> SetRemindersAsync(
        Team team,
        Actor actor,
        TimeOnly eveningBeforeAt,
        TimeOnly morningOfAt,
        TimeSpan beforeStartLead,
        CancellationToken ct
    );

    Task<Result<IReadOnlyList<Membership>>> LoadMembersAsync(Team team, Actor actor, CancellationToken ct);

    // Returns the refreshed member list rather than the one toggled membership: every caller
    // re-renders the whole list, and this way the toggle costs one captain check, not two.
    Task<Result<IReadOnlyList<Membership>>> ToggleCaptainAsync(
        Team team,
        Actor actor,
        PlayerId targetPlayerId,
        CancellationToken ct
    );

    // Self-service (/myreminders): a person's own per-team reminder preferences, no captaincy
    // involved. Here rather than on a captain-gated method because it is still a team-scoped
    // read of the same table.
    Task<Membership> LoadOwnMembershipAsync(Team team, PlayerId playerId, CancellationToken ct);
}

public sealed class TeamService : ITeamService
{
    private readonly QuizrDb _db;
    private readonly TeamGuard _guard;
    private readonly TimeProvider _clock;

    public TeamService(QuizrDb db, TeamGuard guard, TimeProvider clock)
    {
        _db = db;
        _guard = guard;
        _clock = clock;
    }

    public async Task<Result<Unit>> SetTimeZoneAsync(Team team, Actor actor, string timeZoneId, CancellationToken ct)
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        team.TimeZoneId = timeZoneId;
        await _db.SaveChangesAsync(ct);

        return new Unit();
    }

    public async Task<Result<Unit>> SetLocaleAsync(Team team, Actor actor, string locale, CancellationToken ct)
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        team.Locale = locale;
        await _db.SaveChangesAsync(ct);

        return new Unit();
    }

    public async Task<Result<Unit>> SetRemindersAsync(
        Team team,
        Actor actor,
        TimeOnly eveningBeforeAt,
        TimeOnly morningOfAt,
        TimeSpan beforeStartLead,
        CancellationToken ct
    )
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        team.EveningBeforeAt = eveningBeforeAt;
        team.MorningOfAt = morningOfAt;
        team.BeforeStartLead = beforeStartLead;
        await _db.SaveChangesAsync(ct);

        return new Unit();
    }

    public async Task<Result<IReadOnlyList<Membership>>> LoadMembersAsync(Team team, Actor actor, CancellationToken ct)
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        return await QueryMembersAsync(team.Id, ct);
    }

    public async Task<Result<IReadOnlyList<Membership>>> ToggleCaptainAsync(
        Team team,
        Actor actor,
        PlayerId targetPlayerId,
        CancellationToken ct
    )
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        var membership = await _db.Memberships.SingleAsync(
            m => m.TeamId == team.Id && m.PlayerId == targetPlayerId,
            ct
        );
        membership.IsCaptain = !membership.IsCaptain;

        AuditRecorder.Record(
            _db,
            team.Id,
            null,
            actor.PlayerId,
            membership.IsCaptain ? AuditActions.CaptainGranted : AuditActions.CaptainRevoked,
            new { TargetPlayerId = targetPlayerId.Value },
            _clock
        );
        await _db.SaveChangesAsync(ct);

        return await QueryMembersAsync(team.Id, ct);
    }

    public async Task<Membership> LoadOwnMembershipAsync(Team team, PlayerId playerId, CancellationToken ct) =>
        await _db.Memberships.SingleAsync(m => m.TeamId == team.Id && m.PlayerId == playerId, ct);

    // List, not IReadOnlyList: Result<T>'s implicit conversion can't take an interface-typed
    // source, so returning this straight into a Result<IReadOnlyList<Membership>> only compiles
    // if the concrete type survives to the return statement.
    private async Task<List<Membership>> QueryMembersAsync(TeamId teamId, CancellationToken ct) =>
        await _db.Memberships.AsNoTracking().Include(m => m.Player).Where(m => m.TeamId == teamId).ToListAsync(ct);
}
