using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Data;

// Keeps Player.CalendarVersion and Game.Revision current, in the same SaveChanges — and so
// the same transaction — as the change that invalidated them. That mirrors what CLAUDE.md's
// Conventions already require of the notifications table and of AuditEntry.
//
// Doing it here rather than at each of the fifteen service methods that save a signup or a
// game is the one place this feature chooses implicit over explicit against STYLE.md's grain.
// The reasoning is the same as the one behind strongly-typed ids: a missed call site produces
// a feed that is silently stale until some unrelated change happens to fix it, and one
// interceptor that cannot be forgotten beats fifteen calls that can. See docs/CALENDAR.md §5,
// which also names the explicit form as the rejected alternative if this proves too clever to
// live with.
//
// Nothing happens for a player who has never asked for a feed. The bump is filtered to rows
// with a token, so a team where nobody subscribes pays one query per save and writes nothing.
public sealed class CalendarVersionInterceptor : SaveChangesInterceptor
{
    // Everything a VEVENT renders from the game itself. A change to any of them revises the
    // event; a change to anything else about the game (LastNudgedAt, the announcement's
    // message id) does not, and must not, or every nudge would look like a rescheduling.
    private static readonly string[] FeedVisibleGameProperties =
    [
        nameof(Game.Title),
        nameof(Game.Venue),
        nameof(Game.StartsAt),
        nameof(Game.Capacity),
        nameof(Game.Price),
        nameof(Game.Notes),
        nameof(Game.Tags),
        nameof(Game.FinishedAt),
        nameof(Game.DeclinedAt),
    ];

    // Everything below simply sets properties on tracked entities. EF runs its own change
    // detection after this interceptor, so those writes are picked up and land in the same
    // batch — no need to mark anything modified by hand, and the tests fail loudly if that
    // ordering ever stops holding.
    private readonly TimeProvider _clock;

    public CalendarVersionInterceptor(TimeProvider clock) => _clock = clock;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        if (eventData.Context is QuizrDb db)
        {
            await BumpAsync(db, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private async Task BumpAsync(QuizrDb db, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        // Who is affected, in the three shapes the answer arrives in: a game whose whole
        // roster is now rendering something different, a team whose name or zone every
        // member's feed prints, and a player named outright.
        var gameIds = new HashSet<GameId>();
        var teamIds = new HashSet<TeamId>();
        var playerIds = new HashSet<PlayerId>();

        // Materialized before touching anything: setting a property below marks entries
        // modified, and mutating the change tracker while enumerating it throws.
        var entries = db.ChangeTracker.Entries().ToList();

        foreach (var entry in entries)
        {
            switch (entry.Entity)
            {
                case Signup signup when entry.State is EntityState.Added or EntityState.Modified:
                    gameIds.Add(signup.GameId);

                    // A signup being inserted right now is not in the database yet, so the
                    // roster query below cannot see it — and the person joining is exactly
                    // the one whose feed gains an event. Their inviter comes too: a guest
                    // occupies a seat and shows up in the inviter's own event description.
                    AddIfPresent(playerIds, signup.PlayerId);
                    AddIfPresent(playerIds, signup.InvitedByPlayerId);
                    break;

                case Participation participation when entry.State is EntityState.Added or EntityState.Modified:
                    gameIds.Add(participation.GameId);
                    AddIfPresent(playerIds, participation.PlayerId);
                    break;

                case Game game when entry.State is EntityState.Added:
                    // A new game revises nothing and has no signups yet, so nobody is bumped.
                    // DTSTAMP still needs a value, and the moment the game was created is the
                    // truthful one.
                    game.RevisedAt = game.CreatedAt;
                    break;

                case Game game when entry.State is EntityState.Modified && IsModified(entry, FeedVisibleGameProperties):
                    gameIds.Add(game.Id);
                    game.Revision++;
                    game.RevisedAt = now;
                    break;

                case Team team
                    when entry.State is EntityState.Modified
                        && IsModified(entry, nameof(Team.TimeZoneId), nameof(Team.Name)):
                    teamIds.Add(team.Id);
                    break;

                case Player player when entry.State is EntityState.Modified && IsModified(entry, nameof(Player.Locale)):
                    // The feed renders in the person's own language, so their own choice of
                    // it is a change to their own feed and nobody else's.
                    playerIds.Add(player.Id);
                    break;
            }
        }

        if (gameIds.Count > 0)
        {
            // Everyone holding a live signup, playing or reserve alike: a seat taken or freed
            // moves everyone below it across the split, and the split is what each person's
            // own event says about them.
            var fromGames = await db
                .Signups.AsNoTracking()
                .Where(s => gameIds.Contains(s.GameId) && s.CancelledAt == null && s.PlayerId != null)
                .Select(s => s.PlayerId)
                .Distinct()
                .ToListAsync(ct);

            foreach (var playerId in fromGames)
            {
                AddIfPresent(playerIds, playerId);
            }
        }

        if (teamIds.Count > 0)
        {
            var fromTeams = await db
                .Memberships.AsNoTracking()
                .Where(m => teamIds.Contains(m.TeamId))
                .Select(m => m.PlayerId)
                .Distinct()
                .ToListAsync(ct);

            playerIds.UnionWith(fromTeams);
        }

        if (playerIds.Count == 0)
        {
            return;
        }

        // The token filter is what keeps this free for everyone who does not use the feature:
        // a version nobody can read is a version not worth writing.
        var subscribers = await db
            .Players.Where(p => playerIds.Contains(p.Id) && p.CalendarToken != null)
            .ToListAsync(ct);

        foreach (var player in subscribers)
        {
            player.CalendarVersion++;
        }
    }

    private static bool IsModified(EntityEntry entry, params string[] propertyNames) =>
        propertyNames.Any(name => entry.Property(name).IsModified);

    private static void AddIfPresent(HashSet<PlayerId> playerIds, PlayerId? playerId)
    {
        if (playerId is { } id)
        {
            playerIds.Add(id);
        }
    }
}
