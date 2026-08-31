using Microsoft.EntityFrameworkCore;
using Npgsql;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Services;

// Franchise mutation only — no Telegram calls, same split as ISignupService. Every field here
// is captain-supplied text already validated by FieldParsing except the name, which can collide
// with another live franchise's. Managing franchises is captain-only throughout, so every
// method here carries the check (STYLE.md) rather than trusting the screen that led to it.
public interface IFranchiseService
{
    // The live franchises /editfranchise offers.
    Task<Result<IReadOnlyList<Franchise>>> LoadEditableAsync(Team team, Actor actor, CancellationToken ct);

    Task<Result<Franchise>> CreateAsync(
        Team team,
        Actor actor,
        string name,
        string? venue,
        int? capacity,
        decimal? price,
        Dictionary<DayOfWeek, TimeOnly> schedule,
        CancellationToken ct
    );

    Task<Result<Unit>> SetNameAsync(Franchise franchise, Team team, Actor actor, string name, CancellationToken ct);

    Task<Result<Unit>> SetVenueAsync(Franchise franchise, Team team, Actor actor, string? venue, CancellationToken ct);

    Task<Result<Unit>> SetCapacityAsync(
        Franchise franchise,
        Team team,
        Actor actor,
        int? capacity,
        CancellationToken ct
    );

    Task<Result<Unit>> SetPriceAsync(Franchise franchise, Team team, Actor actor, decimal? price, CancellationToken ct);

    Task<Result<Unit>> SetScheduleAsync(
        Franchise franchise,
        Team team,
        Actor actor,
        Dictionary<DayOfWeek, TimeOnly> schedule,
        CancellationToken ct
    );

    Task<Result<Unit>> ArchiveAsync(Franchise franchise, Team team, Actor actor, CancellationToken ct);
}

public sealed class FranchiseService : IFranchiseService
{
    private readonly QuizrDb _db;
    private readonly TeamGuard _guard;
    private readonly TimeProvider _clock;

    public FranchiseService(QuizrDb db, TeamGuard guard, TimeProvider clock)
    {
        _db = db;
        _guard = guard;
        _clock = clock;
    }

    public async Task<Result<IReadOnlyList<Franchise>>> LoadEditableAsync(Team team, Actor actor, CancellationToken ct)
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        return await _db
            .Franchises.AsNoTracking()
            .Where(f => f.TeamId == team.Id && f.ArchivedAt == null)
            .ToListAsync(ct);
    }

    public async Task<Result<Franchise>> CreateAsync(
        Team team,
        Actor actor,
        string name,
        string? venue,
        int? capacity,
        decimal? price,
        Dictionary<DayOfWeek, TimeOnly> schedule,
        CancellationToken ct
    )
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        if (await NameTakenAsync(team.Id, name, excluding: null, ct))
        {
            return new BusinessError.FranchiseNameTaken();
        }

        var franchise = new Franchise
        {
            TeamId = team.Id,
            Name = name,
            DefaultVenue = venue,
            DefaultCapacity = capacity,
            DefaultPrice = price,
            Schedule = schedule,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.Franchises.Add(franchise);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The proactive check above misses only a genuine race between two captains
            // creating the same name at once — the filtered unique index is the backstop.
            _db.Entry(franchise).State = EntityState.Detached;
            return new BusinessError.FranchiseNameTaken();
        }

        return franchise;
    }

    public async Task<Result<Unit>> SetNameAsync(
        Franchise franchise,
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

        if (name != franchise.Name && await NameTakenAsync(franchise.TeamId, name, excluding: franchise.Id, ct))
        {
            return new BusinessError.FranchiseNameTaken();
        }

        franchise.Name = name;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _db.Entry(franchise).Property(f => f.Name).IsModified = false;
            return new BusinessError.FranchiseNameTaken();
        }

        return new Unit();
    }

    private Task<bool> NameTakenAsync(TeamId teamId, string name, FranchiseId? excluding, CancellationToken ct) =>
        _db.Franchises.AnyAsync(
            f => f.TeamId == teamId && f.Name == name && f.ArchivedAt == null && f.Id != excluding,
            ct
        );

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public Task<Result<Unit>> SetVenueAsync(
        Franchise franchise,
        Team team,
        Actor actor,
        string? venue,
        CancellationToken ct
    ) => ApplyAsync(team, actor, () => franchise.DefaultVenue = venue, ct);

    public Task<Result<Unit>> SetCapacityAsync(
        Franchise franchise,
        Team team,
        Actor actor,
        int? capacity,
        CancellationToken ct
    ) => ApplyAsync(team, actor, () => franchise.DefaultCapacity = capacity, ct);

    public Task<Result<Unit>> SetPriceAsync(
        Franchise franchise,
        Team team,
        Actor actor,
        decimal? price,
        CancellationToken ct
    ) => ApplyAsync(team, actor, () => franchise.DefaultPrice = price, ct);

    public Task<Result<Unit>> SetScheduleAsync(
        Franchise franchise,
        Team team,
        Actor actor,
        Dictionary<DayOfWeek, TimeOnly> schedule,
        CancellationToken ct
    ) => ApplyAsync(team, actor, () => franchise.Schedule = schedule, ct);

    public Task<Result<Unit>> ArchiveAsync(Franchise franchise, Team team, Actor actor, CancellationToken ct) =>
        ApplyAsync(team, actor, () => franchise.ArchivedAt = _clock.GetUtcNow(), ct);

    // Every plain field setter is the same three steps: check, mutate, save.
    private async Task<Result<Unit>> ApplyAsync(Team team, Actor actor, Action mutate, CancellationToken ct)
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        mutate();
        await _db.SaveChangesAsync(ct);

        return new Unit();
    }
}
