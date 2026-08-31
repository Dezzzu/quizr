using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot.Types;

namespace Quizr.App.Services;

// Players and memberships are created lazily on first interaction, so the bot works for
// someone who has never spoken before — CLAUDE.md's Team bootstrap section.
public sealed class PlayerBootstrapService
{
    private readonly QuizrDb _db;
    private readonly TimeProvider _clock;

    public PlayerBootstrapService(QuizrDb db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<Player> GetOrCreateAsync(User telegramUser, CancellationToken ct)
    {
        var telegramUserId = new TelegramUserId(telegramUser.Id);
        var player = await _db.Players.SingleOrDefaultAsync(p => p.TelegramUserId == telegramUserId, ct);
        if (player is not null)
        {
            return player;
        }

        player = new Player
        {
            TelegramUserId = telegramUserId,
            DisplayName = FormatDisplayName(telegramUser),
            Username = telegramUser.Username,
            Locale = null,
            DmEnabled = false,
            CreatedAt = _clock.GetUtcNow(),
        };

        _db.Players.Add(player);
        await _db.SaveChangesAsync(ct);
        return player;
    }

    // A bot cannot message anyone who has not started it (CLAUDE.md), so DmEnabled is what
    // SchedulerService checks before sending any DM reminder. Nothing ever wrote it: it was set
    // false at creation and never revisited, which left the whole Dm channel unreachable — a
    // person could pick it in /myreminders and simply never hear from the bot again.
    //
    // Two things prove a DM would land, and both flip it: a message the person sent in their
    // own chat with the bot, and Telegram reporting them unblocking it. Blocking flips it back.
    public async Task SetDmEnabledAsync(Player player, bool enabled, CancellationToken ct)
    {
        if (player.DmEnabled == enabled)
        {
            return;
        }

        player.DmEnabled = enabled;
        await _db.SaveChangesAsync(ct);
    }

    public async Task EnsureMembershipAsync(TeamId teamId, PlayerId playerId, CancellationToken ct)
    {
        var exists = await _db.Memberships.AnyAsync(m => m.TeamId == teamId && m.PlayerId == playerId, ct);
        if (exists)
        {
            return;
        }

        _db.Memberships.Add(
            new Membership
            {
                TeamId = teamId,
                PlayerId = playerId,
                JoinedAt = _clock.GetUtcNow(),
            }
        );

        await _db.SaveChangesAsync(ct);
    }

    private static string FormatDisplayName(User user) =>
        user.LastName is null ? user.FirstName : $"{user.FirstName} {user.LastName}";
}
