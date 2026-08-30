using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Rendering;
using Quizr.App.Services;
using Quizr.App.Time;
using Quizr.App.Validation;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Quizr.Domain.Extensions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Game = Quizr.Domain.Entities.Game;

namespace Quizr.App.Telegram;

// Switches on update type and, for messages, on the parsed command. Scoped — one instance
// per update, sharing the DbContext UpdateDispatcher's scope created. See STACK.md: "Update
// dispatch — switch on update type, match callback-data prefixes to DI-resolved handlers."
public sealed class UpdateRouter
{
    private readonly QuizrDb _db;
    private readonly IMessageSender _sender;
    private readonly ITelegramBotClient _bot;
    private readonly IStrings _strings;
    private readonly TeamBootstrapService _teamBootstrap;
    private readonly PlayerBootstrapService _playerBootstrap;
    private readonly TeamGuard _teamGuard;
    private readonly ISignupService _signups;
    private readonly IFranchiseService _franchises;
    private readonly IGameService _games;
    private readonly IParticipationService _participations;
    private readonly AnnouncementService _announcements;
    private readonly BoardService _board;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpdateRouter> _logger;

    public UpdateRouter(
        QuizrDb db,
        IMessageSender sender,
        ITelegramBotClient bot,
        IStrings strings,
        TeamBootstrapService teamBootstrap,
        PlayerBootstrapService playerBootstrap,
        TeamGuard teamGuard,
        ISignupService signups,
        IFranchiseService franchises,
        IGameService games,
        IParticipationService participations,
        AnnouncementService announcements,
        BoardService board,
        TimeProvider clock,
        ILogger<UpdateRouter> logger
    )
    {
        _db = db;
        _sender = sender;
        _bot = bot;
        _strings = strings;
        _teamBootstrap = teamBootstrap;
        _playerBootstrap = playerBootstrap;
        _teamGuard = teamGuard;
        _signups = signups;
        _franchises = franchises;
        _games = games;
        _participations = participations;
        _announcements = announcements;
        _board = board;
        _clock = clock;
        _logger = logger;
    }

    public async Task RouteAsync(Update update, CancellationToken ct)
    {
        switch (update.Type)
        {
            case UpdateType.MyChatMember:
                await _teamBootstrap.HandleMyChatMemberAsync(update.MyChatMember!, ct);
                break;

            case UpdateType.ChatMember:
                // CLAUDE.md mentions using this to mark departures from Membership, which has
                // no such field yet and isn't part of M3 — subscribed via allowed_updates so
                // nothing has to change when that's built.
                _logger.LogDebug("Ignoring chat_member update for chat {ChatId}", update.ChatMember!.Chat.Id);
                break;

            case UpdateType.Message when update.Message?.Text is not null:
                await HandleMessageAsync(update.Message, ct);
                break;

            case UpdateType.CallbackQuery:
                await HandleCallbackQueryAsync(update.CallbackQuery!, ct);
                break;

            default:
                _logger.LogDebug("Ignoring update {UpdateId} of type {Type}", update.Id, update.Type);
                break;
        }
    }

    private async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        var chatId = new TelegramChatId(message.Chat.Id);
        var team = await _db.Teams.SingleOrDefaultAsync(t => t.ChatId == chatId, ct);

        Player? player = null;
        if (message.From is not null)
        {
            player = await _playerBootstrap.GetOrCreateAsync(message.From, ct);
            if (team is not null)
            {
                await _playerBootstrap.EnsureMembershipAsync(team.Id, player.Id, ct);
            }
        }

        if (player is not null && !message.Text!.StartsWith('/'))
        {
            var dialog = await _db.DialogStates.SingleOrDefaultAsync(
                d => d.ChatId == chatId && d.PlayerId == player.Id,
                ct
            );
            if (dialog is not null)
            {
                await HandleDialogReplyAsync(dialog, message, ct);
                return;
            }
        }

        var (command, argument) = CommandText.Parse(message.Text!);

        switch (command)
        {
            case "/start":
                var locale = LocaleResolver.Resolve(player?.Locale, message.From?.LanguageCode, team?.Locale ?? "en");
                await _sender.SendAsync(chatId, _strings.For(locale).Text("Start.Greeting"), null, ct);
                break;

            case "/settimezone" when team is not null && player is not null && message.From is not null:
                await HandleSetTimeZoneAsync(
                    team,
                    player.Id,
                    chatId,
                    new TelegramUserId(message.From.Id),
                    argument,
                    ct
                );
                break;

            case "/setlanguage" when team is not null && player is not null && message.From is not null:
                await HandleSetLanguageAsync(
                    team,
                    player.Id,
                    chatId,
                    new TelegramUserId(message.From.Id),
                    argument,
                    ct
                );
                break;

            case "/mylanguage" when player is not null:
                await HandleSetMyLanguageAsync(player, chatId, argument, ct);
                break;

            case "/setreminders" when team is not null && player is not null && message.From is not null:
                await HandleSetRemindersAsync(
                    team,
                    player.Id,
                    chatId,
                    new TelegramUserId(message.From.Id),
                    argument,
                    ct
                );
                break;

            case "/newgame" when team is not null && player is not null && message.From is not null:
                await HandleNewGameCommandAsync(team, player.Id, chatId, new TelegramUserId(message.From.Id), ct);
                break;

            case "/newfranchise" when team is not null && player is not null && message.From is not null:
                await HandleNewFranchiseCommandAsync(team, player.Id, chatId, new TelegramUserId(message.From.Id), ct);
                break;

            case "/editfranchise" when team is not null && player is not null && message.From is not null:
                await HandleEditFranchiseCommandAsync(team, player.Id, chatId, new TelegramUserId(message.From.Id), ct);
                break;

            case "/editgame" when team is not null && player is not null && message.From is not null:
                await HandleEditGameCommandAsync(team, player.Id, chatId, new TelegramUserId(message.From.Id), ct);
                break;

            case "/myreminders" when team is not null && player is not null:
                await HandleMyRemindersCommandAsync(team, player.Id, chatId, ct);
                break;

            case "/managecaptains" when team is not null && player is not null && message.From is not null:
                await HandleManageCaptainsCommandAsync(
                    team,
                    player.Id,
                    chatId,
                    new TelegramUserId(message.From.Id),
                    ct
                );
                break;
        }
    }

    private async Task HandleSetTimeZoneAsync(
        Team team,
        PlayerId playerId,
        TelegramChatId chatId,
        TelegramUserId telegramUserId,
        string? argument,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

        if (!await _teamGuard.IsCaptainAsync(team.Id, playerId, chatId, telegramUserId, ct))
        {
            await _sender.SendAsync(chatId, strings.Text("NewGame.NotCaptain"), null, ct);
            return;
        }

        if (argument is null || !IsValidTimeZone(argument))
        {
            await _sender.SendAsync(
                chatId,
                strings.Text("Setup.TimeZoneInvalid", new { Input = argument ?? "" }),
                null,
                ct
            );
            return;
        }

        team.TimeZoneId = argument;
        await _db.SaveChangesAsync(ct);

        await _sender.SendAsync(chatId, strings.Text("Setup.TimeZoneSet", new { TimeZoneId = argument }), null, ct);

        // The team is now operational — invariant 12 says only the Board is ever pinned, so
        // it earns its pin from the moment the team can have games, not from the first one.
        await _board.RefreshAsync(team, ct);
    }

    // Group messages use the team's language (CLAUDE.md) — a captain setting, like the
    // timezone.
    private async Task HandleSetLanguageAsync(
        Team team,
        PlayerId playerId,
        TelegramChatId chatId,
        TelegramUserId telegramUserId,
        string? argument,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

        if (!await _teamGuard.IsCaptainAsync(team.Id, playerId, chatId, telegramUserId, ct))
        {
            await _sender.SendAsync(chatId, strings.Text("NewGame.NotCaptain"), null, ct);
            return;
        }

        if (argument is null || !LocaleResolver.IsSupported(argument))
        {
            await _sender.SendAsync(
                chatId,
                strings.Text("Setup.LanguageInvalid", new { Input = argument ?? "" }),
                null,
                ct
            );
            return;
        }

        team.Locale = argument;
        await _db.SaveChangesAsync(ct);

        // Rendered in the new locale, not the old one — the confirmation itself is the proof
        // the change took effect.
        await _sender.SendAsync(
            chatId,
            _strings.For(team.Locale).Text("Setup.LanguageSet", new { Locale = team.Locale }),
            null,
            ct
        );
    }

    // DMs and the app use the person's own language (CLAUDE.md) — this is the "explicit user
    // choice" step of the resolution chain; nothing else ever writes Player.Locale.
    private async Task HandleSetMyLanguageAsync(
        Player player,
        TelegramChatId chatId,
        string? argument,
        CancellationToken ct
    )
    {
        if (argument is null || !LocaleResolver.IsSupported(argument))
        {
            var strings = _strings.For(player.Locale ?? "en");
            await _sender.SendAsync(
                chatId,
                strings.Text("Setup.LanguageInvalid", new { Input = argument ?? "" }),
                null,
                ct
            );
            return;
        }

        player.Locale = argument;
        await _db.SaveChangesAsync(ct);

        await _sender.SendAsync(
            chatId,
            _strings.For(player.Locale).Text("Setup.MyLanguageSet", new { Locale = player.Locale }),
            null,
            ct
        );
    }

    // All three reminder slots at once, not one setting per command like /settimezone — they
    // read as one coherent schedule, and asking for all three together avoids the ambiguity
    // of a single-field edit leaving the other two silently unexplained.
    private async Task HandleSetRemindersAsync(
        Team team,
        PlayerId playerId,
        TelegramChatId chatId,
        TelegramUserId telegramUserId,
        string? argument,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

        if (!await _teamGuard.IsCaptainAsync(team.Id, playerId, chatId, telegramUserId, ct))
        {
            await _sender.SendAsync(chatId, strings.Text("NewGame.NotCaptain"), null, ct);
            return;
        }

        var parts = argument?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (
            parts.Length != 3
            || !FieldParsing.TryParseTime(parts[0], out var eveningBeforeAt, out _)
            || !FieldParsing.TryParseTime(parts[1], out var morningOfAt, out _)
            || !FieldParsing.TryParseDuration(parts[2], out var beforeStartLead, out _)
        )
        {
            await _sender.SendAsync(chatId, strings.Text("Setup.RemindersUsage"), null, ct);
            return;
        }

        team.EveningBeforeAt = eveningBeforeAt;
        team.MorningOfAt = morningOfAt;
        team.BeforeStartLead = beforeStartLead;
        await _db.SaveChangesAsync(ct);

        await _sender.SendAsync(
            chatId,
            strings.Text(
                "Setup.RemindersSet",
                new
                {
                    EveningBeforeAt = eveningBeforeAt,
                    MorningOfAt = morningOfAt,
                    BeforeStartLead = beforeStartLead,
                }
            ),
            null,
            ct
        );
    }

    private async Task HandleNewGameCommandAsync(
        Team team,
        PlayerId playerId,
        TelegramChatId chatId,
        TelegramUserId telegramUserId,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

        if (!await _teamGuard.IsCaptainAsync(team.Id, playerId, chatId, telegramUserId, ct))
        {
            await _sender.SendAsync(chatId, strings.Text("NewGame.NotCaptain"), null, ct);
            return;
        }

        var guard = TeamGuard.EnsureTimeZoneConfigured(team);
        if (!guard.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text("NewGame.NeedsTimeZone"), null, ct);
            return;
        }

        var franchises = await _db
            .Franchises.AsNoTracking()
            .Where(f => f.TeamId == team.Id && f.ArchivedAt == null)
            .ToListAsync(ct);

        await StartDialogAsync(
            team.Id,
            playerId,
            chatId,
            DialogKinds.NewGame,
            new NewGameDialogData(NewGameDialogData.ChooseBranch),
            ct
        );

        var keyboard = new List<IEnumerable<InlineKeyboardButton>>(
            franchises.Select(f =>
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        f.Name,
                        CallbackData.Format(CallbackData.PickFranchise, f.Id)
                    ),
                }
            )
        )
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("NewGame.OneOffButton"),
                    CallbackData.Format(CallbackData.OneOff, 0L)
                ),
            },
        };

        await _sender.SendAsync(chatId, strings.Text("NewGame.ChooseBranch"), new InlineKeyboardMarkup(keyboard), ct);
    }

    private async Task HandleNewFranchiseCommandAsync(
        Team team,
        PlayerId playerId,
        TelegramChatId chatId,
        TelegramUserId telegramUserId,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

        if (!await _teamGuard.IsCaptainAsync(team.Id, playerId, chatId, telegramUserId, ct))
        {
            await _sender.SendAsync(chatId, strings.Text("NewGame.NotCaptain"), null, ct);
            return;
        }

        await StartDialogAsync(
            team.Id,
            playerId,
            chatId,
            DialogKinds.NewFranchise,
            new NewFranchiseDialogData(NewFranchiseDialogData.AskName),
            ct
        );

        await _sender.SendAsync(chatId, strings.Text("Franchise.AskName"), null, ct);
    }

    private async Task HandleEditFranchiseCommandAsync(
        Team team,
        PlayerId playerId,
        TelegramChatId chatId,
        TelegramUserId telegramUserId,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

        if (!await _teamGuard.IsCaptainAsync(team.Id, playerId, chatId, telegramUserId, ct))
        {
            await _sender.SendAsync(chatId, strings.Text("NewGame.NotCaptain"), null, ct);
            return;
        }

        var franchises = await _db
            .Franchises.AsNoTracking()
            .Where(f => f.TeamId == team.Id && f.ArchivedAt == null)
            .ToListAsync(ct);

        if (franchises.Count == 0)
        {
            await _sender.SendAsync(chatId, strings.Text("Franchise.NoneYet"), null, ct);
            return;
        }

        await _sender.SendAsync(
            chatId,
            strings.Text("Franchise.PickToEdit"),
            FranchiseRenderer.RenderPicker(franchises, CallbackData.PickFranchise),
            ct
        );
    }

    private async Task HandleEditGameCommandAsync(
        Team team,
        PlayerId playerId,
        TelegramChatId chatId,
        TelegramUserId telegramUserId,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

        if (!await _teamGuard.IsCaptainAsync(team.Id, playerId, chatId, telegramUserId, ct))
        {
            await _sender.SendAsync(chatId, strings.Text("NewGame.NotCaptain"), null, ct);
            return;
        }

        var games = await _db
            .Games.AsNoTracking()
            .Where(g => g.TeamId == team.Id && g.FinishedAt == null && g.DeclinedAt == null)
            .OrderBy(g => g.StartsAt)
            .ToListAsync(ct);

        if (games.Count == 0)
        {
            await _sender.SendAsync(chatId, strings.Text("EditGame.NoneYet"), null, ct);
            return;
        }

        var keyboard = new InlineKeyboardMarkup(
            games.Select(g =>
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        g.Title,
                        CallbackData.Format(CallbackData.PickGameToEdit, g.Id)
                    ),
                }
            )
        );
        await _sender.SendAsync(chatId, strings.Text("EditGame.PickGame"), keyboard, ct);
    }

    // One dialog per (chat, player) — a stray earlier one is replaced rather than left to
    // collide on the unique index, same rule StartGuestNamingDialogAsync already follows.
    private async Task StartDialogAsync<TData>(
        TeamId teamId,
        PlayerId playerId,
        TelegramChatId chatId,
        string kind,
        TData data,
        CancellationToken ct
    )
    {
        var existing = await _db.DialogStates.SingleOrDefaultAsync(
            d => d.ChatId == chatId && d.PlayerId == playerId,
            ct
        );
        if (existing is not null)
        {
            _db.DialogStates.Remove(existing);
        }

        var now = _clock.GetUtcNow();
        _db.DialogStates.Add(
            new DialogState
            {
                TeamId = teamId,
                PlayerId = playerId,
                ChatId = chatId,
                Kind = kind,
                Step = "",
                Data = JsonSerializer.Serialize(data),
                CreatedAt = now,
                UpdatedAt = now,
            }
        );
        await _db.SaveChangesAsync(ct);
    }

    // Overwrites an already-active dialog's Data in place (same row, new UpdatedAt) — used at
    // every step of a multi-step captain flow instead of removing and re-adding.
    private async Task SaveDialogDataAsync<TData>(DialogState dialog, TData data, CancellationToken ct)
    {
        dialog.Data = JsonSerializer.Serialize(data);
        dialog.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    // --- Dialog replies (currently: naming a guest) ---

    private async Task HandleDialogReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        switch (dialog.Kind)
        {
            case DialogKinds.NameGuest:
                await HandleGuestNameReplyAsync(dialog, message, ct);
                break;

            case DialogKinds.NewFranchise:
                await HandleNewFranchiseReplyAsync(dialog, message, ct);
                break;

            case DialogKinds.EditFranchise:
                await HandleEditFranchiseReplyAsync(dialog, message, ct);
                break;

            case DialogKinds.NewGame:
                await HandleNewGameReplyAsync(dialog, message, ct);
                break;

            case DialogKinds.EditGame:
                await HandleEditGameReplyAsync(dialog, message, ct);
                break;

            case DialogKinds.AddVenuePlayer:
                await HandleAddVenuePlayerReplyAsync(dialog, message, ct);
                break;

            // Callback-only dialogs — no text-reply step exists for either, so a reply while
            // one is active is discarded exactly like a genuinely unrecognised kind.
            case DialogKinds.Nudge:
            case DialogKinds.ManagePlayers:
            default:
                _logger.LogWarning("Discarding a dialog with unrecognised kind {Kind}", dialog.Kind);
                _db.DialogStates.Remove(dialog);
                await _db.SaveChangesAsync(ct);
                break;
        }
    }

    // --- Franchise creation/editing ---

    private async Task HandleNewFranchiseReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<NewFranchiseDialogData>(dialog.Data)!;
        var chatId = new TelegramChatId(message.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.Id == dialog.TeamId, ct);
        var strings = _strings.For(team.Locale);
        var input = message.Text!;

        switch (data.Step)
        {
            case NewFranchiseDialogData.AskName:
                if (!FieldParsing.TryParseText(input, out var name, out var nameError))
                {
                    await _sender.SendAsync(chatId, strings.Text(nameError!), null, ct);
                    return;
                }

                await SaveDialogDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewFranchiseDialogData.AskVenue,
                        Name = name,
                    },
                    ct
                );
                await _sender.SendAsync(chatId, strings.Text("Franchise.AskVenue"), null, ct);
                break;

            case NewFranchiseDialogData.AskVenue:
                if (!FieldParsing.TryParseText(input, out var venue, out var venueError))
                {
                    await _sender.SendAsync(chatId, strings.Text(venueError!), null, ct);
                    return;
                }

                await SaveDialogDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewFranchiseDialogData.AskCapacity,
                        Venue = venue,
                    },
                    ct
                );
                await _sender.SendAsync(chatId, strings.Text("Franchise.AskCapacity"), null, ct);
                break;

            case NewFranchiseDialogData.AskCapacity:
                if (!FieldParsing.TryParseCapacity(input, out var capacity, out var capacityError))
                {
                    await _sender.SendAsync(chatId, strings.Text(capacityError!), null, ct);
                    return;
                }

                await SaveDialogDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewFranchiseDialogData.AskPrice,
                        Capacity = capacity,
                    },
                    ct
                );
                await _sender.SendAsync(chatId, strings.Text("Franchise.AskPrice"), null, ct);
                break;

            case NewFranchiseDialogData.AskPrice:
                if (!FieldParsing.TryParsePrice(input, out var price, out var priceError))
                {
                    await _sender.SendAsync(chatId, strings.Text(priceError!), null, ct);
                    return;
                }

                await SaveDialogDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewFranchiseDialogData.AskSchedule,
                        Price = price,
                    },
                    ct
                );
                await _sender.SendAsync(chatId, strings.Text("Franchise.AskSchedule"), null, ct);
                break;

            case NewFranchiseDialogData.AskSchedule:
                if (!FieldParsing.TryParseSchedule(input, team.Locale, out var schedule, out var scheduleError))
                {
                    await _sender.SendAsync(chatId, strings.Text(scheduleError!), null, ct);
                    return;
                }

                var franchise = await _franchises.CreateAsync(
                    team.Id,
                    data.Name!,
                    data.Venue!,
                    data.Capacity!.Value,
                    data.Price,
                    schedule,
                    ct
                );
                _db.DialogStates.Remove(dialog);
                await _db.SaveChangesAsync(ct);

                await _sender.SendAsync(chatId, strings.Text("Franchise.Created"), null, ct);
                await _sender.SendAsync(
                    chatId,
                    FranchiseRenderer.RenderSummary(franchise, strings),
                    FranchiseRenderer.RenderFieldPicker(franchise, strings),
                    ct
                );
                break;
        }
    }

    private async Task HandleEditFranchiseReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<EditFranchiseDialogData>(dialog.Data)!;
        var chatId = new TelegramChatId(message.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.Id == dialog.TeamId, ct);
        var strings = _strings.For(team.Locale);

        if (data.FieldIndex is not { } fieldIndex)
        {
            await _sender.SendAsync(chatId, strings.Text("Franchise.PickFieldFirst"), null, ct);
            return;
        }

        var franchise = await _db.Franchises.SingleAsync(f => f.Id == data.FranchiseId, ct);
        var input = message.Text!;
        string? errorKey;

        switch (fieldIndex)
        {
            case EditFranchiseDialogData.Name:
                if (FieldParsing.TryParseText(input, out var name, out errorKey))
                {
                    await _franchises.SetNameAsync(franchise, name, ct);
                }
                break;

            case EditFranchiseDialogData.Venue:
                if (FieldParsing.TryParseText(input, out var venue, out errorKey))
                {
                    await _franchises.SetVenueAsync(franchise, venue, ct);
                }
                break;

            case EditFranchiseDialogData.Capacity:
                if (FieldParsing.TryParseCapacity(input, out var capacity, out errorKey))
                {
                    await _franchises.SetCapacityAsync(franchise, capacity, ct);
                }
                break;

            case EditFranchiseDialogData.Price:
                if (FieldParsing.TryParsePrice(input, out var price, out errorKey))
                {
                    await _franchises.SetPriceAsync(franchise, price, ct);
                }
                break;

            case EditFranchiseDialogData.Schedule:
                if (FieldParsing.TryParseSchedule(input, team.Locale, out var schedule, out errorKey))
                {
                    await _franchises.SetScheduleAsync(franchise, schedule, ct);
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(dialog), fieldIndex, "Unknown EditFranchise field index");
        }

        if (errorKey is not null)
        {
            await _sender.SendAsync(chatId, strings.Text(errorKey), null, ct);
            return;
        }

        _db.DialogStates.Remove(dialog);
        await _db.SaveChangesAsync(ct);

        await _sender.SendAsync(chatId, strings.Text("Franchise.Updated"), null, ct);
        await _sender.SendAsync(
            chatId,
            FranchiseRenderer.RenderSummary(franchise, strings),
            FranchiseRenderer.RenderFieldPicker(franchise, strings),
            ct
        );
    }

    // --- Game creation/editing ---

    private async Task HandleNewGameReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<NewGameDialogData>(dialog.Data)!;
        var chatId = new TelegramChatId(message.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.Id == dialog.TeamId, ct);
        var strings = _strings.For(team.Locale);
        var input = message.Text!;

        switch (data.Step)
        {
            case NewGameDialogData.OneOffTitle:
                if (!FieldParsing.TryParseText(input, out var title, out var titleError))
                {
                    await _sender.SendAsync(chatId, strings.Text(titleError!), null, ct);
                    return;
                }

                await SaveDialogDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewGameDialogData.OneOffVenue,
                        Title = title,
                    },
                    ct
                );
                await _sender.SendAsync(chatId, strings.Text("NewGame.AskVenue"), null, ct);
                break;

            case NewGameDialogData.OneOffVenue:
                if (!FieldParsing.TryParseText(input, out var venue, out var venueError))
                {
                    await _sender.SendAsync(chatId, strings.Text(venueError!), null, ct);
                    return;
                }

                await SaveDialogDataAsync(dialog, data with { Step = NewGameDialogData.OneOffDate, Venue = venue }, ct);
                await _sender.SendAsync(chatId, strings.Text("NewGame.AskDate"), null, ct);
                break;

            case NewGameDialogData.OneOffDate:
                if (!FieldParsing.TryParseDate(input, out var oneOffDate, out var dateError))
                {
                    await _sender.SendAsync(chatId, strings.Text(dateError!), null, ct);
                    return;
                }

                await SaveDialogDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewGameDialogData.OneOffTime,
                        Date = oneOffDate,
                    },
                    ct
                );
                await _sender.SendAsync(chatId, strings.Text("NewGame.AskTime"), null, ct);
                break;

            case NewGameDialogData.OneOffTime:
                if (!FieldParsing.TryParseTime(input, out var time, out var timeError))
                {
                    await _sender.SendAsync(chatId, strings.Text(timeError!), null, ct);
                    return;
                }

                await SaveDialogDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewGameDialogData.OneOffCapacity,
                        Time = time,
                    },
                    ct
                );
                await _sender.SendAsync(chatId, strings.Text("NewGame.AskCapacity"), null, ct);
                break;

            case NewGameDialogData.OneOffCapacity:
                if (!FieldParsing.TryParseCapacity(input, out var capacity, out var capacityError))
                {
                    await _sender.SendAsync(chatId, strings.Text(capacityError!), null, ct);
                    return;
                }

                await SaveDialogDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewGameDialogData.OneOffPrice,
                        Capacity = capacity,
                    },
                    ct
                );
                await _sender.SendAsync(chatId, strings.Text("NewGame.AskPrice"), null, ct);
                break;

            case NewGameDialogData.OneOffPrice:
                if (!FieldParsing.TryParsePrice(input, out var price, out var priceError))
                {
                    await _sender.SendAsync(chatId, strings.Text(priceError!), null, ct);
                    return;
                }

                var confirmData = data with { Step = NewGameDialogData.Confirm, Price = price };
                await SaveDialogDataAsync(dialog, confirmData, ct);
                await SendConfirmScreenAsync(chatId, confirmData, strings, ct);
                break;

            case NewGameDialogData.EditingField:
                await HandleNewGameFieldOverrideReplyAsync(dialog, data, input, chatId, strings, ct);
                break;

            case NewGameDialogData.ChooseBranch:
            case NewGameDialogData.PickDate:
            case NewGameDialogData.Confirm:
                await _sender.SendAsync(chatId, strings.Text("NewGame.UseButtons"), null, ct);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(dialog), data.Step, "Unknown NewGame dialog step");
        }
    }

    private async Task HandleNewGameFieldOverrideReplyAsync(
        DialogState dialog,
        NewGameDialogData data,
        string input,
        TelegramChatId chatId,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        string? errorKey;
        var updated = data;

        switch (data.EditingFieldIndex)
        {
            case NewGameDialogData.OverrideVenue:
                if (FieldParsing.TryParseText(input, out var venue, out errorKey))
                {
                    updated = data with { Venue = venue };
                }
                break;

            case NewGameDialogData.OverrideCapacity:
                if (FieldParsing.TryParseCapacity(input, out var capacity, out errorKey))
                {
                    updated = data with { Capacity = capacity };
                }
                break;

            case NewGameDialogData.OverridePrice:
                if (FieldParsing.TryParsePrice(input, out var price, out errorKey))
                {
                    updated = data with { Price = price };
                }
                break;

            case NewGameDialogData.OverrideNotes:
                _ = FieldParsing.TryParseOptionalText(input, out var notes, out errorKey);
                updated = data with { Notes = notes };
                break;

            case NewGameDialogData.OverrideTags:
                _ = FieldParsing.TryParseTags(input, out var tags, out errorKey);
                updated = data with { Tags = tags };
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(data),
                    data.EditingFieldIndex,
                    "Unknown NewGame override field index"
                );
        }

        if (errorKey is not null)
        {
            await _sender.SendAsync(chatId, strings.Text(errorKey), null, ct);
            return;
        }

        updated = updated with { Step = NewGameDialogData.Confirm, EditingFieldIndex = null };
        await SaveDialogDataAsync(dialog, updated, ct);
        await SendConfirmScreenAsync(chatId, updated, strings, ct);
    }

    private async Task SendConfirmScreenAsync(
        TelegramChatId chatId,
        NewGameDialogData data,
        IStringsFor strings,
        CancellationToken ct
    ) =>
        await _sender.SendAsync(
            chatId,
            GameConfirmRenderer.RenderText(data, strings),
            GameConfirmRenderer.RenderKeyboard(strings),
            ct
        );

    private async Task HandleEditGameReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<EditGameDialogData>(dialog.Data)!;
        var chatId = new TelegramChatId(message.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.Id == dialog.TeamId, ct);
        var strings = _strings.For(team.Locale);

        if (data.FieldIndex is not { } fieldIndex)
        {
            await _sender.SendAsync(chatId, strings.Text("EditGame.PickFieldFirst"), null, ct);
            return;
        }

        var game = await _db.Games.SingleAsync(g => g.Id == data.GameId, ct);
        var input = message.Text!;
        string? errorKey;
        IReadOnlyList<Signup> promoted = [];

        switch (fieldIndex)
        {
            case EditGameDialogData.Title:
                if (FieldParsing.TryParseText(input, out var title, out errorKey))
                {
                    await _games.SetTitleAsync(game, title, ct);
                }
                break;

            case EditGameDialogData.Venue:
                if (FieldParsing.TryParseText(input, out var venue, out errorKey))
                {
                    await _games.SetVenueAsync(game, venue, ct);
                }
                break;

            case EditGameDialogData.Capacity:
                if (FieldParsing.TryParseCapacity(input, out var capacity, out errorKey))
                {
                    promoted = await _games.SetCapacityAsync(game, capacity, ct);
                }
                break;

            case EditGameDialogData.Price:
                if (FieldParsing.TryParsePrice(input, out var price, out errorKey))
                {
                    await _games.SetPriceAsync(game, price, ct);
                }
                break;

            case EditGameDialogData.Notes:
                _ = FieldParsing.TryParseOptionalText(input, out var notes, out errorKey);
                await _games.SetNotesAsync(game, notes, ct);
                break;

            case EditGameDialogData.StartTime:
                if (FieldParsing.TryParseTime(input, out var time, out errorKey))
                {
                    await _games.SetStartTimeAsync(game, time, team.TimeZoneId!, ct);
                }
                break;

            case EditGameDialogData.Tags:
                _ = FieldParsing.TryParseTags(input, out var tags, out errorKey);
                await _games.SetTagsAsync(game, tags, ct);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(dialog), fieldIndex, "Unknown EditGame field index");
        }

        if (errorKey is not null)
        {
            await _sender.SendAsync(chatId, strings.Text(errorKey), null, ct);
            return;
        }

        _db.DialogStates.Remove(dialog);
        await _db.SaveChangesAsync(ct);

        await _announcements.RefreshAsync(game, team, ct);
        await _sender.SendAsync(chatId, strings.Text("EditGame.Updated"), null, ct);

        await SendPromotionMessagesAsync(chatId, promoted, strings, ct);
    }

    private async Task HandleAddVenuePlayerReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<AddVenuePlayerDialogData>(dialog.Data)!;
        var chatId = new TelegramChatId(message.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.Id == dialog.TeamId, ct);
        var strings = _strings.For(team.Locale);

        if (!FieldParsing.TryParseText(message.Text!, out var name, out var errorKey))
        {
            await _sender.SendAsync(chatId, strings.Text(errorKey!), null, ct);
            return;
        }

        var game = await _db.Games.SingleAsync(g => g.Id == data.GameId, ct);
        var result = await _participations.AddVenueAssignedAsync(game, name, dialog.PlayerId, ct);

        _db.DialogStates.Remove(dialog);
        await _db.SaveChangesAsync(ct);

        if (!result.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(result.Error)), null, ct);
            return;
        }

        await SendRosterViewAsync(game, chatId, strings, ct);
    }

    private async Task HandleGuestNameReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<GuestNameDialogData>(dialog.Data)!;
        var chatId = new TelegramChatId(message.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.Id == dialog.TeamId, ct);
        var strings = _strings.For(team.Locale);

        var result = await _signups.NameGuestAsync(data.SignupId, dialog.PlayerId, message.Text!.Trim(), ct);

        _db.DialogStates.Remove(dialog);
        await _db.SaveChangesAsync(ct);

        if (!result.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(result.Error)), null, ct);
            return;
        }

        var game = await _db.Games.SingleAsync(g => g.Id == result.Value.GameId, ct);
        await _announcements.RefreshAsync(game, team, ct);

        await _sender.SendAsync(
            chatId,
            strings.Text("Guest.Named", new { Name = WebUtility.HtmlEncode(result.Value.GuestName) }),
            null,
            ct
        );
    }

    // --- Callback queries ---

    private async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        if (callbackQuery.Data is not { } data || !CallbackData.TryParseVerb(data, out var verb))
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        switch (verb)
        {
            case CallbackData.Join:
            case CallbackData.Guest:
            case CallbackData.Drop:
            case CallbackData.ConfirmDrop:
            case CallbackData.Stay:
            case CallbackData.MyGuests:
            case CallbackData.Nudge:
            case CallbackData.ManageRoster:
            case CallbackData.ManagePlayers:
            case CallbackData.DeclineGame:
            case CallbackData.ConfirmDecline:
            case CallbackData.CancelDecline:
            case CallbackData.FinishGame:
                await HandleGameCallbackAsync(verb, callbackQuery, ct);
                break;

            case CallbackData.SkipGuestName:
            case CallbackData.KeepGuest:
            case CallbackData.RemoveGuestToo:
            case CallbackData.RemoveGuest:
                await HandleGuestCallbackAsync(verb, callbackQuery, ct);
                break;

            case CallbackData.PickFranchise:
            case CallbackData.ArchiveFranchise:
            case CallbackData.OneOff:
            case CallbackData.PickDate:
            case CallbackData.EditField:
            case CallbackData.Confirm:
            case CallbackData.CancelDialog:
            case CallbackData.PickGameToEdit:
                await HandleCaptainFlowCallbackAsync(verb, callbackQuery, ct);
                break;

            case CallbackData.ToggleAttended:
            case CallbackData.TogglePlayed:
            case CallbackData.AddPlayer:
                await HandleRosterCallbackAsync(verb, callbackQuery, ct);
                break;

            case CallbackData.ToggleNudgeTarget:
            case CallbackData.SendNudge:
                await HandleNudgeCallbackAsync(verb, callbackQuery, ct);
                break;

            case CallbackData.TogglePlayerSignup:
                await HandleManagePlayersCallbackAsync(callbackQuery, ct);
                break;

            case CallbackData.CycleReminderChannel:
            case CallbackData.ToggleReserveReminder:
                await HandleReminderSettingsCallbackAsync(verb, callbackQuery, ct);
                break;

            case CallbackData.ToggleCaptain:
                await HandleManageCaptainsCallbackAsync(callbackQuery, ct);
                break;

            default:
                _logger.LogDebug("No handler for callback data {Data}", data);
                await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
                break;
        }
    }

    private async Task HandleGameCallbackAsync(char verb, CallbackQuery callbackQuery, CancellationToken ct)
    {
        _ = CallbackData.TryParse(callbackQuery.Data!, out _, out GameId gameId);

        var game = await _db.Games.SingleOrDefaultAsync(g => g.Id == gameId, ct);
        if (game is null)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var team = await _db.Teams.SingleAsync(t => t.Id == game.TeamId, ct);
        var strings = _strings.For(team.Locale);
        var player = await _playerBootstrap.GetOrCreateAsync(callbackQuery.From, ct);
        await _playerBootstrap.EnsureMembershipAsync(team.Id, player.Id, ct);

        switch (verb)
        {
            case CallbackData.Join:
                await HandleJoinAsync(game, team, player, callbackQuery, strings, ct);
                break;

            case CallbackData.Guest:
                await HandleBringGuestAsync(game, team, player, callbackQuery, strings, ct);
                break;

            case CallbackData.Drop:
                await HandleDropPromptAsync(game, player, callbackQuery, strings, ct);
                break;

            case CallbackData.ConfirmDrop:
                await HandleConfirmDropAsync(game, team, player, callbackQuery, strings, ct);
                break;

            case CallbackData.Stay:
                await HandleStayAsync(callbackQuery, strings, ct);
                break;

            case CallbackData.MyGuests:
                await HandleMyGuestsAsync(game, player, callbackQuery, strings, ct);
                break;

            case CallbackData.Nudge:
                await HandleNudgeButtonAsync(game, team, player, callbackQuery, strings, ct);
                break;

            case CallbackData.ManageRoster:
                await HandleManageRosterButtonAsync(game, team, player, callbackQuery, strings, ct);
                break;

            case CallbackData.ManagePlayers:
                await HandleManagePlayersButtonAsync(game, team, player, callbackQuery, strings, ct);
                break;

            case CallbackData.DeclineGame:
                await HandleDeclinePromptAsync(game, team, player, callbackQuery, strings, ct);
                break;

            case CallbackData.ConfirmDecline:
                await HandleConfirmDeclineAsync(game, team, player, callbackQuery, strings, ct);
                break;

            case CallbackData.CancelDecline:
                await HandleCancelDeclineAsync(callbackQuery, strings, ct);
                break;

            case CallbackData.FinishGame:
                await HandleFinishButtonAsync(game, team, player, callbackQuery, strings, ct);
                break;
        }
    }

    private async Task HandleJoinAsync(
        Game game,
        Team team,
        Player player,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var result = await _signups.JoinAsync(game, player.Id, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        await _announcements.RefreshAsync(game, team, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, strings.Text("Announcement.Joined"), cancellationToken: ct);
    }

    private async Task HandleBringGuestAsync(
        Game game,
        Team team,
        Player player,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var result = await _signups.BringGuestAsync(game, player.Id, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        await _announcements.RefreshAsync(game, team, ct);
        await _bot.AnswerCallbackQuery(
            callbackQuery.Id,
            strings.Text("Announcement.GuestAdded"),
            cancellationToken: ct
        );

        await StartGuestNamingDialogAsync(team, player, callbackQuery, result.Value, strings, ct);
    }

    private async Task StartGuestNamingDialogAsync(
        Team team,
        Player player,
        CallbackQuery callbackQuery,
        Signup guestSignup,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);

        // One dialog per (chat, player) — a stray earlier one (e.g. from a naming prompt
        // nobody answered) is replaced rather than left to collide on the unique index.
        var existing = await _db.DialogStates.SingleOrDefaultAsync(
            d => d.ChatId == chatId && d.PlayerId == player.Id,
            ct
        );
        if (existing is not null)
        {
            _db.DialogStates.Remove(existing);
        }

        var now = _clock.GetUtcNow();
        _db.DialogStates.Add(
            new DialogState
            {
                TeamId = team.Id,
                PlayerId = player.Id,
                ChatId = chatId,
                Kind = DialogKinds.NameGuest,
                Step = "AwaitingName",
                Data = JsonSerializer.Serialize(new GuestNameDialogData(guestSignup.Id)),
                CreatedAt = now,
                UpdatedAt = now,
            }
        );
        await _db.SaveChangesAsync(ct);

        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Guest.SkipButton"),
                    CallbackData.Format(CallbackData.SkipGuestName, guestSignup.Id)
                ),
            ],
        ]);
        await _sender.SendAsync(chatId, strings.Text("Guest.NamePrompt"), keyboard, ct);
    }

    private async Task HandleDropPromptAsync(
        Game game,
        Player player,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var hasSignup = await _db.Signups.AnyAsync(
            s => s.GameId == game.Id && s.PlayerId == player.Id && s.CancelledAt == null,
            ct
        );
        if (!hasSignup)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("Signup.NotSignedUp"), ct);
            return;
        }

        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Drop.ConfirmYes"),
                    CallbackData.Format(CallbackData.ConfirmDrop, game.Id)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Drop.ConfirmNo"),
                    CallbackData.Format(CallbackData.Stay, game.Id)
                ),
            ],
        ]);
        await _sender.SendAsync(
            chatId,
            strings.Text("Drop.ConfirmPrompt", new { Title = WebUtility.HtmlEncode(game.Title) }),
            keyboard,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleConfirmDropAsync(
        Game game,
        Team team,
        Player player,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var result = await _signups.DropAsync(game, player.Id, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        await _announcements.RefreshAsync(game, team, ct);

        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        await _sender.EditAsync(
            chatId,
            new TelegramMessageId(callbackQuery.Message.MessageId),
            strings.Text("Drop.Cancelled"),
            null,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);

        var outcome = result.Value;
        foreach (var guest in outcome.NamedGuestsNeedingChoice)
        {
            await SendGuestChoicePromptAsync(chatId, guest, strings, ct);
        }

        await SendPromotionMessagesAsync(chatId, outcome.NewlyPromoted, strings, ct);
    }

    private async Task HandleStayAsync(CallbackQuery callbackQuery, IStringsFor strings, CancellationToken ct)
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        await _sender.EditAsync(
            chatId,
            new TelegramMessageId(callbackQuery.Message.MessageId),
            strings.Text("Drop.StillIn"),
            null,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleMyGuestsAsync(
        Game game,
        Player player,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var guests = await _signups.LoadLiveGuestsAsync(game, player.Id, ct);
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var (text, keyboard) = BuildMyGuestsView(game, guests, strings);

        await _sender.SendAsync(chatId, text, keyboard, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private static (string Text, InlineKeyboardMarkup Keyboard) BuildMyGuestsView(
        Game game,
        IReadOnlyList<Signup> guests,
        IStringsFor strings
    )
    {
        var encodedTitle = WebUtility.HtmlEncode(game.Title);
        var text =
            guests.Count == 0
                ? strings.Text("MyGuests.Empty", new { Title = encodedTitle })
                : strings.Text("MyGuests.Header", new { Title = encodedTitle });

        var rows = new List<IEnumerable<InlineKeyboardButton>>();
        for (var i = 0; i < guests.Count; i++)
        {
            var guest = guests[i];
            var label = guest.GuestName is { } name
                ? strings.Text("MyGuests.RemoveNamedButton", new { Name = name })
                : strings.Text("MyGuests.RemoveAnonymousButton", new { Index = i + 1 });

            rows.Add([
                InlineKeyboardButton.WithCallbackData(label, CallbackData.Format(CallbackData.RemoveGuest, guest.Id)),
            ]);
        }

        rows.Add([
            InlineKeyboardButton.WithCallbackData(
                strings.Text("MyGuests.AddAnotherButton"),
                CallbackData.Format(CallbackData.Guest, game.Id)
            ),
        ]);

        return (text, new InlineKeyboardMarkup(rows));
    }

    private async Task HandleRemoveGuestAsync(
        SignupId guestSignupId,
        Player player,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.ChatId == chatId, ct);
        var strings = _strings.For(team.Locale);

        var result = await _signups.RemoveGuestAsync(guestSignupId, player.Id, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        var outcome = result.Value;
        var game = await _db.Games.SingleAsync(g => g.Id == outcome.Guest.GameId, ct);
        await _announcements.RefreshAsync(game, team, ct);

        var remaining = await _signups.LoadLiveGuestsAsync(game, player.Id, ct);
        var (text, keyboard) = BuildMyGuestsView(game, remaining, strings);
        await _sender.EditAsync(chatId, new TelegramMessageId(callbackQuery.Message.MessageId), text, keyboard, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, strings.Text("MyGuests.Removed"), cancellationToken: ct);

        await SendPromotionMessagesAsync(chatId, outcome.NewlyPromoted, strings, ct);
    }

    private async Task HandleGuestCallbackAsync(char verb, CallbackQuery callbackQuery, CancellationToken ct)
    {
        _ = CallbackData.TryParse(callbackQuery.Data!, out _, out SignupId signupId);
        var player = await _playerBootstrap.GetOrCreateAsync(callbackQuery.From, ct);

        switch (verb)
        {
            case CallbackData.SkipGuestName:
                await HandleSkipGuestNameAsync(player, callbackQuery, ct);
                break;

            case CallbackData.KeepGuest:
            case CallbackData.RemoveGuestToo:
                await HandleGuestChoiceAsync(signupId, verb == CallbackData.KeepGuest, player, callbackQuery, ct);
                break;

            case CallbackData.RemoveGuest:
                await HandleRemoveGuestAsync(signupId, player, callbackQuery, ct);
                break;
        }
    }

    private async Task HandleSkipGuestNameAsync(Player player, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var dialog = await _db.DialogStates.SingleOrDefaultAsync(
            d => d.ChatId == chatId && d.PlayerId == player.Id && d.Kind == DialogKinds.NameGuest,
            ct
        );
        if (dialog is not null)
        {
            _db.DialogStates.Remove(dialog);
            await _db.SaveChangesAsync(ct);
        }

        var team = await _db.Teams.SingleAsync(t => t.ChatId == chatId, ct);
        var strings = _strings.For(team.Locale);
        await _sender.EditAsync(
            chatId,
            new TelegramMessageId(callbackQuery.Message.MessageId),
            strings.Text("Guest.SkippedName"),
            null,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleGuestChoiceAsync(
        SignupId guestSignupId,
        bool keep,
        Player player,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.ChatId == chatId, ct);
        var strings = _strings.For(team.Locale);

        var result = await _signups.ResolveGuestChoiceAsync(guestSignupId, player.Id, keep, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        var outcome = result.Value;
        var encodedName = WebUtility.HtmlEncode(outcome.Guest.GuestName);
        await _sender.EditAsync(
            chatId,
            new TelegramMessageId(callbackQuery.Message.MessageId),
            strings.Text(keep ? "Guest.Kept" : "Guest.Removed", new { Name = encodedName }),
            null,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);

        var game = await _db.Games.SingleAsync(g => g.Id == outcome.Guest.GameId, ct);
        await _announcements.RefreshAsync(game, team, ct);

        await SendPromotionMessagesAsync(chatId, outcome.NewlyPromoted, strings, ct);
    }

    private async Task SendGuestChoicePromptAsync(
        TelegramChatId chatId,
        Signup guest,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var encodedName = WebUtility.HtmlEncode(guest.GuestName);
        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Guest.KeepButton", new { Name = guest.GuestName }),
                    CallbackData.Format(CallbackData.KeepGuest, guest.Id)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Guest.RemoveButton", new { Name = guest.GuestName }),
                    CallbackData.Format(CallbackData.RemoveGuestToo, guest.Id)
                ),
            ],
        ]);
        await _sender.SendAsync(chatId, strings.Text("Guest.KeepQuestion", new { Name = encodedName }), keyboard, ct);
    }

    // --- Captain flows: franchise pick/edit, game creation branch/date/confirm, game-edit
    // field pick. Which dialog is active decides how a shared verb like EditField is
    // interpreted — see CallbackData.cs.

    private async Task HandleCaptainFlowCallbackAsync(char verb, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.ChatId == chatId, ct);
        var strings = _strings.For(team.Locale);
        var player = await _playerBootstrap.GetOrCreateAsync(callbackQuery.From, ct);
        var telegramUserId = new TelegramUserId(callbackQuery.From.Id);

        if (!await _teamGuard.IsCaptainAsync(team.Id, player.Id, chatId, telegramUserId, ct))
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("NewGame.NotCaptain"), ct);
            return;
        }

        _ = CallbackData.TryParse(callbackQuery.Data!, out _, out long value);
        var dialog = await _db.DialogStates.SingleOrDefaultAsync(
            d => d.ChatId == chatId && d.PlayerId == player.Id,
            ct
        );

        switch (verb)
        {
            case CallbackData.PickFranchise:
                await HandlePickFranchiseAsync(dialog, team, player, chatId, value, callbackQuery, strings, ct);
                break;

            case CallbackData.ArchiveFranchise:
                await HandleArchiveFranchiseAsync(new FranchiseId(value), chatId, callbackQuery, strings, ct);
                break;

            case CallbackData.OneOff:
                await HandleOneOffAsync(dialog, chatId, callbackQuery, strings, ct);
                break;

            case CallbackData.PickDate:
                await HandlePickDateAsync(dialog, chatId, (int)value, callbackQuery, strings, ct);
                break;

            case CallbackData.EditField:
                await HandleEditFieldAsync(dialog, chatId, (int)value, callbackQuery, strings, ct);
                break;

            case CallbackData.Confirm:
                await HandleConfirmNewGameAsync(dialog, team, player, chatId, callbackQuery, strings, ct);
                break;

            case CallbackData.CancelDialog:
                await HandleCancelDialogAsync(dialog, chatId, callbackQuery, strings, ct);
                break;

            case CallbackData.PickGameToEdit:
                await HandlePickGameToEditAsync(new GameId(value), team, player, chatId, callbackQuery, strings, ct);
                break;
        }
    }

    private async Task HandlePickFranchiseAsync(
        DialogState? dialog,
        Team team,
        Player player,
        TelegramChatId chatId,
        long rawFranchiseId,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var franchiseId = new FranchiseId(rawFranchiseId);
        var franchise = await _db.Franchises.SingleOrDefaultAsync(f => f.Id == franchiseId, ct);
        if (franchise is null)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        if (dialog is { Kind: DialogKinds.NewGame } newGameDialog)
        {
            var newGameData = JsonSerializer.Deserialize<NewGameDialogData>(newGameDialog.Data)!;
            if (newGameData.Step == NewGameDialogData.ChooseBranch)
            {
                await HandleFranchisePickedForNewGameAsync(
                    newGameDialog,
                    newGameData,
                    franchise,
                    team,
                    chatId,
                    callbackQuery,
                    strings,
                    ct
                );
                return;
            }
        }

        // Otherwise: the /editfranchise picker (design decision #1).
        await StartDialogAsync(
            team.Id,
            player.Id,
            chatId,
            DialogKinds.EditFranchise,
            new EditFranchiseDialogData(franchiseId, null),
            ct
        );
        await _sender.SendAsync(
            chatId,
            FranchiseRenderer.RenderSummary(franchise, strings),
            FranchiseRenderer.RenderFieldPicker(franchise, strings),
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleFranchisePickedForNewGameAsync(
        DialogState dialog,
        NewGameDialogData data,
        Franchise franchise,
        Team team,
        TelegramChatId chatId,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var today = DateOnly.FromDateTime(TeamTime.ConvertToLocal(_clock.GetUtcNow(), team.TimeZoneId!).Date);
        var candidateDates = GameService.NextCandidateDates(today, franchise.Schedule, 8);
        var title = await _games.PreviewFranchiseTitleAsync(franchise, ct);

        var updated = data with
        {
            Step = NewGameDialogData.PickDate,
            FranchiseId = franchise.Id,
            CandidateDates = candidateDates,
            Title = title,
            Venue = franchise.DefaultVenue,
            Capacity = franchise.DefaultCapacity,
            Price = franchise.DefaultPrice,
        };
        await SaveDialogDataAsync(dialog, updated, ct);

        var keyboard = new InlineKeyboardMarkup(
            candidateDates.Select(
                (date, index) =>
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData(
                            $"{date:yyyy-MM-dd}",
                            CallbackData.Format(CallbackData.PickDate, index)
                        ),
                    }
            )
        );
        await _sender.SendAsync(chatId, strings.Text("NewGame.PickDate"), keyboard, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleArchiveFranchiseAsync(
        FranchiseId franchiseId,
        TelegramChatId chatId,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var franchise = await _db.Franchises.SingleOrDefaultAsync(f => f.Id == franchiseId, ct);
        if (franchise is null)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        await _franchises.ArchiveAsync(franchise, ct);
        await _sender.EditAsync(
            chatId,
            new TelegramMessageId(callbackQuery.Message!.MessageId),
            strings.Text("Franchise.ArchivedConfirm", new { Name = WebUtility.HtmlEncode(franchise.Name) }),
            null,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleOneOffAsync(
        DialogState? dialog,
        TelegramChatId chatId,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        if (dialog is not { Kind: DialogKinds.NewGame })
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var data = JsonSerializer.Deserialize<NewGameDialogData>(dialog.Data)!;
        if (data.Step != NewGameDialogData.ChooseBranch)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        await SaveDialogDataAsync(dialog, data with { Step = NewGameDialogData.OneOffTitle }, ct);
        await _sender.SendAsync(chatId, strings.Text("NewGame.AskTitle"), null, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandlePickDateAsync(
        DialogState? dialog,
        TelegramChatId chatId,
        int index,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        if (dialog is not { Kind: DialogKinds.NewGame })
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var data = JsonSerializer.Deserialize<NewGameDialogData>(dialog.Data)!;
        if (
            data.Step != NewGameDialogData.PickDate
            || data.CandidateDates is not { } dates
            || index < 0
            || index >= dates.Count
        )
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var franchise = await _db.Franchises.SingleAsync(f => f.Id == data.FranchiseId!.Value, ct);
        var time = franchise.Schedule[dates[index].DayOfWeek];

        var updated = data with { Step = NewGameDialogData.Confirm, Date = dates[index], Time = time };
        await SaveDialogDataAsync(dialog, updated, ct);
        await SendConfirmScreenAsync(chatId, updated, strings, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleEditFieldAsync(
        DialogState? dialog,
        TelegramChatId chatId,
        int fieldIndex,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        switch (dialog?.Kind)
        {
            case DialogKinds.EditFranchise:
            {
                var data = JsonSerializer.Deserialize<EditFranchiseDialogData>(dialog.Data)!;
                await SaveDialogDataAsync(dialog, data with { FieldIndex = fieldIndex }, ct);
                await _sender.SendAsync(chatId, strings.Text(FranchiseFieldPromptKey(fieldIndex)), null, ct);
                break;
            }

            case DialogKinds.NewGame:
            {
                var data = JsonSerializer.Deserialize<NewGameDialogData>(dialog.Data)!;
                var updated = data with { Step = NewGameDialogData.EditingField, EditingFieldIndex = fieldIndex };
                await SaveDialogDataAsync(dialog, updated, ct);
                await _sender.SendAsync(chatId, strings.Text(NewGameFieldPromptKey(fieldIndex)), null, ct);
                break;
            }

            case DialogKinds.EditGame:
            {
                var data = JsonSerializer.Deserialize<EditGameDialogData>(dialog.Data)!;
                await SaveDialogDataAsync(dialog, data with { FieldIndex = fieldIndex }, ct);
                await _sender.SendAsync(chatId, strings.Text(EditGameFieldPromptKey(fieldIndex)), null, ct);
                break;
            }
        }

        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private static string FranchiseFieldPromptKey(int fieldIndex) =>
        fieldIndex switch
        {
            EditFranchiseDialogData.Name => "Franchise.AskName",
            EditFranchiseDialogData.Venue => "Franchise.AskVenue",
            EditFranchiseDialogData.Capacity => "Franchise.AskCapacity",
            EditFranchiseDialogData.Price => "Franchise.AskPrice",
            EditFranchiseDialogData.Schedule => "Franchise.AskSchedule",
            _ => throw new ArgumentOutOfRangeException(
                nameof(fieldIndex),
                fieldIndex,
                "Unknown EditFranchise field index"
            ),
        };

    private static string NewGameFieldPromptKey(int fieldIndex) =>
        fieldIndex switch
        {
            NewGameDialogData.OverrideVenue => "NewGame.AskVenue",
            NewGameDialogData.OverrideCapacity => "NewGame.AskCapacity",
            NewGameDialogData.OverridePrice => "NewGame.AskPrice",
            NewGameDialogData.OverrideNotes => "NewGame.AskNotes",
            NewGameDialogData.OverrideTags => "NewGame.AskTags",
            _ => throw new ArgumentOutOfRangeException(
                nameof(fieldIndex),
                fieldIndex,
                "Unknown NewGame override field index"
            ),
        };

    private static string EditGameFieldPromptKey(int fieldIndex) =>
        fieldIndex switch
        {
            EditGameDialogData.Title => "EditGame.AskTitle",
            EditGameDialogData.Venue => "EditGame.AskVenue",
            EditGameDialogData.Capacity => "EditGame.AskCapacity",
            EditGameDialogData.Price => "EditGame.AskPrice",
            EditGameDialogData.Notes => "EditGame.AskNotes",
            EditGameDialogData.StartTime => "EditGame.AskStartTime",
            EditGameDialogData.Tags => "EditGame.AskTags",
            _ => throw new ArgumentOutOfRangeException(nameof(fieldIndex), fieldIndex, "Unknown EditGame field index"),
        };

    private async Task HandleConfirmNewGameAsync(
        DialogState? dialog,
        Team team,
        Player player,
        TelegramChatId chatId,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        if (dialog is not { Kind: DialogKinds.NewGame })
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var data = JsonSerializer.Deserialize<NewGameDialogData>(dialog.Data)!;
        if (data.Step != NewGameDialogData.Confirm)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        Game game;
        if (data.FranchiseId is { } franchiseId)
        {
            var franchise = await _db.Franchises.SingleAsync(f => f.Id == franchiseId, ct);
            game = await _games.CreateFromFranchiseAsync(
                franchise,
                data.Title!,
                data.Date!.Value,
                data.Venue!,
                data.Capacity!.Value,
                data.Price,
                data.Notes,
                data.Tags ?? [],
                player.Id,
                team.TimeZoneId!,
                ct
            );
        }
        else
        {
            game = await _games.CreateOneOffAsync(
                team.Id,
                data.Title!,
                data.Venue!,
                data.Date!.Value,
                data.Time!.Value,
                data.Capacity!.Value,
                data.Price,
                player.Id,
                team.TimeZoneId!,
                ct
            );
            if (!string.IsNullOrWhiteSpace(data.Notes))
            {
                await _games.SetNotesAsync(game, data.Notes, ct);
            }

            if (data.Tags is { Count: > 0 })
            {
                await _games.SetTagsAsync(game, data.Tags, ct);
            }
        }

        _db.DialogStates.Remove(dialog);
        await _db.SaveChangesAsync(ct);

        var messageId = await _announcements.PostAsync(game, team, ct);
        game.AnnouncementMessageId = messageId;
        await _db.SaveChangesAsync(ct);
        await _board.RefreshAsync(team, ct);

        await _sender.EditAsync(
            chatId,
            new TelegramMessageId(callbackQuery.Message!.MessageId),
            strings.Text("NewGame.Created"),
            null,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleCancelDialogAsync(
        DialogState? dialog,
        TelegramChatId chatId,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var key = dialog?.Kind == DialogKinds.Nudge ? "Nudge.Cancelled" : "NewGame.Cancelled";

        if (dialog is not null)
        {
            _db.DialogStates.Remove(dialog);
            await _db.SaveChangesAsync(ct);
        }

        await _sender.EditAsync(
            chatId,
            new TelegramMessageId(callbackQuery.Message!.MessageId),
            strings.Text(key),
            null,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandlePickGameToEditAsync(
        GameId gameId,
        Team team,
        Player player,
        TelegramChatId chatId,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var game = await _db.Games.SingleOrDefaultAsync(g => g.Id == gameId, ct);
        if (game is null)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        await StartDialogAsync(
            team.Id,
            player.Id,
            chatId,
            DialogKinds.EditGame,
            new EditGameDialogData(gameId, null),
            ct
        );
        await _sender.SendAsync(
            chatId,
            strings.Text("EditGame.PickField", new { Title = WebUtility.HtmlEncode(game.Title) }),
            RenderEditGameFieldPicker(strings),
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private static InlineKeyboardMarkup RenderEditGameFieldPicker(IStringsFor strings) =>
        new([
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("EditGame.EditTitleButton"),
                    CallbackData.Format(CallbackData.EditField, EditGameDialogData.Title)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("EditGame.EditVenueButton"),
                    CallbackData.Format(CallbackData.EditField, EditGameDialogData.Venue)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("EditGame.EditCapacityButton"),
                    CallbackData.Format(CallbackData.EditField, EditGameDialogData.Capacity)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("EditGame.EditPriceButton"),
                    CallbackData.Format(CallbackData.EditField, EditGameDialogData.Price)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("EditGame.EditNotesButton"),
                    CallbackData.Format(CallbackData.EditField, EditGameDialogData.Notes)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("EditGame.EditStartTimeButton"),
                    CallbackData.Format(CallbackData.EditField, EditGameDialogData.StartTime)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("EditGame.EditTagsButton"),
                    CallbackData.Format(CallbackData.EditField, EditGameDialogData.Tags)
                ),
            ],
        ]);

    // --- Manage roster (design decision #4): Played/Attended toggles plus Add player, on a
    // finished game's captain-only button. ---

    private async Task HandleManageRosterButtonAsync(
        Game game,
        Team team,
        Player player,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var telegramUserId = new TelegramUserId(callbackQuery.From.Id);
        if (!await _teamGuard.IsCaptainAsync(team.Id, player.Id, chatId, telegramUserId, ct))
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("NewGame.NotCaptain"), ct);
            return;
        }

        await SendRosterViewAsync(game, chatId, strings, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task SendRosterViewAsync(Game game, TelegramChatId chatId, IStringsFor strings, CancellationToken ct)
    {
        var participations = await LoadParticipationsAsync(game, ct);
        await _sender.SendAsync(
            chatId,
            RosterManagementRenderer.RenderText(game, participations, strings),
            RosterManagementRenderer.RenderKeyboard(participations, strings),
            ct
        );
    }

    private async Task<IReadOnlyList<Participation>> LoadParticipationsAsync(Game game, CancellationToken ct) =>
        await _db
            .Participations.AsNoTracking()
            .Include(p => p.Player)
            .Where(p => p.GameId == game.Id)
            // Id breaks ties on identical CreatedAt (FinishGameAsync stamps a whole batch with
            // the same instant) — same reasoning as Roster.Split.
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .ToListAsync(ct);

    private async Task HandleRosterCallbackAsync(char verb, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.ChatId == chatId, ct);
        var strings = _strings.For(team.Locale);
        var player = await _playerBootstrap.GetOrCreateAsync(callbackQuery.From, ct);
        var telegramUserId = new TelegramUserId(callbackQuery.From.Id);

        if (!await _teamGuard.IsCaptainAsync(team.Id, player.Id, chatId, telegramUserId, ct))
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("NewGame.NotCaptain"), ct);
            return;
        }

        _ = CallbackData.TryParse(callbackQuery.Data!, out _, out long value);

        switch (verb)
        {
            case CallbackData.ToggleAttended:
            case CallbackData.TogglePlayed:
                await HandleToggleParticipationAsync(
                    new ParticipationId(value),
                    verb == CallbackData.ToggleAttended,
                    player.Id,
                    chatId,
                    callbackQuery,
                    strings,
                    ct
                );
                break;

            case CallbackData.AddPlayer:
                await HandleAddPlayerButtonAsync(new GameId(value), team, player, chatId, callbackQuery, strings, ct);
                break;
        }
    }

    private async Task HandleToggleParticipationAsync(
        ParticipationId participationId,
        bool toggleAttended,
        PlayerId actorPlayerId,
        TelegramChatId chatId,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var participation = await _db
            .Participations.Include(p => p.Game)
            .SingleOrDefaultAsync(p => p.Id == participationId, ct);
        if (participation is null)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        _ = toggleAttended
            ? await _participations.ToggleAttendedAsync(participation, actorPlayerId, ct)
            : await _participations.TogglePlayedAsync(participation, actorPlayerId, ct);

        var game = participation.Game;
        var participations = await LoadParticipationsAsync(game, ct);
        await _sender.EditAsync(
            chatId,
            new TelegramMessageId(callbackQuery.Message!.MessageId),
            RosterManagementRenderer.RenderText(game, participations, strings),
            RosterManagementRenderer.RenderKeyboard(participations, strings),
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleAddPlayerButtonAsync(
        GameId gameId,
        Team team,
        Player player,
        TelegramChatId chatId,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        await StartDialogAsync(
            team.Id,
            player.Id,
            chatId,
            DialogKinds.AddVenuePlayer,
            new AddVenuePlayerDialogData(gameId),
            ct
        );
        await _sender.SendAsync(chatId, strings.Text("Roster.AskPlayerName"), null, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    // --- Nudge (design decision #5): a targeted multi-select, not a blind broadcast. ---

    private async Task HandleNudgeButtonAsync(
        Game game,
        Team team,
        Player player,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var telegramUserId = new TelegramUserId(callbackQuery.From.Id);
        if (!await _teamGuard.IsCaptainAsync(team.Id, player.Id, chatId, telegramUserId, ct))
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("NewGame.NotCaptain"), ct);
            return;
        }

        var missing = await _games.LoadMissingMembersAsync(game, ct);
        if (missing.Count == 0)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("Nudge.NoneMissing"), ct);
            return;
        }

        var selected = missing.Select(m => m.PlayerId.Value).ToList();
        await StartDialogAsync(
            team.Id,
            player.Id,
            chatId,
            DialogKinds.Nudge,
            new NudgeDialogData(game.Id, selected),
            ct
        );

        var (text, keyboard) = BuildNudgeView(game, missing, selected, strings);
        await _sender.SendAsync(chatId, text, keyboard, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private static (string Text, InlineKeyboardMarkup Keyboard) BuildNudgeView(
        Game game,
        IReadOnlyList<Membership> missing,
        List<long> selectedPlayerIds,
        IStringsFor strings
    )
    {
        var text = strings.Text("Nudge.Header", new { Title = WebUtility.HtmlEncode(game.Title) });

        var rows = new List<IEnumerable<InlineKeyboardButton>>();
        foreach (var membership in missing)
        {
            var isSelected = selectedPlayerIds.Contains(membership.PlayerId.Value);
            var label = strings.Text(
                isSelected ? "Nudge.SelectedButton" : "Nudge.UnselectedButton",
                new { Name = membership.Player.DisplayName }
            );
            rows.Add([
                InlineKeyboardButton.WithCallbackData(
                    label,
                    CallbackData.Format(CallbackData.ToggleNudgeTarget, membership.PlayerId)
                ),
            ]);
        }

        rows.Add([
            InlineKeyboardButton.WithCallbackData(
                strings.Text("Nudge.SendButton"),
                CallbackData.Format(CallbackData.SendNudge, game.Id)
            ),
            InlineKeyboardButton.WithCallbackData(
                strings.Text("Nudge.CancelButton"),
                CallbackData.Format(CallbackData.CancelDialog, 0L)
            ),
        ]);

        return (text, new InlineKeyboardMarkup(rows));
    }

    private async Task HandleNudgeCallbackAsync(char verb, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.ChatId == chatId, ct);
        var strings = _strings.For(team.Locale);
        var player = await _playerBootstrap.GetOrCreateAsync(callbackQuery.From, ct);
        var telegramUserId = new TelegramUserId(callbackQuery.From.Id);

        if (!await _teamGuard.IsCaptainAsync(team.Id, player.Id, chatId, telegramUserId, ct))
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("NewGame.NotCaptain"), ct);
            return;
        }

        var dialog = await _db.DialogStates.SingleOrDefaultAsync(
            d => d.ChatId == chatId && d.PlayerId == player.Id && d.Kind == DialogKinds.Nudge,
            ct
        );
        if (dialog is null)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var data = JsonSerializer.Deserialize<NudgeDialogData>(dialog.Data)!;
        var game = await _db.Games.SingleAsync(g => g.Id == data.GameId, ct);
        _ = CallbackData.TryParse(callbackQuery.Data!, out _, out long value);

        if (verb == CallbackData.ToggleNudgeTarget)
        {
            await HandleToggleNudgeTargetAsync(dialog, data, game, value, chatId, callbackQuery, strings, ct);
            return;
        }

        await HandleSendNudgeAsync(dialog, data, game, chatId, callbackQuery, strings, ct);
    }

    private async Task HandleToggleNudgeTargetAsync(
        DialogState dialog,
        NudgeDialogData data,
        Game game,
        long playerId,
        TelegramChatId chatId,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var selected = new List<long>(data.SelectedPlayerIds);
        if (!selected.Remove(playerId))
        {
            selected.Add(playerId);
        }

        await SaveDialogDataAsync(dialog, data with { SelectedPlayerIds = selected }, ct);

        var missing = await _games.LoadMissingMembersAsync(game, ct);
        var (text, keyboard) = BuildNudgeView(game, missing, selected, strings);
        await _sender.EditAsync(chatId, new TelegramMessageId(callbackQuery.Message!.MessageId), text, keyboard, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleSendNudgeAsync(
        DialogState dialog,
        NudgeDialogData data,
        Game game,
        TelegramChatId chatId,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        if (data.SelectedPlayerIds.Count == 0)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("Nudge.NoneSelected"), ct);
            return;
        }

        var result = await _games.TryNudgeAsync(game, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        var selectedIds = data.SelectedPlayerIds.Select(id => new PlayerId(id)).ToList();
        var players = await _db.Players.AsNoTracking().Where(p => selectedIds.Contains(p.Id)).ToListAsync(ct);
        var mentions = string.Join(", ", players.Select(Mention));

        _db.DialogStates.Remove(dialog);
        await _db.SaveChangesAsync(ct);

        await _sender.SendAsync(
            chatId,
            strings.Text("Nudge.Sent", new { Mentions = mentions, Title = WebUtility.HtmlEncode(game.Title) }),
            null,
            ct
        );
        await _sender.EditAsync(
            chatId,
            new TelegramMessageId(callbackQuery.Message!.MessageId),
            strings.Text("Nudge.PickerClosed"),
            null,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private static string Mention(Player player) =>
        $"""<a href="tg://user?id={player.TelegramUserId.Value}">{WebUtility.HtmlEncode(player.DisplayName)}</a>""";

    // One message for every promotion a single change caused (a capacity bump or a drop can
    // promote several people at once), not one per person — same reasoning as
    // SchedulerService's batched group reminder. Player is Included wherever these signups are
    // loaded (GameService.SetCapacityAsync, SignupService.LoadRosterAsync), so this reads the
    // navigation directly instead of a separate lookup per signup or a hand-rolled dictionary.
    private async Task SendPromotionMessagesAsync(
        TelegramChatId chatId,
        IReadOnlyList<Signup> promoted,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        if (promoted.Count == 0)
        {
            return;
        }

        // An anonymous guest is rare here — invariant 5 means one only survives with a live
        // inviter, and this is the promotion path, not the removal cascade — but still a real,
        // reachable case, so the fallback goes through the strings table like everything else
        // user-visible, never a bare English literal.
        var unnamedGuestLabel = strings.Text("Promotion.UnnamedGuest");
        var who = string.Join(
            ", ",
            promoted.Select(s =>
                WebUtility.HtmlEncode(s.IsMember ? s.Player!.DisplayName : s.GuestName ?? unnamedGuestLabel)
            )
        );

        await _sender.SendAsync(chatId, strings.Text("Promotion.Message", new { Who = who }), null, ct);
    }

    // --- Decline (with confirm, mirroring Drop/ConfirmDrop/Stay) and the no-confirm Finish
    // button. Both captain-only, on a live announcement. ---

    private async Task HandleDeclinePromptAsync(
        Game game,
        Team team,
        Player player,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var telegramUserId = new TelegramUserId(callbackQuery.From.Id);
        if (!await _teamGuard.IsCaptainAsync(team.Id, player.Id, chatId, telegramUserId, ct))
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("NewGame.NotCaptain"), ct);
            return;
        }

        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Decline.ConfirmYes"),
                    CallbackData.Format(CallbackData.ConfirmDecline, game.Id)
                ),
                InlineKeyboardButton.WithCallbackData(
                    strings.Text("Decline.ConfirmNo"),
                    CallbackData.Format(CallbackData.CancelDecline, game.Id)
                ),
            ],
        ]);
        await _sender.SendAsync(
            chatId,
            strings.Text("Decline.ConfirmPrompt", new { Title = WebUtility.HtmlEncode(game.Title) }),
            keyboard,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleConfirmDeclineAsync(
        Game game,
        Team team,
        Player player,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var telegramUserId = new TelegramUserId(callbackQuery.From.Id);
        if (!await _teamGuard.IsCaptainAsync(team.Id, player.Id, chatId, telegramUserId, ct))
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("NewGame.NotCaptain"), ct);
            return;
        }

        await _games.DeclineAsync(game, player.Id, ct);
        await _announcements.RefreshAsync(game, team, ct);
        await _board.RefreshAsync(team, ct);

        await _sender.EditAsync(
            chatId,
            new TelegramMessageId(callbackQuery.Message.MessageId),
            strings.Text("Decline.Declined"),
            null,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleCancelDeclineAsync(CallbackQuery callbackQuery, IStringsFor strings, CancellationToken ct)
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        await _sender.EditAsync(
            chatId,
            new TelegramMessageId(callbackQuery.Message!.MessageId),
            strings.Text("Decline.Cancelled"),
            null,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleFinishButtonAsync(
        Game game,
        Team team,
        Player player,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var telegramUserId = new TelegramUserId(callbackQuery.From.Id);
        if (!await _teamGuard.IsCaptainAsync(team.Id, player.Id, chatId, telegramUserId, ct))
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("NewGame.NotCaptain"), ct);
            return;
        }

        await _games.FinishAsync(game, player.Id, ct);
        await _announcements.RefreshAsync(game, team, ct);
        await _board.RefreshAsync(team, ct);

        await _bot.AnswerCallbackQuery(callbackQuery.Id, strings.Text("Finish.Finished"), cancellationToken: ct);
    }

    // --- Act on behalf of a player ("Manage players", design decision #2 of M9) ---

    private async Task HandleManagePlayersButtonAsync(
        Game game,
        Team team,
        Player player,
        CallbackQuery callbackQuery,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var telegramUserId = new TelegramUserId(callbackQuery.From.Id);
        if (!await _teamGuard.IsCaptainAsync(team.Id, player.Id, chatId, telegramUserId, ct))
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("NewGame.NotCaptain"), ct);
            return;
        }

        await StartDialogAsync(
            team.Id,
            player.Id,
            chatId,
            DialogKinds.ManagePlayers,
            new ManagePlayersDialogData(game.Id),
            ct
        );

        var statuses = await _games.LoadMemberStatusesAsync(game, ct);
        await _sender.SendAsync(
            chatId,
            ManagePlayersRenderer.RenderText(game, statuses, strings),
            ManagePlayersRenderer.RenderKeyboard(statuses, strings),
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleManagePlayersCallbackAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.ChatId == chatId, ct);
        var strings = _strings.For(team.Locale);
        var actor = await _playerBootstrap.GetOrCreateAsync(callbackQuery.From, ct);
        var telegramUserId = new TelegramUserId(callbackQuery.From.Id);

        if (!await _teamGuard.IsCaptainAsync(team.Id, actor.Id, chatId, telegramUserId, ct))
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("NewGame.NotCaptain"), ct);
            return;
        }

        var dialog = await _db.DialogStates.SingleOrDefaultAsync(
            d => d.ChatId == chatId && d.PlayerId == actor.Id && d.Kind == DialogKinds.ManagePlayers,
            ct
        );
        if (dialog is null)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var data = JsonSerializer.Deserialize<ManagePlayersDialogData>(dialog.Data)!;
        var game = await _db.Games.SingleAsync(g => g.Id == data.GameId, ct);
        _ = CallbackData.TryParse(callbackQuery.Data!, out _, out PlayerId targetPlayerId);

        var isSignedUp = await _db.Signups.AnyAsync(
            s => s.GameId == game.Id && s.PlayerId == targetPlayerId && s.CancelledAt == null,
            ct
        );

        if (isSignedUp)
        {
            var result = await _signups.DropAsync(game, targetPlayerId, ct);
            if (!result.IsSuccess)
            {
                await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(result.Error)), ct);
                return;
            }

            AuditRecorder.Record(
                _db,
                team.Id,
                game.Id,
                actor.Id,
                AuditActions.PlayerDroppedOnBehalf,
                new { TargetPlayerId = targetPlayerId.Value },
                _clock
            );
            await _db.SaveChangesAsync(ct);
            await _announcements.RefreshAsync(game, team, ct);

            var outcome = result.Value;
            foreach (var guest in outcome.NamedGuestsNeedingChoice)
            {
                await SendGuestChoicePromptAsync(chatId, guest, strings, ct);
            }

            await SendPromotionMessagesAsync(chatId, outcome.NewlyPromoted, strings, ct);
        }
        else
        {
            var result = await _signups.JoinAsync(game, targetPlayerId, ct);
            if (!result.IsSuccess)
            {
                await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(result.Error)), ct);
                return;
            }

            AuditRecorder.Record(
                _db,
                team.Id,
                game.Id,
                actor.Id,
                AuditActions.PlayerRegisteredOnBehalf,
                new { TargetPlayerId = targetPlayerId.Value },
                _clock
            );
            await _db.SaveChangesAsync(ct);
            await _announcements.RefreshAsync(game, team, ct);
        }

        var statuses = await _games.LoadMemberStatusesAsync(game, ct);
        await _sender.EditAsync(
            chatId,
            new TelegramMessageId(callbackQuery.Message!.MessageId),
            ManagePlayersRenderer.RenderText(game, statuses, strings),
            ManagePlayersRenderer.RenderKeyboard(statuses, strings),
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    // --- Reminder settings (/myreminders) — self-service, no captain check, no dialog. ---

    private async Task HandleMyRemindersCommandAsync(
        Team team,
        PlayerId playerId,
        TelegramChatId chatId,
        CancellationToken ct
    )
    {
        var membership = await _db.Memberships.SingleAsync(m => m.TeamId == team.Id && m.PlayerId == playerId, ct);
        var strings = _strings.For(team.Locale);
        var (text, keyboard) = BuildReminderSettingsView(membership, strings);
        await _sender.SendAsync(chatId, text, keyboard, ct);
    }

    private async Task HandleReminderSettingsCallbackAsync(char verb, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.ChatId == chatId, ct);
        var player = await _playerBootstrap.GetOrCreateAsync(callbackQuery.From, ct);
        var membership = await _db.Memberships.SingleAsync(m => m.TeamId == team.Id && m.PlayerId == player.Id, ct);
        var strings = _strings.For(team.Locale);

        if (verb == CallbackData.CycleReminderChannel)
        {
            _ = CallbackData.TryParse(callbackQuery.Data!, out _, out long slotIndex);
            var next = (ReminderChannel)(((int)ChannelFor(membership, (int)slotIndex) + 1) % 3);
            SetChannel(membership, (int)slotIndex, next);
        }
        else
        {
            membership.RemindWhenReserve = !membership.RemindWhenReserve;
        }

        await _db.SaveChangesAsync(ct);

        var (text, keyboard) = BuildReminderSettingsView(membership, strings);
        await _sender.EditAsync(chatId, new TelegramMessageId(callbackQuery.Message!.MessageId), text, keyboard, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private static (string Text, InlineKeyboardMarkup Keyboard) BuildReminderSettingsView(
        Membership membership,
        IStringsFor strings
    )
    {
        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text(
                        "Reminders.EveningBeforeButton",
                        new { Channel = ChannelLabel(membership.EveningBefore, strings) }
                    ),
                    CallbackData.Format(CallbackData.CycleReminderChannel, 0L)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text(
                        "Reminders.MorningOfButton",
                        new { Channel = ChannelLabel(membership.MorningOf, strings) }
                    ),
                    CallbackData.Format(CallbackData.CycleReminderChannel, 1L)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text(
                        "Reminders.BeforeStartButton",
                        new { Channel = ChannelLabel(membership.BeforeStart, strings) }
                    ),
                    CallbackData.Format(CallbackData.CycleReminderChannel, 2L)
                ),
            ],
            [
                InlineKeyboardButton.WithCallbackData(
                    strings.Text(membership.RemindWhenReserve ? "Reminders.ReserveOn" : "Reminders.ReserveOff"),
                    CallbackData.Format(CallbackData.ToggleReserveReminder, 0L)
                ),
            ],
        ]);

        return (strings.Text("Reminders.Header"), keyboard);
    }

    private static string ChannelLabel(ReminderChannel channel, IStringsFor strings) =>
        channel switch
        {
            ReminderChannel.Off => strings.Text("Reminders.ChannelOff"),
            ReminderChannel.Group => strings.Text("Reminders.ChannelGroup"),
            ReminderChannel.Dm => strings.Text("Reminders.ChannelDm"),
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown reminder channel."),
        };

    private static ReminderChannel ChannelFor(Membership membership, int slotIndex) =>
        slotIndex switch
        {
            0 => membership.EveningBefore,
            1 => membership.MorningOf,
            2 => membership.BeforeStart,
            _ => throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, "Unknown reminder slot index"),
        };

    private static void SetChannel(Membership membership, int slotIndex, ReminderChannel value)
    {
        switch (slotIndex)
        {
            case 0:
                membership.EveningBefore = value;
                break;
            case 1:
                membership.MorningOf = value;
                break;
            case 2:
                membership.BeforeStart = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, "Unknown reminder slot index");
        }
    }

    // --- Captain grant/revoke (/managecaptains) — team-wide, no game context, no dialog. ---

    private async Task HandleManageCaptainsCommandAsync(
        Team team,
        PlayerId playerId,
        TelegramChatId chatId,
        TelegramUserId telegramUserId,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);
        if (!await _teamGuard.IsCaptainAsync(team.Id, playerId, chatId, telegramUserId, ct))
        {
            await _sender.SendAsync(chatId, strings.Text("NewGame.NotCaptain"), null, ct);
            return;
        }

        var members = await LoadMembersAsync(team.Id, ct);
        var (text, keyboard) = BuildManageCaptainsView(members, strings);
        await _sender.SendAsync(chatId, text, keyboard, ct);
    }

    private async Task HandleManageCaptainsCallbackAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = new TelegramChatId(callbackQuery.Message!.Chat.Id);
        var team = await _db.Teams.SingleAsync(t => t.ChatId == chatId, ct);
        var strings = _strings.For(team.Locale);
        var actor = await _playerBootstrap.GetOrCreateAsync(callbackQuery.From, ct);
        var telegramUserId = new TelegramUserId(callbackQuery.From.Id);

        if (!await _teamGuard.IsCaptainAsync(team.Id, actor.Id, chatId, telegramUserId, ct))
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("NewGame.NotCaptain"), ct);
            return;
        }

        _ = CallbackData.TryParse(callbackQuery.Data!, out _, out PlayerId targetPlayerId);
        var membership = await _db.Memberships.SingleAsync(
            m => m.TeamId == team.Id && m.PlayerId == targetPlayerId,
            ct
        );
        membership.IsCaptain = !membership.IsCaptain;

        AuditRecorder.Record(
            _db,
            team.Id,
            null,
            actor.Id,
            membership.IsCaptain ? AuditActions.CaptainGranted : AuditActions.CaptainRevoked,
            new { TargetPlayerId = targetPlayerId.Value },
            _clock
        );
        await _db.SaveChangesAsync(ct);

        var members = await LoadMembersAsync(team.Id, ct);
        var (text, keyboard) = BuildManageCaptainsView(members, strings);
        await _sender.EditAsync(chatId, new TelegramMessageId(callbackQuery.Message!.MessageId), text, keyboard, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task<IReadOnlyList<Membership>> LoadMembersAsync(TeamId teamId, CancellationToken ct) =>
        await _db.Memberships.AsNoTracking().Include(m => m.Player).Where(m => m.TeamId == teamId).ToListAsync(ct);

    private static (string Text, InlineKeyboardMarkup Keyboard) BuildManageCaptainsView(
        IReadOnlyList<Membership> members,
        IStringsFor strings
    )
    {
        var ordered = members.OrderBy(m => m.Player.DisplayName, StringComparer.Ordinal).ToList();
        var rows = ordered
            .Select(m =>
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        strings.Text(
                            m.IsCaptain ? "Captains.RevokeButton" : "Captains.GrantButton",
                            new { Name = m.Player.DisplayName }
                        ),
                        CallbackData.Format(CallbackData.ToggleCaptain, m.PlayerId)
                    ),
                }
            )
            .ToList();

        return (strings.Text("Captains.Header"), new InlineKeyboardMarkup(rows));
    }

    private async Task AnswerAlertAsync(CallbackQuery callbackQuery, string text, CancellationToken ct) =>
        await _bot.AnswerCallbackQuery(callbackQuery.Id, text, showAlert: true, cancellationToken: ct);

    private static string ErrorKey(BusinessError error) =>
        error switch
        {
            BusinessError.AlreadySignedUp => "Signup.AlreadySignedUp",
            BusinessError.NotSignedUp => "Signup.NotSignedUp",
            BusinessError.NotYourGuest => "Signup.NotYourGuest",
            BusinessError.GuestAlreadyResolved => "Signup.GuestAlreadyResolved",
            BusinessError.GameAlreadyFinished => "Signup.GameAlreadyFinished",
            BusinessError.RegistrationClosed => "Signup.RegistrationClosed",
            BusinessError.TeamNotConfigured => "NewGame.NeedsTimeZone",
            BusinessError.NotCaptain => "NewGame.NotCaptain",
            BusinessError.NudgeOnCooldown => "Nudge.OnCooldown",
            BusinessError.GameNotFinished => "Roster.GameNotFinished",
            _ => "Error.Generic",
        };

    private static bool IsValidTimeZone(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
    }
}
