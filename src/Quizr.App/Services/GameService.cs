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

    // The franchise list a new game can be built from, plus the timezone guard that has to
    // pass before any of it means anything — one call, because /newgame needs both answers
    // and either one failing stops it.
    Task<Result<IReadOnlyList<Franchise>>> LoadNewGameOptionsAsync(Team team, Actor actor, CancellationToken ct);

    // The games /editgame offers: live ones only, soonest first.
    Task<Result<IReadOnlyList<Game>>> LoadEditableGamesAsync(Team team, Actor actor, CancellationToken ct);

    Task<Result<Game>> LoadForEditAsync(Team team, Actor actor, GameId gameId, CancellationToken ct);

    // The team carries the timezone every start instant is computed against, so these take it
    // rather than a separate id, and answer TeamNotConfigured themselves if it isn't set yet.
    Task<Result<Game>> CreateFromFranchiseAsync(
        Team team,
        Actor actor,
        Franchise franchise,
        string title,
        DateOnly gameDate,
        TimeOnly time,
        string venue,
        int capacity,
        decimal? price,
        string? notes,
        List<string> tags,
        CancellationToken ct
    );

    Task<Result<Game>> CreateOneOffAsync(
        Team team,
        Actor actor,
        string title,
        string venue,
        DateOnly gameDate,
        TimeOnly time,
        int capacity,
        decimal? price,
        CancellationToken ct
    );

    // Editing a game is captain-only like creating one, so every setter carries the same check
    // rather than trusting whichever screen led here to have made it.
    Task<Result<Unit>> SetTitleAsync(Game game, Team team, Actor actor, string title, CancellationToken ct);

    Task<Result<Unit>> SetVenueAsync(Game game, Team team, Actor actor, string venue, CancellationToken ct);

    Task<Result<IReadOnlyList<Signup>>> SetCapacityAsync(
        Game game,
        Team team,
        Actor actor,
        int capacity,
        CancellationToken ct
    );

    Task<Result<Unit>> SetPriceAsync(Game game, Team team, Actor actor, decimal? price, CancellationToken ct);

    Task<Result<Unit>> SetNotesAsync(Game game, Team team, Actor actor, string? notes, CancellationToken ct);

    Task<Result<Unit>> SetTagsAsync(Game game, Team team, Actor actor, List<string> tags, CancellationToken ct);

    Task<Result<Unit>> SetStartTimeAsync(Game game, Team team, Actor actor, TimeOnly time, CancellationToken ct);

    // Nudge's target list — CLAUDE.md/VISION.md: "ping the players who haven't arrived yet,"
    // meaning people who signed up and are late, not people who never signed up at all.
    // Guests are excluded: a guest signup has no PlayerId, so there's nobody to @mention.
    Task<Result<IReadOnlyList<Membership>>> LoadPlayingMembersAsync(
        Game game,
        Team team,
        Actor actor,
        CancellationToken ct
    );

    Task<Result<IReadOnlyList<MemberSignupStatus>>> LoadMemberStatusesAsync(
        Game game,
        Team team,
        Actor actor,
        CancellationToken ct
    );

    Task<Result<Unit>> TryNudgeAsync(Game game, Team team, Actor actor, CancellationToken ct);

    // The gate for a confirm step that has nothing to load or write yet — declining asks
    // "are you sure?" first, and offering that keyboard to someone who could never go through
    // with it is worse than refusing up front. Everywhere else the check rides along on the
    // load or the write the handler was making anyway.
    Task<Result<Unit>> EnsureCanManageAsync(Team team, Actor actor, CancellationToken ct);

    // Invariant 13's GameDeclined. No un-decline flow exists, so the call site is still
    // expected to put a confirm step in front of it.
    Task<Result<Unit>> DeclineAsync(Game game, Team team, Actor actor, CancellationToken ct);

    // Shared by the scheduler's auto-finish and the manual Finish button (invariant 8) — one
    // materialization path, not two. A null actor means the system did it, which is also the
    // one case that skips the captain check: the scheduler has nobody to check.
    Task<Result<Unit>> FinishAsync(Game game, Team team, Actor? actor, CancellationToken ct);
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
    private readonly TeamGuard _guard;
    private readonly TimeProvider _clock;

    public GameService(QuizrDb db, TeamGuard guard, TimeProvider clock)
    {
        _db = db;
        _guard = guard;
        _clock = clock;
    }

    public async Task<string> PreviewFranchiseTitleAsync(Franchise franchise, CancellationToken ct)
    {
        var number = await _db.Games.CountAsync(g => g.FranchiseId == franchise.Id, ct) + 1;
        return $"{franchise.Name} #{number}";
    }

    public async Task<Result<IReadOnlyList<Franchise>>> LoadNewGameOptionsAsync(
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

        var configured = TeamGuard.EnsureTimeZoneConfigured(team);
        if (!configured.IsSuccess)
        {
            return configured.Error;
        }

        return await _db
            .Franchises.AsNoTracking()
            .Where(f => f.TeamId == team.Id && f.ArchivedAt == null)
            .ToListAsync(ct);
    }

    public async Task<Result<IReadOnlyList<Game>>> LoadEditableGamesAsync(Team team, Actor actor, CancellationToken ct)
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        return await _db
            .Games.AsNoTracking()
            .Where(g => g.TeamId == team.Id && g.FinishedAt == null && g.DeclinedAt == null)
            .OrderBy(g => g.StartsAt)
            .ToListAsync(ct);
    }

    public async Task<Result<Game>> LoadForEditAsync(Team team, Actor actor, GameId gameId, CancellationToken ct)
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        return await _db.Games.SingleAsync(g => g.Id == gameId, ct);
    }

    public async Task<Result<Game>> CreateFromFranchiseAsync(
        Team team,
        Actor actor,
        Franchise franchise,
        string title,
        DateOnly gameDate,
        TimeOnly time,
        string venue,
        int capacity,
        decimal? price,
        string? notes,
        List<string> tags,
        CancellationToken ct
    )
    {
        var ready = await EnsureCaptainOfConfiguredTeamAsync(team, actor, ct);
        if (!ready.IsSuccess)
        {
            return ready.Error;
        }

        var game = new Game
        {
            TeamId = franchise.TeamId,
            FranchiseId = franchise.Id,
            Title = title,
            Venue = venue,
            StartsAt = TeamTime.ConvertToUtc(gameDate, time, team.TimeZoneId!),
            Capacity = capacity,
            Price = price,
            Notes = notes,
            Tags = tags,
            CreatedAt = _clock.GetUtcNow(),
            CreatedByPlayerId = actor.PlayerId,
        };
        _db.Games.Add(game);
        await _db.SaveChangesAsync(ct);

        return game;
    }

    public async Task<Result<Game>> CreateOneOffAsync(
        Team team,
        Actor actor,
        string title,
        string venue,
        DateOnly gameDate,
        TimeOnly time,
        int capacity,
        decimal? price,
        CancellationToken ct
    )
    {
        var ready = await EnsureCaptainOfConfiguredTeamAsync(team, actor, ct);
        if (!ready.IsSuccess)
        {
            return ready.Error;
        }

        var game = new Game
        {
            TeamId = team.Id,
            Title = title,
            Venue = venue,
            StartsAt = TeamTime.ConvertToUtc(gameDate, time, team.TimeZoneId!),
            Capacity = capacity,
            Price = price,
            CreatedAt = _clock.GetUtcNow(),
            CreatedByPlayerId = actor.PlayerId,
        };
        _db.Games.Add(game);
        await _db.SaveChangesAsync(ct);

        return game;
    }

    public Task<Result<Unit>> SetTitleAsync(Game game, Team team, Actor actor, string title, CancellationToken ct) =>
        ApplyAsync(team, actor, () => game.Title = title, ct);

    public Task<Result<Unit>> SetVenueAsync(Game game, Team team, Actor actor, string venue, CancellationToken ct) =>
        ApplyAsync(team, actor, () => game.Venue = venue, ct);

    public async Task<Result<IReadOnlyList<Signup>>> SetCapacityAsync(
        Game game,
        Team team,
        Actor actor,
        int capacity,
        CancellationToken ct
    )
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

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

    public Task<Result<Unit>> SetPriceAsync(Game game, Team team, Actor actor, decimal? price, CancellationToken ct) =>
        ApplyAsync(team, actor, () => game.Price = price, ct);

    public Task<Result<Unit>> SetNotesAsync(Game game, Team team, Actor actor, string? notes, CancellationToken ct) =>
        ApplyAsync(team, actor, () => game.Notes = notes, ct);

    public Task<Result<Unit>> SetTagsAsync(
        Game game,
        Team team,
        Actor actor,
        List<string> tags,
        CancellationToken ct
    ) => ApplyAsync(team, actor, () => game.Tags = tags, ct);

    public async Task<Result<Unit>> SetStartTimeAsync(
        Game game,
        Team team,
        Actor actor,
        TimeOnly time,
        CancellationToken ct
    )
    {
        var ready = await EnsureCaptainOfConfiguredTeamAsync(team, actor, ct);
        if (!ready.IsSuccess)
        {
            return ready.Error;
        }

        // The date is the game's existing local one — only the time of day is being moved.
        var timeZoneId = team.TimeZoneId!;
        var localDate = DateOnly.FromDateTime(TeamTime.ConvertToLocal(game.StartsAt, timeZoneId).Date);
        game.StartsAt = TeamTime.ConvertToUtc(localDate, time, timeZoneId);
        await _db.SaveChangesAsync(ct);

        return new Unit();
    }

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

    // Anything that computes an instant needs both answers before it can run.
    private async Task<Result<Unit>> EnsureCaptainOfConfiguredTeamAsync(Team team, Actor actor, CancellationToken ct)
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        return allowed.IsSuccess ? TeamGuard.EnsureTimeZoneConfigured(team) : allowed.Error;
    }

    public async Task<Result<IReadOnlyList<Membership>>> LoadPlayingMembersAsync(
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

    public async Task<Result<IReadOnlyList<MemberSignupStatus>>> LoadMemberStatusesAsync(
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

    public async Task<Result<Unit>> TryNudgeAsync(Game game, Team team, Actor actor, CancellationToken ct)
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        var now = _clock.GetUtcNow();
        if (game.LastNudgedAt is { } lastNudgedAt && now < lastNudgedAt + NudgeCooldown)
        {
            return new BusinessError.NudgeOnCooldown();
        }

        game.LastNudgedAt = now;
        await _db.SaveChangesAsync(ct);

        return new Unit();
    }

    public async Task<Result<Unit>> EnsureCanManageAsync(Team team, Actor actor, CancellationToken ct) =>
        await _guard.RequireCaptainAsync(team, actor, ct);

    public async Task<Result<Unit>> DeclineAsync(Game game, Team team, Actor actor, CancellationToken ct)
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        game.DeclinedAt = _clock.GetUtcNow();

        AuditRecorder.Record(_db, game.TeamId, game.Id, actor.PlayerId, AuditActions.GameDeclined, new { }, _clock);

        await _db.SaveChangesAsync(ct);

        return new Unit();
    }

    public async Task<Result<Unit>> FinishAsync(Game game, Team team, Actor? actor, CancellationToken ct)
    {
        if (actor is { } captain)
        {
            var allowed = await _guard.RequireCaptainAsync(team, captain, ct);
            if (!allowed.IsSuccess)
            {
                return allowed.Error;
            }
        }

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
                    CreatedAt = now,
                }
            );
        }

        game.FinishedAt = now;

        AuditRecorder.Record(_db, game.TeamId, game.Id, actor?.PlayerId, AuditActions.GameFinished, new { }, _clock);

        await _db.SaveChangesAsync(ct);

        return new Unit();
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
