using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.App.Time;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;

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
        TimeOnly time,
        string venue,
        int capacity,
        decimal? price,
        string? notes,
        List<string> tags,
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

    Task SetTagsAsync(Game game, List<string> tags, CancellationToken ct);

    Task SetStartTimeAsync(Game game, TimeOnly time, string timeZoneId, CancellationToken ct);

    // Nudge's target list — CLAUDE.md/VISION.md: "ping the players who haven't arrived yet,"
    // meaning people who signed up and are late, not people who never signed up at all.
    // Guests are excluded: a guest signup has no PlayerId, so there's nobody to @mention.
    Task<IReadOnlyList<Membership>> LoadPlayingMembersAsync(Game game, CancellationToken ct);

    Task<IReadOnlyList<MemberSignupStatus>> LoadMemberStatusesAsync(Game game, CancellationToken ct);

    Task<Result<Unit>> TryNudgeAsync(Game game, CancellationToken ct);

    // Captain-only (invariant 13's GameDeclined). No un-decline flow exists, so this is meant
    // to be gated by a confirm step at the call site.
    Task DeclineAsync(Game game, PlayerId actorPlayerId, CancellationToken ct);

    // Shared by the scheduler's auto-finish and the manual Finish button (invariant 8) — one
    // materialization path, not two. actorPlayerId null means the system did it.
    Task FinishAsync(Game game, PlayerId? actorPlayerId, CancellationToken ct);
}

// Whether this member currently has a live signup for the game — the "Manage players" view's
// register-or-drop toggle reads this to decide which action a tap performs.
public sealed record MemberSignupStatus(Membership Membership, bool IsSignedUp);

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
        // An empty schedule (a franchise with no fixed days) has no day of the week to ever
        // match — without this, the loop below would run until DateOnly overflows. Zero
        // candidates is correct here: the date picker falls back to its own custom-date entry.
        if (schedule.Count == 0)
        {
            return [];
        }

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
        TimeOnly time,
        string venue,
        int capacity,
        decimal? price,
        string? notes,
        List<string> tags,
        PlayerId createdByPlayerId,
        string timeZoneId,
        CancellationToken ct
    )
    {
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
            Tags = tags,
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

    public async Task SetTagsAsync(Game game, List<string> tags, CancellationToken ct)
    {
        game.Tags = tags;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetStartTimeAsync(Game game, TimeOnly time, string timeZoneId, CancellationToken ct)
    {
        var localDate = DateOnly.FromDateTime(TeamTime.ConvertToLocal(game.StartsAt, timeZoneId).Date);
        game.StartsAt = TeamTime.ConvertToUtc(localDate, time, timeZoneId);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Membership>> LoadPlayingMembersAsync(Game game, CancellationToken ct)
    {
        var liveSignups = await _db
            .Signups.AsNoTracking()
            .Where(s => s.GameId == game.Id && s.CancelledAt == null)
            .ToListAsync(ct);

        // Playing, not the whole roster — invariant 2's derived split, same as everywhere
        // else. Someone on the reserve isn't "late," they're not confirmed to play yet.
        var playingPlayerIds = Roster
            .Split(liveSignups, game.Capacity)
            .Playing.Where(s => s.PlayerId is not null)
            .Select(s => s.PlayerId!.Value)
            .ToHashSet();

        return await _db
            .Memberships.AsNoTracking()
            .Include(m => m.Player)
            .Where(m => m.TeamId == game.TeamId && playingPlayerIds.Contains(m.PlayerId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MemberSignupStatus>> LoadMemberStatusesAsync(Game game, CancellationToken ct)
    {
        var signedUpPlayerIds = await _db
            .Signups.AsNoTracking()
            .Where(s => s.GameId == game.Id && s.CancelledAt == null && s.PlayerId != null)
            .Select(s => s.PlayerId!.Value)
            .ToListAsync(ct);
        var signedUpSet = signedUpPlayerIds.ToHashSet();

        var members = await _db
            .Memberships.AsNoTracking()
            .Include(m => m.Player)
            .Where(m => m.TeamId == game.TeamId)
            .ToListAsync(ct);

        return members.Select(m => new MemberSignupStatus(m, signedUpSet.Contains(m.PlayerId))).ToList();
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

    public async Task DeclineAsync(Game game, PlayerId actorPlayerId, CancellationToken ct)
    {
        game.DeclinedAt = _clock.GetUtcNow();

        AuditRecorder.Record(_db, game.TeamId, game.Id, actorPlayerId, AuditActions.GameDeclined, new { }, _clock);

        await _db.SaveChangesAsync(ct);
    }

    public async Task FinishAsync(Game game, PlayerId? actorPlayerId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        var liveSignups = await _db
            .Signups.AsNoTracking()
            .Where(s => s.GameId == game.Id && s.CancelledAt == null)
            .ToListAsync(ct);
        var roster = Roster.Split(liveSignups, game.Capacity);
        var playingIds = roster.Playing.Select(s => s.Id).ToHashSet();

        // Inserted in queue order so Participation.Id — the only ordering signal it has —
        // still reflects it, for whatever later reads these rows back.
        foreach (var signup in roster.Playing.Concat(roster.Reserve))
        {
            _db.Participations.Add(
                new Participation
                {
                    GameId = game.Id,
                    PlayerId = signup.PlayerId,
                    Name = signup.IsGuest ? signup.GuestName : null,
                    Kind = ParticipationKindOf(signup),
                    Played = playingIds.Contains(signup.Id),
                    // Invariant 9: attended defaults true, the ordinary case needing zero input.
                    Attended = true,
                    CreatedAt = now,
                }
            );
        }

        game.FinishedAt = now;

        AuditRecorder.Record(_db, game.TeamId, game.Id, actorPlayerId, AuditActions.GameFinished, new { }, _clock);

        await _db.SaveChangesAsync(ct);
    }

    private static ParticipationKind ParticipationKindOf(Signup signup)
    {
        if (signup.IsMember)
        {
            return ParticipationKind.Member;
        }

        return signup.IsTeamGuest ? ParticipationKind.TeamGuest : ParticipationKind.Guest;
    }
}
