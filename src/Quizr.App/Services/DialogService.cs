using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Services;

// The multi-step-flow store: one active dialog per (chat, player), in Postgres so a reply
// after a restart still resolves it.
//
// It is a service rather than a set of helpers on the router because most captain flows are
// not a service call at all — picking a date, editing a field, confirming — they only move
// dialog state along. Authorization for those steps has to live somewhere, and STYLE.md says
// not the dispatcher, so it lives on the load: reaching a captain-only dialog at all is the
// captain-only operation. That keeps a non-captain tapping a stale button on somebody else's
// keyboard getting "only a captain can do that" rather than a silent no-op, which is what
// leaving it to the (chat, player) lookup to miss would have produced.
public interface IDialogService
{
    Task<DialogState?> LoadAsync(TelegramChatId chatId, PlayerId playerId, CancellationToken ct);

    Task<DialogState?> LoadOfKindAsync(TelegramChatId chatId, PlayerId playerId, string kind, CancellationToken ct);

    Task<Result<DialogState?>> LoadForCaptainAsync(Team team, Actor actor, CancellationToken ct);

    Task<Result<DialogState?>> LoadForCaptainAsync(Team team, Actor actor, string kind, CancellationToken ct);

    Task<DialogState> StartAsync<TData>(
        TeamId teamId,
        PlayerId playerId,
        TelegramChatId chatId,
        string kind,
        TData data,
        CancellationToken ct
    );

    Task<Result<DialogState>> StartForCaptainAsync<TData>(
        Team team,
        Actor actor,
        string kind,
        TData data,
        CancellationToken ct
    );

    Task SaveDataAsync<TData>(DialogState dialog, TData data, CancellationToken ct);

    Task SetPromptMessageAsync(DialogState dialog, TelegramMessageId messageId, CancellationToken ct);

    Task ClearAsync(DialogState dialog, CancellationToken ct);
}

public sealed class DialogService : IDialogService
{
    private readonly QuizrDb _db;
    private readonly TeamGuard _guard;
    private readonly TimeProvider _clock;

    public DialogService(QuizrDb db, TeamGuard guard, TimeProvider clock)
    {
        _db = db;
        _guard = guard;
        _clock = clock;
    }

    public async Task<DialogState?> LoadAsync(TelegramChatId chatId, PlayerId playerId, CancellationToken ct) =>
        await _db.DialogStates.SingleOrDefaultAsync(d => d.ChatId == chatId && d.PlayerId == playerId, ct);

    public async Task<DialogState?> LoadOfKindAsync(
        TelegramChatId chatId,
        PlayerId playerId,
        string kind,
        CancellationToken ct
    ) =>
        await _db.DialogStates.SingleOrDefaultAsync(
            d => d.ChatId == chatId && d.PlayerId == playerId && d.Kind == kind,
            ct
        );

    public async Task<Result<DialogState?>> LoadForCaptainAsync(Team team, Actor actor, CancellationToken ct)
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        return await LoadAsync(team.ChatId, actor.PlayerId, ct);
    }

    public async Task<Result<DialogState?>> LoadForCaptainAsync(
        Team team,
        Actor actor,
        string kind,
        CancellationToken ct
    )
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        return await LoadOfKindAsync(team.ChatId, actor.PlayerId, kind, ct);
    }

    // One dialog per (chat, player) — a stray earlier one is replaced rather than left to
    // collide on the unique index.
    public async Task<DialogState> StartAsync<TData>(
        TeamId teamId,
        PlayerId playerId,
        TelegramChatId chatId,
        string kind,
        TData data,
        CancellationToken ct
    )
    {
        var existing = await LoadAsync(chatId, playerId, ct);
        if (existing is not null)
        {
            _db.DialogStates.Remove(existing);
        }

        var now = _clock.GetUtcNow();
        var dialog = new DialogState
        {
            TeamId = teamId,
            PlayerId = playerId,
            ChatId = chatId,
            Kind = kind,
            Step = "",
            Data = JsonSerializer.Serialize(data),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.DialogStates.Add(dialog);
        await _db.SaveChangesAsync(ct);

        return dialog;
    }

    public async Task<Result<DialogState>> StartForCaptainAsync<TData>(
        Team team,
        Actor actor,
        string kind,
        TData data,
        CancellationToken ct
    )
    {
        var allowed = await _guard.RequireCaptainAsync(team, actor, ct);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        return await StartAsync(team.Id, actor.PlayerId, team.ChatId, kind, data, ct);
    }

    // Overwrites an already-active dialog's Data in place (same row, new UpdatedAt) — used at
    // every step of a multi-step captain flow instead of removing and re-adding.
    public async Task SaveDataAsync<TData>(DialogState dialog, TData data, CancellationToken ct)
    {
        dialog.Data = JsonSerializer.Serialize(data);
        dialog.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetPromptMessageAsync(DialogState dialog, TelegramMessageId messageId, CancellationToken ct)
    {
        dialog.MessageId = messageId;
        dialog.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    public async Task ClearAsync(DialogState dialog, CancellationToken ct)
    {
        _db.DialogStates.Remove(dialog);
        await _db.SaveChangesAsync(ct);
    }
}
