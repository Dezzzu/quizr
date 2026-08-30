using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;

namespace Quizr.App.Services;

// Roster mutation only — no Telegram calls. STACK.md: "both front doors call the same
// application services" in phase 2, so sending messages and rewriting the announcement is
// the caller's job, done after this returns.
public interface ISignupService
{
    Task<Result<Signup>> JoinAsync(Game game, PlayerId playerId, CancellationToken ct);

    Task<Result<Signup>> BringGuestAsync(Game game, PlayerId inviterId, CancellationToken ct);

    Task<Result<Signup>> NameGuestAsync(
        SignupId guestSignupId,
        PlayerId requestingPlayerId,
        string name,
        CancellationToken ct
    );

    Task<Result<DropOutcome>> DropAsync(Game game, PlayerId playerId, CancellationToken ct);

    Task<Result<GuestChoiceOutcome>> ResolveGuestChoiceAsync(
        SignupId guestSignupId,
        PlayerId requestingPlayerId,
        bool keep,
        CancellationToken ct
    );

    // Un-inviting a guest at any time — not just the post-drop cascade, and not
    // conditional on them being named, unlike ResolveGuestChoiceAsync.
    Task<Result<GuestRemovalOutcome>> RemoveGuestAsync(
        SignupId guestSignupId,
        PlayerId requestingPlayerId,
        CancellationToken ct
    );

    Task<IReadOnlyList<Signup>> LoadLiveGuestsAsync(Game game, PlayerId inviterId, CancellationToken ct);
}

// Invariant 5's cascade: unnamed guests cancel automatically with the inviter; named ones
// still need a keep-or-drop decision from whoever invited them. NewlyPromoted holds only
// the promotions this call actually got to record — see NotificationRecorder.
public sealed record DropOutcome(
    IReadOnlyList<Signup> AutoCancelledGuests,
    IReadOnlyList<Signup> NamedGuestsNeedingChoice,
    IReadOnlyList<Signup> NewlyPromoted
);

public sealed record GuestChoiceOutcome(Signup Guest, bool Kept, IReadOnlyList<Signup> NewlyPromoted);

public sealed record GuestRemovalOutcome(Signup Guest, IReadOnlyList<Signup> NewlyPromoted);

public sealed class SignupService : ISignupService
{
    private readonly QuizrDb _db;
    private readonly TimeProvider _clock;

    public SignupService(QuizrDb db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<Result<Signup>> JoinAsync(Game game, PlayerId playerId, CancellationToken ct)
    {
        var open = RegistrationGuard(game);
        if (!open.IsSuccess)
        {
            return open.Error;
        }

        var alreadyIn = await _db.Signups.AnyAsync(
            s => s.GameId == game.Id && s.PlayerId == playerId && s.CancelledAt == null,
            ct
        );
        if (alreadyIn)
        {
            return new BusinessError.AlreadySignedUp();
        }

        var signup = new Signup
        {
            GameId = game.Id,
            PlayerId = playerId,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.Signups.Add(signup);
        await _db.SaveChangesAsync(ct);

        return signup;
    }

    public async Task<Result<Signup>> BringGuestAsync(Game game, PlayerId inviterId, CancellationToken ct)
    {
        var open = RegistrationGuard(game);
        if (!open.IsSuccess)
        {
            return open.Error;
        }

        // Anonymous by default (CLAUDE.md) — naming is a follow-up, not a precondition for
        // holding the seat, so the guest's queue position is secured immediately.
        var signup = new Signup
        {
            GameId = game.Id,
            InvitedByPlayerId = inviterId,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.Signups.Add(signup);
        await _db.SaveChangesAsync(ct);

        return signup;
    }

    public async Task<Result<Signup>> NameGuestAsync(
        SignupId guestSignupId,
        PlayerId requestingPlayerId,
        string name,
        CancellationToken ct
    )
    {
        var guest = await _db.Signups.SingleOrDefaultAsync(s => s.Id == guestSignupId, ct);
        if (guest is null || guest.IsMember || guest.InvitedByPlayerId != requestingPlayerId)
        {
            return new BusinessError.NotYourGuest();
        }

        if (guest.IsCancelled)
        {
            return new BusinessError.GuestAlreadyResolved();
        }

        guest.GuestName = name;
        await _db.SaveChangesAsync(ct);

        return guest;
    }

    public async Task<Result<DropOutcome>> DropAsync(Game game, PlayerId playerId, CancellationToken ct)
    {
        var before = await LoadRosterAsync(game, ct);

        var signup = await _db.Signups.SingleOrDefaultAsync(
            s => s.GameId == game.Id && s.PlayerId == playerId && s.CancelledAt == null,
            ct
        );
        if (signup is null)
        {
            return new BusinessError.NotSignedUp();
        }

        var now = _clock.GetUtcNow();
        signup.CancelledAt = now;
        signup.CancelledByPlayerId = playerId;

        var allSignups = await _db.Signups.Where(s => s.GameId == game.Id).ToListAsync(ct);
        var cascade = GuestCascade.ForInviterDrop(allSignups, playerId);
        foreach (var guest in cascade.AutoCancel)
        {
            guest.CancelledAt = now;
            guest.CancelledByPlayerId = playerId;
        }

        await _db.SaveChangesAsync(ct);

        var after = await LoadRosterAsync(game, ct);
        var promoted = await RecordPromotionsAsync(before, after, ct);

        return new DropOutcome(cascade.AutoCancel, cascade.NeedsChoice, promoted);
    }

    public async Task<Result<GuestChoiceOutcome>> ResolveGuestChoiceAsync(
        SignupId guestSignupId,
        PlayerId requestingPlayerId,
        bool keep,
        CancellationToken ct
    )
    {
        var guest = await _db.Signups.SingleOrDefaultAsync(s => s.Id == guestSignupId, ct);
        if (guest is null || guest.InvitedByPlayerId != requestingPlayerId)
        {
            return new BusinessError.NotYourGuest();
        }

        if (guest.IsCancelled || guest.GuestName is null)
        {
            return new BusinessError.GuestAlreadyResolved();
        }

        var game = await _db.Games.SingleAsync(g => g.Id == guest.GameId, ct);
        var before = await LoadRosterAsync(game, ct);

        if (keep)
        {
            // A team guest has no owner — that's what "team guest" means.
            guest.InvitedByPlayerId = null;
        }
        else
        {
            guest.CancelledAt = _clock.GetUtcNow();
            guest.CancelledByPlayerId = requestingPlayerId;
        }

        await _db.SaveChangesAsync(ct);

        IReadOnlyList<Signup> promoted = [];
        if (!keep)
        {
            var after = await LoadRosterAsync(game, ct);
            promoted = await RecordPromotionsAsync(before, after, ct);
        }

        return new GuestChoiceOutcome(guest, keep, promoted);
    }

    public async Task<Result<GuestRemovalOutcome>> RemoveGuestAsync(
        SignupId guestSignupId,
        PlayerId requestingPlayerId,
        CancellationToken ct
    )
    {
        var guest = await _db.Signups.SingleOrDefaultAsync(s => s.Id == guestSignupId, ct);
        if (guest is null || guest.InvitedByPlayerId != requestingPlayerId)
        {
            return new BusinessError.NotYourGuest();
        }

        if (guest.IsCancelled)
        {
            return new BusinessError.GuestAlreadyResolved();
        }

        var game = await _db.Games.SingleAsync(g => g.Id == guest.GameId, ct);
        var before = await LoadRosterAsync(game, ct);

        guest.CancelledAt = _clock.GetUtcNow();
        guest.CancelledByPlayerId = requestingPlayerId;
        await _db.SaveChangesAsync(ct);

        var after = await LoadRosterAsync(game, ct);
        var promoted = await RecordPromotionsAsync(before, after, ct);

        return new GuestRemovalOutcome(guest, promoted);
    }

    public async Task<IReadOnlyList<Signup>> LoadLiveGuestsAsync(Game game, PlayerId inviterId, CancellationToken ct) =>
        await _db
            .Signups.AsNoTracking()
            .Where(s => s.GameId == game.Id && s.InvitedByPlayerId == inviterId && s.CancelledAt == null)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

    private async Task<RosterSplit> LoadRosterAsync(Game game, CancellationToken ct)
    {
        var signups = await _db
            .Signups.AsNoTracking()
            .Where(s => s.GameId == game.Id && s.CancelledAt == null)
            .ToListAsync(ct);

        return Roster.Split(signups, game.Capacity);
    }

    private async Task<IReadOnlyList<Signup>> RecordPromotionsAsync(
        RosterSplit before,
        RosterSplit after,
        CancellationToken ct
    )
    {
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

    private static Result<Unit> RegistrationGuard(Game game)
    {
        if (game.IsFinished)
        {
            return new BusinessError.GameAlreadyFinished();
        }

        return game.IsDeclined ? new BusinessError.RegistrationClosed() : new Unit();
    }
}
