using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Services;

// Franchise mutation only — no Telegram calls, same split as ISignupService. Every field
// here is captain-supplied text already validated by FieldParsing; nothing here can fail on
// its own terms, so there's no Result<T> — captain-ness is checked by the caller, same as
// every other captain flow.
public interface IFranchiseService
{
    Task<Franchise> CreateAsync(
        TeamId teamId,
        string name,
        string venue,
        int capacity,
        decimal? price,
        Dictionary<DayOfWeek, TimeOnly> schedule,
        CancellationToken ct
    );

    Task SetNameAsync(Franchise franchise, string name, CancellationToken ct);

    Task SetVenueAsync(Franchise franchise, string venue, CancellationToken ct);

    Task SetCapacityAsync(Franchise franchise, int capacity, CancellationToken ct);

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

    public async Task<Franchise> CreateAsync(
        TeamId teamId,
        string name,
        string venue,
        int capacity,
        decimal? price,
        Dictionary<DayOfWeek, TimeOnly> schedule,
        CancellationToken ct
    )
    {
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
        await _db.SaveChangesAsync(ct);

        return franchise;
    }

    public async Task SetNameAsync(Franchise franchise, string name, CancellationToken ct)
    {
        franchise.Name = name;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetVenueAsync(Franchise franchise, string venue, CancellationToken ct)
    {
        franchise.DefaultVenue = venue;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetCapacityAsync(Franchise franchise, int capacity, CancellationToken ct)
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
