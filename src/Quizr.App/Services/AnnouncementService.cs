using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Rendering;
using Quizr.App.Telegram;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Services;

// CLAUDE.md's rule everything else follows: the database is the source of truth, chat
// messages are generated views. Every signup mutation ends with a call here to rewrite the
// announcement from what's actually in the database — never a targeted edit of the old text.
public sealed class AnnouncementService
{
    private readonly QuizrDb _db;
    private readonly IMessageSender _sender;
    private readonly IStrings _strings;

    public AnnouncementService(QuizrDb db, IMessageSender sender, IStrings strings)
    {
        _db = db;
        _sender = sender;
        _strings = strings;
    }

    public async Task<TelegramMessageId> PostAsync(Game game, Team team, CancellationToken ct)
    {
        var (text, keyboard) = await RenderAsync(game, team, ct);
        return await _sender.SendAsync(team.ChatId, text, keyboard, ct);
    }

    public async Task RefreshAsync(Game game, Team team, CancellationToken ct)
    {
        if (game.AnnouncementMessageId is not { } messageId)
        {
            return;
        }

        var (text, keyboard) = await RenderAsync(game, team, ct);
        await _sender.EditAsync(team.ChatId, messageId, text, keyboard, ct);
    }

    // The announcement's half of invariant 12's "restore it silently", and the same shape as
    // BoardService.RefreshAsync on purpose: edit it if it's still there, repost it from the
    // database if it isn't. CLAUDE.md already promises a deleted post costs nothing; until
    // this existed that promise only held for the Board, because the one thing that could
    // repost an announcement was an edit failing, and every edit is triggered by a button on
    // the message that is gone.
    //
    // Immediate rather than debounced: this runs from a scheduler tick or a command, never in
    // the burst of signups the debouncer exists to coalesce. Returns whether it had to repost,
    // so /restoreannouncements can say how many were actually missing.
    public async Task<bool> RestoreAsync(Game game, Team team, CancellationToken ct)
    {
        var (text, keyboard) = await RenderAsync(game, team, ct);

        if (
            game.AnnouncementMessageId is { } messageId
            && await _sender.TryEditImmediatelyAsync(team.ChatId, messageId, text, keyboard, ct)
        )
        {
            return false;
        }

        // Also covers a game that never got an announcement at all — PostAsync only runs at
        // creation, so before this a game whose first post failed stayed silent forever.
        game.AnnouncementMessageId = await _sender.SendAsync(team.ChatId, text, keyboard, ct);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<(string Text, InlineKeyboardMarkup? Keyboard)> RenderAsync(
        Game game,
        Team team,
        CancellationToken ct
    )
    {
        var signups = await _db
            .Signups.AsNoTracking()
            .Include(s => s.Player)
            .Include(s => s.InvitedByPlayer)
            .Where(s => s.GameId == game.Id && s.CancelledAt == null)
            .ToListAsync(ct);
        var roster = Roster.Split(signups, game.Capacity);

        var strings = _strings.For(team.Locale);

        return (
            AnnouncementRenderer.RenderText(
                game,
                roster,
                team.TimeZoneId!,
                strings,
                await FranchiseNameAsync(game, ct)
            ),
            AnnouncementRenderer.RenderKeyboard(game, strings)
        );
    }

    // Looked up rather than read off game.Franchise, which is the one place this differs from
    // the Board. The Board owns the query its games come from and can .Include the navigation;
    // here the Game is a parameter handed over by a dozen call sites — the router, the
    // scheduler, every signup mutation — and on Game a null navigation also means "not
    // Included". Trusting it would make the franchise prefix appear or vanish depending on
    // which call site rewrote the announcement, which is worse than one indexed lookup by
    // primary key. A one-off game does no query at all.
    private async Task<string?> FranchiseNameAsync(Game game, CancellationToken ct) =>
        game.FranchiseId is { } franchiseId
            ? await _db.Franchises.AsNoTracking().Where(f => f.Id == franchiseId).Select(f => f.Name).SingleAsync(ct)
            : null;
}
