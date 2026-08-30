using Microsoft.EntityFrameworkCore;
using Npgsql;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Services;

// Franchise mutation only — no Telegram calls, same split as ISignupService. Every field
// here is captain-supplied text already validated by FieldParsing except the name, which can
// collide with another live franchise's — captain-ness is checked by the caller, same as
// every other captain flow.
public interface IFranchiseService
{
    Task<Result<Franchise>> CreateAsync(
        TeamId teamId,
        string name,
        string? venue,
        int? capacity,
        decimal? price,
        Dictionary<DayOfWeek, TimeOnly> schedule,
        CancellationToken ct
    );

    Task<Result<Unit>> SetNameAsync(Franchise franchise, string name, CancellationToken ct);

    Task SetVenueAsync(Franchise franchise, string? venue, CancellationToken ct);

    Task SetCapacityAsync(Franchise franchise, int? capacity, CancellationToken ct);

    Task SetPriceAsync(Franchise franchise, decimal? price, CancellationToken ct);

    Task SetScheduleAsync(Franchise franchise, Dictionary<DayOfWeek, TimeOnly> schedule, CancellationToken ct);

    Task ArchiveAsync(Franchise franchise, CancellationToken ct);
}

public sealed class FranchiseService : IFranchiseService
{
    private readonly QuizrDb _db;
    private readonly TimeProvider _clock;

    public FranchiseService(QuizrDb db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<Result<Franchise>> CreateAsync(
        TeamId teamId,
        string name,
        string? venue,
        int? capacity,
        decimal? price,
        Dictionary<DayOfWeek, TimeOnly> schedule,
        CancellationToken ct
    )
    {
        if (await NameTakenAsync(teamId, name, excluding: null, ct))
        {
            return new BusinessError.FranchiseNameTaken();
        }

        var franchise = new Franchise
        {
            TeamId = teamId,
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

    public async Task<Result<Unit>> SetNameAsync(Franchise franchise, string name, CancellationToken ct)
    {
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

    public async Task SetVenueAsync(Franchise franchise, string? venue, CancellationToken ct)
    {
        franchise.DefaultVenue = venue;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetCapacityAsync(Franchise franchise, int? capacity, CancellationToken ct)
    {
        franchise.DefaultCapacity = capacity;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetPriceAsync(Franchise franchise, decimal? price, CancellationToken ct)
    {
        franchise.DefaultPrice = price;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetScheduleAsync(
        Franchise franchise,
        Dictionary<DayOfWeek, TimeOnly> schedule,
        CancellationToken ct
    )
    {
        franchise.Schedule = schedule;
        await _db.SaveChangesAsync(ct);
    }

    public async Task ArchiveAsync(Franchise franchise, CancellationToken ct)
    {
        franchise.ArchivedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }
}
