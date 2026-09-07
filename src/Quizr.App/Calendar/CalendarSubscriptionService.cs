using Quizr.App.Data;
using Quizr.Domain.Entities;

namespace Quizr.App.Calendar;

// The write side of the calendar feed: who has a link, and what happens when they want a new
// one. CalendarFeedService is the read side.
//
// Nothing here returns a Result — none of it can fail for a business reason. There is no
// permission to check, since a person is only ever acting on their own subscription, and no
// state that makes issuing or revoking invalid. An interface all the same, because this is an
// application service and the mini app's own settings screen will call exactly these three
// methods (STYLE.md).
public interface ICalendarSubscriptionService
{
    // The token they already have, or a new one. Issued lazily on first use, the same way
    // players and memberships are.
    Task<string> IssueAsync(Player player, CancellationToken ct);

    // A new token, and the old one stops working immediately, on every device, with no grace
    // period. That is the point of it rather than a shortcoming: the reason to rotate is that
    // the old link got somewhere it shouldn't have.
    Task<string> RotateAsync(Player player, CancellationToken ct);

    Task RevokeAsync(Player player, CancellationToken ct);
}

public sealed class CalendarSubscriptionService : ICalendarSubscriptionService
{
    private readonly QuizrDb _db;
    private readonly TimeProvider _clock;

    public CalendarSubscriptionService(QuizrDb db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<string> IssueAsync(Player player, CancellationToken ct) =>
        player.CalendarToken ?? await SetTokenAsync(player, CalendarToken.Generate(), ct);

    public Task<string> RotateAsync(Player player, CancellationToken ct) =>
        SetTokenAsync(player, CalendarToken.Generate(), ct);

    public async Task RevokeAsync(Player player, CancellationToken ct)
    {
        // Nulled rather than kept with a flag beside it, so the endpoint's single indexed
        // lookup is also the whole of the revocation check — there is no second condition to
        // forget. Invariant 7 is about the audit trail of who did what; a credential is not
        // history, and destroying the old one is the operation.
        player.CalendarToken = null;
        player.CalendarTokenIssuedAt = null;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<string> SetTokenAsync(Player player, string token, CancellationToken ct)
    {
        player.CalendarToken = token;
        player.CalendarTokenIssuedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        return token;
    }
}
