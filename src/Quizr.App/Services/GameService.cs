using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.App.Time;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Services;

// Game mutation only — no Telegram calls, same split as ISignupService. Capacity is the one
// field that can promote a reserve (invariant 2), so it's the one routed through
// Roster.Split/Promotion.Promoted/NotificationRecorder, exactly like SignupService.DropAsync;
// every other setter is a plain field write.
public interface IGameService
{
    // The number a franchise game is known by (CLAUDE.md vocabulary: "franchise, number or
    // title"). Computed ahead of creation so the confirm screen (design decision #2) and the
    // created game show the same number rather than two independent counts that could drift.
    Task<string> PreviewFranchiseTitleAsync(Franchise franchise, CancellationToken ct);

    Task<Game> CreateFromFranchiseAsync(
        Franchise franchise,
        string title,
        DateOnly gameDate,
        string venue,
        int capacity,
        decimal? price,
        string? notes,
        PlayerId createdByPlayerId,
        string timeZoneId,
        CancellationToken ct
    );

    Task<Game> CreateOneOffAsync(
        TeamId teamId,
        string title,
        string venue,
        DateOnly gameDate,
        TimeOnly time,
        int capacity,
        decimal? price,
        PlayerId createdByPlayerId,
        string timeZoneId,
        CancellationToken ct
    );

    Task SetTitleAsync(Game game, string title, CancellationToken ct);

    Task SetVenueAsync(Game game, string venue, CancellationToken ct);

    Task<IReadOnlyList<Signup>> SetCapacityAsync(Game game, int capacity, CancellationToken ct);

    Task SetPriceAsync(Game game, decimal? price, CancellationToken ct);

    Task SetNotesAsync(Game game, string? notes, CancellationToken ct);

    Task SetStartTimeAsync(Game game, TimeOnly time, string timeZoneId, CancellationToken ct);

    Task<IReadOnlyList<Membership>> LoadMissingMembersAsync(Game game, CancellationToken ct);

    Task<Result<Unit>> TryNudgeAsync(Game game, CancellationToken ct);
}

public sealed class GameService : IGameService
{
    // Design decision #6: revised down from PLAN.md's proposed 10 minutes.
    private static readonly TimeSpan NudgeCooldown = TimeSpan.FromMinutes(5);

    // Pure — computed once when the date-picker step starts and stored in the dialog
    // (design decision #2), so a slow reply can't land on a date the schedule no longer
    // matches by the time it's picked.
    public static List<DateOnly> NextCandidateDates(
        DateOnly from,
        IReadOnlyDictionary<DayOfWeek, TimeOnly> schedule,
        int count
    )
    {
        var dates = new List<DateOnly>();
        var date = from;
        while (dates.Count < count)
        {
            if (schedule.ContainsKey(date.DayOfWeek))
            {
                dates.Add(date);
            }

            date = date.AddDays(1);
        }

        return dates;
    }

    private readonly QuizrDb _db;
    private readonly TimeProvider _clock;

    public GameService(QuizrDb db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<string> PreviewFranchiseTitleAsync(Franchise franchise, CancellationToken ct)
    {
        var number = await _db.Games.CountAsync(g => g.FranchiseId == franchise.Id, ct) + 1;
        return $"{franchise.Name} #{number}";
    }

    public async Task<Game> CreateFromFranchiseAsync(
        Franchise franchise,
        string title,
        DateOnly gameDate,
        string venue,
        int capacity,
        decimal? price,
        string? notes,
        PlayerId createdByPlayerId,
        string timeZoneId,
        CancellationToken ct
    )
    {
        var time = franchise.Schedule[gameDate.DayOfWeek];

        var game = new Game
        {
            TeamId = franchise.TeamId,
            FranchiseId = franchise.Id,
            Title = title,
            Venue = venue,
            StartsAt = TeamTime.ConvertToUtc(gameDate, time, timeZoneId),
            Capacity = capacity,
            Price = price,
            Notes = notes,
            CreatedAt = _clock.GetUtcNow(),
            CreatedByPlayerId = createdByPlayerId,
        };
        _db.Games.Add(game);
        await _db.SaveChangesAsync(ct);

        return game;
    }

    public async Task<Game> CreateOneOffAsync(
        TeamId teamId,
        string title,
        string venue,
        DateOnly gameDate,
        TimeOnly time,
        int capacity,
        decimal? price,
        PlayerId createdByPlayerId,
        string timeZoneId,
        CancellationToken ct
    )
    {
        var game = new Game
        {
            TeamId = teamId,
            Title = title,
            Venue = venue,
            StartsAt = TeamTime.ConvertToUtc(gameDate, time, timeZoneId),
            Capacity = capacity,
            Price = price,
            CreatedAt = _clock.GetUtcNow(),
            CreatedByPlayerId = createdByPlayerId,
        };
        _db.Games.Add(game);
        await _db.SaveChangesAsync(ct);

        return game;
    }

    public async Task SetTitleAsync(Game game, string title, CancellationToken ct)
    {
        game.Title = title;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetVenueAsync(Game game, string venue, CancellationToken ct)
    {
        game.Venue = venue;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Signup>> SetCapacityAsync(Game game, int capacity, CancellationToken ct)
    {
        var liveSignups = await _db
            .Signups.AsNoTracking()
            .Include(s => s.Player)
            .Where(s => s.GameId == game.Id && s.CancelledAt == null)
            .ToListAsync(ct);
        var before = Roster.Split(liveSignups, game.Capacity);

        game.Capacity = capacity;
        await _db.SaveChangesAsync(ct);

        var after = Roster.Split(liveSignups, capacity);
        var promoted = Promotion.Promoted(before, after);
        var newlyNotified = new List<Signup>();

        foreach (var signup in promoted)
        {
            if (
                await NotificationRecorder.TryRecordAsync(_db, signup.Id, NotificationKind.ReservePromotion, _clock, ct)
            )
            {
                newlyNotified.Add(signup);
            }
        }

        return newlyNotified;
    }

    public async Task SetPriceAsync(Game game, decimal? price, CancellationToken ct)
    {
        game.Price = price;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetNotesAsync(Game game, string? notes, CancellationToken ct)
    {
        game.Notes = notes;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetStartTimeAsync(Game game, TimeOnly time, string timeZoneId, CancellationToken ct)
    {
        var localDate = DateOnly.FromDateTime(TeamTime.ConvertToLocal(game.StartsAt, timeZoneId).Date);
        game.StartsAt = TeamTime.ConvertToUtc(localDate, time, timeZoneId);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Membership>> LoadMissingMembersAsync(Game game, CancellationToken ct)
    {
        var signedUpPlayerIds = await _db
            .Signups.AsNoTracking()
            .Where(s => s.GameId == game.Id && s.CancelledAt == null && s.PlayerId != null)
            .Select(s => s.PlayerId!.Value)
            .ToListAsync(ct);

        return await _db
            .Memberships.AsNoTracking()
            .Include(m => m.Player)
            .Where(m => m.TeamId == game.TeamId && !signedUpPlayerIds.Contains(m.PlayerId))
            .ToListAsync(ct);
    }

    public async Task<Result<Unit>> TryNudgeAsync(Game game, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (game.LastNudgedAt is { } lastNudgedAt && now < lastNudgedAt + NudgeCooldown)
        {
            return new BusinessError.NudgeOnCooldown();
        }

        game.LastNudgedAt = now;
        await _db.SaveChangesAsync(ct);

        return new Unit();
    }
}
