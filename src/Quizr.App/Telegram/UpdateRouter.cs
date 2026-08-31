using System.Globalization;
using System.Net;
using System.Text;
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
using Telegram.Bot.Requests;
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
    private readonly ITeamService _teams;
    private readonly IDialogService _dialogs;
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
        ITeamService teams,
        IDialogService dialogs,
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
        _teams = teams;
        _dialogs = dialogs;
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
                await HandleMyChatMemberAsync(update.MyChatMember!, ct);
                break;

            case UpdateType.ChatMember:
                // CLAUDE.md mentions using this to mark departures from Membership, which has
                // no such field yet and isn't part of M3 — subscribed via allowed_updates so
                // nothing has to change when that's built.
                _logger.LogDebug("Ignoring chat_member update for chat {ChatId}", update.ChatMember!.Chat.Id);
                break;

            // A group's chat id changing is announced as a Message with no text — silently
            // discarded by the next case's own filter, so it has to be caught here first.
            // See TeamChatMigration.
            case UpdateType.Message when update.Message?.MigrateToChatId is { } newChatId:
                await HandleChatMigratedAsync(
                    new TelegramChatId(update.Message!.Chat.Id),
                    new TelegramChatId(newChatId),
                    ct
                );
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

    // my_chat_member covers two unrelated things. In a group it is the bot being added or
    // removed, which is the only way a Team is born. In a private chat Telegram sends it only
    // when someone blocks or unblocks the bot — the one signal that says whether a DM would
    // land, and emphatically not a team: a private chat has no title and no members, so
    // routing it into TeamBootstrapService minted a "Quiz team" keyed on the person's own chat
    // id and greeted them with the group setup message the first time they unblocked.
    private async Task HandleMyChatMemberAsync(ChatMemberUpdated update, CancellationToken ct)
    {
        if (update.Chat.Type != ChatType.Private)
        {
            await _teamBootstrap.HandleMyChatMemberAsync(update, ct);
            return;
        }

        var player = await _playerBootstrap.GetOrCreateAsync(update.From, ct);
        await _playerBootstrap.SetDmEnabledAsync(player, CanReceiveDms(update.NewChatMember.Status), ct);
    }

    // Blocking the bot reports as Kicked; unblocking reports as Member. Anything else in a
    // private chat is not a state this can act on, so it reads as "no DMs" rather than
    // guessing.
    private static bool CanReceiveDms(ChatMemberStatus status) => status == ChatMemberStatus.Member;

    private async Task HandleChatMigratedAsync(TelegramChatId oldChatId, TelegramChatId newChatId, CancellationToken ct)
    {
        var team = await _db.Teams.SingleOrDefaultAsync(t => t.ChatId == oldChatId, ct);
        if (team is null)
        {
            return;
        }

        await TeamChatMigration.ApplyAsync(_db, team, newChatId, _clock, _logger, ct);
    }

    private async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        var chatId = await ResolveChatIdAsync(new TelegramChatId(message.Chat.Id), ct);
        var team = await _db.Teams.ByChatId(chatId).SingleOrDefaultAsync(ct);

        Player? player = null;
        Actor? actor = null;
        if (message.From is not null)
        {
            player = await _playerBootstrap.GetOrCreateAsync(message.From, ct);
            actor = new Actor(player.Id, new TelegramUserId(message.From.Id));
            if (team is not null)
            {
                await _playerBootstrap.EnsureMembershipAsync(team.Id, player.Id, ct);
            }

            // Them writing in their own chat with the bot is the proof that a DM would land —
            // the ordinary way this becomes true, since most people press Start and never
            // block anything.
            if (message.Chat.Type == ChatType.Private)
            {
                await _playerBootstrap.SetDmEnabledAsync(player, true, ct);
            }
        }

        if (player is not null && !message.Text!.StartsWith('/'))
        {
            var dialog = await _dialogs.LoadAsync(chatId, player.Id, ct);
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
                var startLocale = LocaleResolver.Resolve(
                    message.Chat.Type,
                    player?.Locale,
                    message.From?.LanguageCode,
                    team?.Locale ?? "en"
                );
                await _sender.SendAsync(chatId, _strings.For(startLocale).Text("Start.Greeting"), null, ct);
                break;

            case "/help":
                var helpLocale = LocaleResolver.Resolve(
                    message.Chat.Type,
                    player?.Locale,
                    message.From?.LanguageCode,
                    team?.Locale ?? "en"
                );
                await _sender.SendAsync(chatId, BuildHelpText(_strings.For(helpLocale)), null, ct);
                break;

            case "/cancel" when team is not null && player is not null:
                await HandleCancelCommandAsync(team, player, chatId, ct);
                break;

            case "/settimezone" when team is not null && actor is { } a:
                await HandleSetTimeZoneAsync(team, a, chatId, argument, ct);
                break;

            case "/setlanguage" when team is not null && actor is { } a:
                await HandleSetLanguageAsync(team, a, chatId, argument, ct);
                break;

            case "/mylanguage" when player is not null:
                await HandleSetMyLanguageAsync(player, chatId, argument, ct);
                break;

            case "/setreminders" when team is not null && actor is { } a:
                await HandleSetRemindersAsync(team, a, chatId, argument, ct);
                break;

            case "/newgame" when team is not null && actor is { } a:
                await HandleNewGameCommandAsync(team, a, chatId, ct);
                break;

            case "/newfranchise" when team is not null && actor is { } a:
                await HandleNewFranchiseCommandAsync(team, a, chatId, ct);
                break;

            case "/editfranchise" when team is not null && actor is { } a:
                await HandleEditFranchiseCommandAsync(team, a, chatId, ct);
                break;

            case "/editgame" when team is not null && actor is { } a:
                await HandleEditGameCommandAsync(team, a, chatId, ct);
                break;

            case "/myreminders" when team is not null && actor is { } a:
                await HandleMyRemindersCommandAsync(team, a, chatId, ct);
                break;

            case "/managecaptains" when team is not null && actor is { } a:
                await HandleManageCaptainsCommandAsync(team, a, chatId, ct);
                break;
        }
    }

    private async Task HandleSetTimeZoneAsync(
        Team team,
        Actor actor,
        TelegramChatId chatId,
        string? argument,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

        // Parsed at the boundary, not in the service: a bad timezone string echoes the input
        // back in its own message, which is a rendering concern, not a business failure.
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

        var result = await _teams.SetTimeZoneAsync(team, actor, argument, ct);
        if (!result.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(result.Error)), null, ct);
            return;
        }

        await _sender.SendAsync(chatId, strings.Text("Setup.TimeZoneSet", new { TimeZoneId = argument }), null, ct);

        // The team is now operational — invariant 12 says only the Board is ever pinned, so
        // it earns its pin from the moment the team can have games, not from the first one.
        await _board.RefreshAsync(team, ct);
    }

    // Group messages use the team's language (CLAUDE.md) — a captain setting, like the
    // timezone.
    private async Task HandleSetLanguageAsync(
        Team team,
        Actor actor,
        TelegramChatId chatId,
        string? argument,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

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

        var result = await _teams.SetLocaleAsync(team, actor, argument, ct);
        if (!result.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(result.Error)), null, ct);
            return;
        }

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
            await _sender.SendEphemeralAsync(
                chatId,
                player.TelegramUserId,
                strings.Text("Setup.LanguageInvalid", new { Input = argument ?? "" }),
                null,
                null,
                ct
            );
            return;
        }

        player.Locale = argument;
        await _db.SaveChangesAsync(ct);

        await _sender.SendEphemeralAsync(
            chatId,
            player.TelegramUserId,
            _strings.For(player.Locale).Text("Setup.MyLanguageSet", new { Locale = player.Locale }),
            null,
            null,
            ct
        );
    }

    // All three reminder slots at once, not one setting per command like /settimezone — they
    // read as one coherent schedule, and asking for all three together avoids the ambiguity
    // of a single-field edit leaving the other two silently unexplained.
    private async Task HandleSetRemindersAsync(
        Team team,
        Actor actor,
        TelegramChatId chatId,
        string? argument,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

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

        var result = await _teams.SetRemindersAsync(team, actor, eveningBeforeAt, morningOfAt, beforeStartLead, ct);
        if (!result.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(result.Error)), null, ct);
            return;
        }

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

    private async Task HandleNewGameCommandAsync(Team team, Actor actor, TelegramChatId chatId, CancellationToken ct)
    {
        var strings = _strings.For(team.Locale);

        var options = await _games.LoadNewGameOptionsAsync(team, actor, ct);
        if (!options.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(options.Error)), null, ct);
            return;
        }

        var franchises = options.Value;
        await _dialogs.StartAsync(
            team.Id,
            actor.PlayerId,
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
            CancelButton.Row(strings),
        };

        await _sender.SendAsync(chatId, strings.Text("NewGame.ChooseBranch"), new InlineKeyboardMarkup(keyboard), ct);
    }

    private async Task HandleNewFranchiseCommandAsync(
        Team team,
        Actor actor,
        TelegramChatId chatId,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

        var started = await _dialogs.StartForCaptainAsync(
            team,
            actor,
            DialogKinds.NewFranchise,
            new NewFranchiseDialogData(NewFranchiseDialogData.AskName),
            ct
        );
        if (!started.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(started.Error)), null, ct);
            return;
        }

        await SendPromptAsync(
            started.Value,
            chatId,
            strings.Text("Franchise.AskName"),
            CancelButton.Keyboard(strings),
            ct
        );
    }

    private async Task HandleEditFranchiseCommandAsync(
        Team team,
        Actor actor,
        TelegramChatId chatId,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

        var result = await _franchises.LoadEditableAsync(team, actor, ct);
        if (!result.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(result.Error)), null, ct);
            return;
        }

        var franchises = result.Value;
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

    private async Task HandleEditGameCommandAsync(Team team, Actor actor, TelegramChatId chatId, CancellationToken ct)
    {
        var strings = _strings.For(team.Locale);

        var result = await _games.LoadEditableGamesAsync(team, actor, ct);
        if (!result.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(result.Error)), null, ct);
            return;
        }

        var games = result.Value;
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

    // Everything a callback handler needs about who tapped what, resolved once. Before this,
    // each of the seven callback sub-dispatchers opened with the same six lines, and every
    // handler beneath them re-derived the chat id from the message a second time.
    private sealed record CallbackScope(
        TelegramChatId ChatId,
        Team Team,
        IStringsFor Strings,
        Player Player,
        Actor Actor,
        MessageRef Message
    );

    private async Task<CallbackScope?> ResolveScopeAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = await ResolveChatIdAsync(new TelegramChatId(callbackQuery.Message!.Chat.Id), ct);
        var team = await _db.Teams.ByChatId(chatId).SingleOrDefaultAsync(ct);
        return team is null ? null : await ScopeForAsync(team, callbackQuery, ct);
    }

    // For the game callbacks, where the team comes from the game rather than from a chat-id
    // lookup — team.ChatId is by definition what ResolveChatIdAsync would have returned, so
    // this path needs no Teams query of its own at all.
    private async Task<CallbackScope> ScopeForAsync(Team team, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var player = await _playerBootstrap.GetOrCreateAsync(callbackQuery.From, ct);
        await _playerBootstrap.EnsureMembershipAsync(team.Id, player.Id, ct);

        var receiver = new TelegramUserId(callbackQuery.From.Id);

        // A tap on an ephemeral message reports Message.Id as 0 and carries the real handle on
        // EphemeralMessageId, so which of the two this is decides how anything downstream can
        // edit it back.
        var message = callbackQuery.Message!.EphemeralMessageId is { } ephemeralId
            ? MessageRef.Ephemeral(team.ChatId, new TelegramMessageId(ephemeralId), receiver)
            : MessageRef.Ordinary(team.ChatId, new TelegramMessageId(callbackQuery.Message.MessageId));

        return new CallbackScope(
            team.ChatId,
            team,
            _strings.For(team.Locale),
            player,
            new Actor(player.Id, receiver),
            message
        );
    }

    // Sends a prompt carrying a Cancel or Skip+Cancel keyboard and remembers its message id on
    // the dialog. HandleDialogReplyAsync reads that back on the next reply to strip this
    // keyboard once the step it belongs to has actually been answered — see its own comment.
    private async Task SendPromptAsync(
        DialogState dialog,
        TelegramChatId chatId,
        string text,
        InlineKeyboardMarkup keyboard,
        CancellationToken ct
    ) => await _dialogs.SetPromptMessageAsync(dialog, await _sender.SendAsync(chatId, text, keyboard, ct), ct);

    // The picker a captain just tapped is spent the moment the wizard moves past it: the branch
    // list once a franchise is chosen, the date list once a date is. Left alone its buttons sit
    // in the chat looking live long after the game exists — and a stale franchise button is
    // worse than merely dead, since with the NewGame dialog gone it falls through to the
    // /editfranchise branch and opens an unrelated dialog. Called after the next screen is
    // sent, so a step that didn't actually advance keeps the keyboard the captain still needs.
    private async Task RetirePickerAsync(CallbackScope scope, CancellationToken ct) =>
        await _sender.RemoveKeyboardAsync(scope.Message, ct);

    // A chat id pulled straight off an incoming message or callback query can still carry a
    // migrated chat's pre-migration shape (CLAUDE.md's Telegram-migration note) — TeamLookup
    // already covers Team's own lookups by matching OldChatId, but the chat id constructed at
    // each call site (there's no single point they all flow through — see HandleDropPromptAsync
    // for one of many) needs the same fallback before it's used for anything else, sending
    // included. Cheap in the common case: a real supergroup id never reaches the database
    // lookup at all.
    private async Task<TelegramChatId> ResolveChatIdAsync(TelegramChatId chatId, CancellationToken ct)
    {
        if (IsSupergroupShaped(chatId))
        {
            return chatId;
        }

        var team = await _db.Teams.ByChatId(chatId).SingleOrDefaultAsync(ct);
        return team?.ChatId ?? chatId;
    }

    // Telegram's own convention: a supergroup or channel id is negative and prefixed "-100";
    // a still-basic-group id — or a migrated one's now-stale pre-migration id — is negative
    // without it; a private chat's id is positive and never migrates.
    private static bool IsSupergroupShaped(TelegramChatId chatId) =>
        chatId.Value >= 0
        || chatId.Value.ToString(CultureInfo.InvariantCulture).StartsWith("-100", StringComparison.Ordinal);

    // --- Dialog replies (currently: naming a guest) ---

    // A dialog is looked up by (chat, player), so whoever is replying is by construction the
    // person who owns it — and HandleSkipAsync copies the tapper onto its synthetic message
    // for this reason, since a skipped step can still reach a captain-gated service.
    private static Actor ActorFor(DialogState dialog, Message message) =>
        new(dialog.PlayerId, new TelegramUserId(message.From!.Id));

    private async Task HandleDialogReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        // Captured before dispatch: if the step below re-shows the same prompt (a validation
        // error), MessageId comes back unchanged and nothing gets stripped — the keyboard is
        // still exactly what the captain needs to retry or cancel. It's only stale once the
        // dialog has actually moved past it.
        var previousMessageId = dialog.MessageId;
        bool advanced;

        switch (dialog.Kind)
        {
            case DialogKinds.NameGuest:
                advanced = await HandleGuestNameReplyAsync(dialog, message, ct);
                break;

            case DialogKinds.NewFranchise:
                advanced = await HandleNewFranchiseReplyAsync(dialog, message, ct);
                break;

            case DialogKinds.EditFranchise:
                advanced = await HandleEditFranchiseReplyAsync(dialog, message, ct);
                break;

            case DialogKinds.NewGame:
                advanced = await HandleNewGameReplyAsync(dialog, message, ct);
                break;

            case DialogKinds.EditGame:
                advanced = await HandleEditGameReplyAsync(dialog, message, ct);
                break;

            case DialogKinds.AddVenuePlayer:
                advanced = await HandleAddVenuePlayerReplyAsync(dialog, message, ct);
                break;

            case DialogKinds.AddTeamGuest:
                advanced = await HandleAddTeamGuestReplyAsync(dialog, message, ct);
                break;

            // Callback-only dialogs — no text-reply step exists for either, so a reply while
            // one is active is discarded exactly like a genuinely unrecognised kind.
            case DialogKinds.Nudge:
            case DialogKinds.ManagePlayers:
            default:
                _logger.LogWarning("Discarding a dialog with unrecognised kind {Kind}", dialog.Kind);
                await _dialogs.ClearAsync(dialog, ct);
                advanced = true;
                break;
        }

        if (advanced && previousMessageId is { } staleMessageId)
        {
            await _sender.RemoveKeyboardAsync(dialog.ChatId, staleMessageId, ct);
        }
    }

    // --- Franchise creation/editing ---

    // Returns whether the step advanced (the prompt it answered is now stale) versus re-showing
    // the same prompt after a validation error (still live, nothing to strip) — see
    // HandleDialogReplyAsync, which strips the previous prompt's keyboard only when this is true.
    private async Task<bool> HandleNewFranchiseReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<NewFranchiseDialogData>(dialog.Data)!;
        var chatId = dialog.ChatId;
        var team = await _db.Teams.SingleAsync(t => t.Id == dialog.TeamId, ct);
        var strings = _strings.For(team.Locale);
        var input = message.Text!;

        switch (data.Step)
        {
            case NewFranchiseDialogData.AskName:
                if (!FieldParsing.TryParseText(input, out var name, out var nameError))
                {
                    await _sender.SendAsync(chatId, strings.Text(nameError!), null, ct);
                    return false;
                }

                await _dialogs.SaveDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewFranchiseDialogData.AskVenue,
                        Name = name,
                    },
                    ct
                );
                await SendPromptAsync(
                    dialog,
                    chatId,
                    strings.Text("Franchise.AskVenue"),
                    SkipButton.KeyboardWithCancel(strings),
                    ct
                );
                return true;

            case NewFranchiseDialogData.AskVenue:
                _ = FieldParsing.TryParseOptionalText(input, out var venue, out _);

                await _dialogs.SaveDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewFranchiseDialogData.AskCapacity,
                        Venue = venue,
                    },
                    ct
                );
                await SendPromptAsync(
                    dialog,
                    chatId,
                    strings.Text("Franchise.AskCapacity"),
                    SkipButton.KeyboardWithCancel(strings),
                    ct
                );
                return true;

            case NewFranchiseDialogData.AskCapacity:
                if (!FieldParsing.TryParseOptionalCapacity(input, out var capacity, out var capacityError))
                {
                    await _sender.SendAsync(chatId, strings.Text(capacityError!), null, ct);
                    return false;
                }

                await _dialogs.SaveDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewFranchiseDialogData.AskPrice,
                        Capacity = capacity,
                    },
                    ct
                );
                await SendPromptAsync(
                    dialog,
                    chatId,
                    strings.Text("Franchise.AskPrice"),
                    SkipButton.KeyboardWithCancel(strings),
                    ct
                );
                return true;

            case NewFranchiseDialogData.AskPrice:
                if (!FieldParsing.TryParsePrice(input, out var price, out var priceError))
                {
                    await _sender.SendAsync(chatId, strings.Text(priceError!), null, ct);
                    return false;
                }

                await _dialogs.SaveDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewFranchiseDialogData.AskSchedule,
                        Price = price,
                    },
                    ct
                );
                await SendPromptAsync(
                    dialog,
                    chatId,
                    strings.Text("Franchise.AskSchedule"),
                    SkipButton.KeyboardWithCancel(strings),
                    ct
                );
                return true;

            case NewFranchiseDialogData.AskSchedule:
                if (!FieldParsing.TryParseSchedule(input, team.Locale, out var schedule, out var scheduleError))
                {
                    await _sender.SendAsync(chatId, strings.Text(scheduleError!), null, ct);
                    return false;
                }

                var createResult = await _franchises.CreateAsync(
                    team,
                    ActorFor(dialog, message),
                    data.Name!,
                    data.Venue,
                    data.Capacity,
                    data.Price,
                    schedule,
                    ct
                );
                if (!createResult.IsSuccess)
                {
                    // The wizard has no "go back and change an earlier answer" — the name was
                    // collected steps ago, and leaving the dialog at AskSchedule would
                    // misinterpret a retried name as a schedule. Clearing it sends the captain
                    // back to a clean /newfranchise instead.
                    await _dialogs.ClearAsync(dialog, ct);
                    await _sender.SendAsync(chatId, strings.Text(ErrorKey(createResult.Error)), null, ct);
                    return true;
                }

                var franchise = createResult.Value;
                await _sender.SendAsync(chatId, strings.Text("Franchise.Created"), null, ct);

                // The field-picker keyboard below is the same one /editfranchise shows, and its
                // buttons (besides Archive, which is self-sufficient) only mean something while
                // an EditFranchise dialog is active — without starting one here, they'd tap into
                // nothing.
                await _dialogs.StartAsync(
                    team.Id,
                    dialog.PlayerId,
                    chatId,
                    DialogKinds.EditFranchise,
                    new EditFranchiseDialogData(franchise.Id, null),
                    ct
                );
                await _sender.SendAsync(
                    chatId,
                    FranchiseRenderer.RenderSummary(franchise, strings),
                    FranchiseRenderer.RenderFieldPicker(franchise, strings),
                    ct
                );
                return true;

            default:
                throw new ArgumentOutOfRangeException(nameof(dialog), data.Step, "Unknown NewFranchise dialog step");
        }
    }

    private async Task<bool> HandleEditFranchiseReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<EditFranchiseDialogData>(dialog.Data)!;
        var chatId = dialog.ChatId;
        var team = await _db.Teams.SingleAsync(t => t.Id == dialog.TeamId, ct);
        var strings = _strings.For(team.Locale);

        if (data.FieldIndex is not { } fieldIndex)
        {
            await _sender.SendAsync(chatId, strings.Text("Franchise.PickFieldFirst"), null, ct);
            return false;
        }

        var actor = ActorFor(dialog, message);
        var franchise = await _db.Franchises.SingleAsync(f => f.Id == data.FranchiseId, ct);
        var input = message.Text!;
        string? errorKey;

        switch (fieldIndex)
        {
            case EditFranchiseDialogData.Name:
                if (FieldParsing.TryParseText(input, out var name, out errorKey))
                {
                    errorKey = FailureKey(await _franchises.SetNameAsync(franchise, team, actor, name, ct));
                }
                break;

            case EditFranchiseDialogData.Venue:
                if (FieldParsing.TryParseOptionalText(input, out var venue, out errorKey))
                {
                    errorKey = FailureKey(await _franchises.SetVenueAsync(franchise, team, actor, venue, ct));
                }
                break;

            case EditFranchiseDialogData.Capacity:
                if (FieldParsing.TryParseOptionalCapacity(input, out var capacity, out errorKey))
                {
                    errorKey = FailureKey(await _franchises.SetCapacityAsync(franchise, team, actor, capacity, ct));
                }
                break;

            case EditFranchiseDialogData.Price:
                if (FieldParsing.TryParsePrice(input, out var price, out errorKey))
                {
                    errorKey = FailureKey(await _franchises.SetPriceAsync(franchise, team, actor, price, ct));
                }
                break;

            case EditFranchiseDialogData.Schedule:
                if (FieldParsing.TryParseSchedule(input, team.Locale, out var schedule, out errorKey))
                {
                    errorKey = FailureKey(await _franchises.SetScheduleAsync(franchise, team, actor, schedule, ct));
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(dialog), fieldIndex, "Unknown EditFranchise field index");
        }

        if (errorKey is not null)
        {
            await _sender.SendAsync(chatId, strings.Text(errorKey), null, ct);
            return false;
        }

        // Reset FieldIndex rather than removing the dialog — the field-picker keyboard below
        // is about to be shown again, and its buttons (besides Archive) only mean something
        // with an EditFranchise dialog behind them. Removing it here left every edit after the
        // first tapping into nothing. The dialog only ends via the picker's own Done button.
        await _dialogs.SaveDataAsync(dialog, data with { FieldIndex = null }, ct);

        await _sender.SendAsync(chatId, strings.Text("Franchise.Updated"), null, ct);
        await _sender.SendAsync(
            chatId,
            FranchiseRenderer.RenderSummary(franchise, strings),
            FranchiseRenderer.RenderFieldPicker(franchise, strings),
            ct
        );
        return true;
    }

    // --- Game creation/editing ---

    private async Task<bool> HandleNewGameReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<NewGameDialogData>(dialog.Data)!;
        var chatId = dialog.ChatId;
        var team = await _db.Teams.SingleAsync(t => t.Id == dialog.TeamId, ct);
        var strings = _strings.For(team.Locale);
        var input = message.Text!;

        switch (data.Step)
        {
            case NewGameDialogData.FranchiseCustomDate:
                if (!FieldParsing.TryParseDate(input, out var customDate, out var customDateError))
                {
                    await _sender.SendAsync(chatId, strings.Text(customDateError!), null, ct);
                    return false;
                }

                await _dialogs.SaveDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewGameDialogData.FranchiseCustomTime,
                        Date = customDate,
                    },
                    ct
                );
                await SendPromptAsync(
                    dialog,
                    chatId,
                    strings.Text("NewGame.AskTime"),
                    CancelButton.Keyboard(strings),
                    ct
                );
                return true;

            case NewGameDialogData.FranchiseCustomTime:
                if (!FieldParsing.TryParseTime(input, out var customTime, out var customTimeError))
                {
                    await _sender.SendAsync(chatId, strings.Text(customTimeError!), null, ct);
                    return false;
                }

                var customConfirmData = data with { Step = NewGameDialogData.Confirm, Time = customTime };
                await _dialogs.SaveDataAsync(dialog, customConfirmData, ct);
                await SendConfirmScreenAsync(dialog, chatId, customConfirmData, strings, ct);
                return true;

            case NewGameDialogData.OneOffTitle:
                if (!FieldParsing.TryParseText(input, out var title, out var titleError))
                {
                    await _sender.SendAsync(chatId, strings.Text(titleError!), null, ct);
                    return false;
                }

                await _dialogs.SaveDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewGameDialogData.OneOffVenue,
                        Title = title,
                    },
                    ct
                );
                await SendPromptAsync(
                    dialog,
                    chatId,
                    strings.Text("NewGame.AskVenue"),
                    CancelButton.Keyboard(strings),
                    ct
                );
                return true;

            case NewGameDialogData.OneOffVenue:
                if (!FieldParsing.TryParseText(input, out var venue, out var venueError))
                {
                    await _sender.SendAsync(chatId, strings.Text(venueError!), null, ct);
                    return false;
                }

                await _dialogs.SaveDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewGameDialogData.OneOffDate,
                        Venue = venue,
                    },
                    ct
                );
                await SendPromptAsync(
                    dialog,
                    chatId,
                    strings.Text("NewGame.AskDate"),
                    CancelButton.Keyboard(strings),
                    ct
                );
                return true;

            case NewGameDialogData.OneOffDate:
                if (!FieldParsing.TryParseDate(input, out var oneOffDate, out var dateError))
                {
                    await _sender.SendAsync(chatId, strings.Text(dateError!), null, ct);
                    return false;
                }

                await _dialogs.SaveDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewGameDialogData.OneOffTime,
                        Date = oneOffDate,
                    },
                    ct
                );
                await SendPromptAsync(
                    dialog,
                    chatId,
                    strings.Text("NewGame.AskTime"),
                    CancelButton.Keyboard(strings),
                    ct
                );
                return true;

            case NewGameDialogData.OneOffTime:
                if (!FieldParsing.TryParseTime(input, out var time, out var timeError))
                {
                    await _sender.SendAsync(chatId, strings.Text(timeError!), null, ct);
                    return false;
                }

                await _dialogs.SaveDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewGameDialogData.OneOffCapacity,
                        Time = time,
                    },
                    ct
                );
                await SendPromptAsync(
                    dialog,
                    chatId,
                    strings.Text("NewGame.AskCapacity"),
                    CancelButton.Keyboard(strings),
                    ct
                );
                return true;

            case NewGameDialogData.OneOffCapacity:
                if (!FieldParsing.TryParseCapacity(input, out var capacity, out var capacityError))
                {
                    await _sender.SendAsync(chatId, strings.Text(capacityError!), null, ct);
                    return false;
                }

                await _dialogs.SaveDataAsync(
                    dialog,
                    data with
                    {
                        Step = NewGameDialogData.OneOffPrice,
                        Capacity = capacity,
                    },
                    ct
                );
                await SendPromptAsync(
                    dialog,
                    chatId,
                    strings.Text("NewGame.AskPrice"),
                    SkipButton.KeyboardWithCancel(strings),
                    ct
                );
                return true;

            case NewGameDialogData.OneOffPrice:
                if (!FieldParsing.TryParsePrice(input, out var price, out var priceError))
                {
                    await _sender.SendAsync(chatId, strings.Text(priceError!), null, ct);
                    return false;
                }

                var confirmData = data with { Step = NewGameDialogData.Confirm, Price = price };
                await _dialogs.SaveDataAsync(dialog, confirmData, ct);
                await SendConfirmScreenAsync(dialog, chatId, confirmData, strings, ct);
                return true;

            case NewGameDialogData.EditingField:
                return await HandleNewGameFieldOverrideReplyAsync(dialog, data, input, chatId, strings, ct);

            case NewGameDialogData.ChooseBranch:
            case NewGameDialogData.PickDate:
            case NewGameDialogData.Confirm:
                await _sender.SendAsync(chatId, strings.Text("NewGame.UseButtons"), null, ct);
                return false;

            default:
                throw new ArgumentOutOfRangeException(nameof(dialog), data.Step, "Unknown NewGame dialog step");
        }
    }

    private async Task<bool> HandleNewGameFieldOverrideReplyAsync(
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
            return false;
        }

        updated = updated with { Step = NewGameDialogData.Confirm, EditingFieldIndex = null };
        await _dialogs.SaveDataAsync(dialog, updated, ct);
        await SendConfirmScreenAsync(dialog, chatId, updated, strings, ct);
        return true;
    }

    private async Task SendConfirmScreenAsync(
        DialogState dialog,
        TelegramChatId chatId,
        NewGameDialogData data,
        IStringsFor strings,
        CancellationToken ct
    ) =>
        await SendPromptAsync(
            dialog,
            chatId,
            GameConfirmRenderer.RenderText(data, strings),
            GameConfirmRenderer.RenderKeyboard(strings),
            ct
        );

    private async Task<bool> HandleEditGameReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<EditGameDialogData>(dialog.Data)!;
        var chatId = dialog.ChatId;
        var team = await _db.Teams.SingleAsync(t => t.Id == dialog.TeamId, ct);
        var strings = _strings.For(team.Locale);

        if (data.FieldIndex is not { } fieldIndex)
        {
            await _sender.SendAsync(chatId, strings.Text("EditGame.PickFieldFirst"), null, ct);
            return false;
        }

        var actor = ActorFor(dialog, message);
        var game = await _db.Games.SingleAsync(g => g.Id == data.GameId, ct);
        var input = message.Text!;
        string? errorKey;
        IReadOnlyList<Signup> promoted = [];

        switch (fieldIndex)
        {
            case EditGameDialogData.Title:
                if (FieldParsing.TryParseText(input, out var title, out errorKey))
                {
                    errorKey = FailureKey(await _games.SetTitleAsync(game, team, actor, title, ct));
                }
                break;

            case EditGameDialogData.Venue:
                if (FieldParsing.TryParseText(input, out var venue, out errorKey))
                {
                    errorKey = FailureKey(await _games.SetVenueAsync(game, team, actor, venue, ct));
                }
                break;

            case EditGameDialogData.Capacity:
                if (FieldParsing.TryParseCapacity(input, out var capacity, out errorKey))
                {
                    var resized = await _games.SetCapacityAsync(game, team, actor, capacity, ct);
                    errorKey = FailureKey(resized);
                    promoted = resized.IsSuccess ? resized.Value : [];
                }
                break;

            case EditGameDialogData.Price:
                if (FieldParsing.TryParsePrice(input, out var price, out errorKey))
                {
                    errorKey = FailureKey(await _games.SetPriceAsync(game, team, actor, price, ct));
                }
                break;

            case EditGameDialogData.Notes:
                if (FieldParsing.TryParseOptionalText(input, out var notes, out errorKey))
                {
                    errorKey = FailureKey(await _games.SetNotesAsync(game, team, actor, notes, ct));
                }
                break;

            case EditGameDialogData.StartTime:
                if (FieldParsing.TryParseTime(input, out var time, out errorKey))
                {
                    errorKey = FailureKey(await _games.SetStartTimeAsync(game, team, actor, time, ct));
                }
                break;

            case EditGameDialogData.Tags:
                if (FieldParsing.TryParseTags(input, out var tags, out errorKey))
                {
                    errorKey = FailureKey(await _games.SetTagsAsync(game, team, actor, tags, ct));
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(dialog), fieldIndex, "Unknown EditGame field index");
        }

        if (errorKey is not null)
        {
            await _sender.SendAsync(chatId, strings.Text(errorKey), null, ct);
            return false;
        }

        await _dialogs.ClearAsync(dialog, ct);

        await _announcements.RefreshAsync(game, team, ct);
        await _sender.SendAsync(chatId, strings.Text("EditGame.Updated"), null, ct);

        await SendPromotionMessagesAsync(chatId, promoted, strings, ct);
        return true;
    }

    private async Task<bool> HandleAddVenuePlayerReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<AddVenuePlayerDialogData>(dialog.Data)!;
        var chatId = dialog.ChatId;
        var team = await _db.Teams.SingleAsync(t => t.Id == dialog.TeamId, ct);
        var strings = _strings.For(team.Locale);

        if (!FieldParsing.TryParseText(message.Text!, out var name, out var errorKey))
        {
            await _sender.SendAsync(chatId, strings.Text(errorKey!), null, ct);
            return false;
        }

        var actor = ActorFor(dialog, message);
        var game = await _db.Games.SingleAsync(g => g.Id == data.GameId, ct);
        var result = await _participations.AddVenueAssignedAsync(game, team, actor, name, ct);

        await _dialogs.ClearAsync(dialog, ct);

        if (!result.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(result.Error)), null, ct);
            return true;
        }

        var roster = await _participations.LoadRosterAsync(game, team, actor, ct);
        if (!roster.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(roster.Error)), null, ct);
            return true;
        }

        await SendRosterViewAsync(game, roster.Value, chatId, actor.TelegramUserId, strings, ct);
        return true;
    }

    private async Task<bool> HandleAddTeamGuestReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<AddTeamGuestDialogData>(dialog.Data)!;
        var chatId = dialog.ChatId;
        var team = await _db.Teams.SingleAsync(t => t.Id == dialog.TeamId, ct);
        var strings = _strings.For(team.Locale);

        // A team guest is always named (invariant 5) — TryParseText, not TryParseOptionalText,
        // since there's no owner for an anonymous one to fall back to identifying by.
        if (!FieldParsing.TryParseText(message.Text!, out var name, out var errorKey))
        {
            await _sender.SendAsync(chatId, strings.Text(errorKey!), null, ct);
            return false;
        }

        var actor = ActorFor(dialog, message);
        var game = await _db.Games.SingleAsync(g => g.Id == data.GameId, ct);
        var result = await _signups.AddTeamGuestAsync(game, team, actor, name, ct);

        // Not IDialogService.ClearAsync: the audit entry below has to ride along in the same
        // SaveChangesAsync (CLAUDE.md), and clearing would commit the removal ahead of it.
        _db.DialogStates.Remove(dialog);

        if (!result.IsSuccess)
        {
            await _db.SaveChangesAsync(ct);
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(result.Error)), null, ct);
            return true;
        }

        AuditRecorder.Record(
            _db,
            team.Id,
            game.Id,
            dialog.PlayerId,
            AuditActions.TeamGuestAdded,
            new { Name = name },
            _clock
        );
        await _db.SaveChangesAsync(ct);
        await _announcements.RefreshAsync(game, team, ct);

        var guests = await _signups.LoadAllLiveGuestsAsync(game, team, actor, ct);
        if (!guests.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(guests.Error)), null, ct);
            return true;
        }

        var (text, keyboard) = BuildManageGuestsView(game, guests.Value, strings);
        await _sender.SendEphemeralAsync(chatId, actor.TelegramUserId, text, keyboard, null, ct);
        return true;
    }

    private async Task<bool> HandleGuestNameReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize<GuestNameDialogData>(dialog.Data)!;
        var chatId = dialog.ChatId;
        var team = await _db.Teams.SingleAsync(t => t.Id == dialog.TeamId, ct);
        var strings = _strings.For(team.Locale);

        var result = await _signups.NameGuestAsync(data.SignupId, dialog.PlayerId, message.Text!.Trim(), ct);

        await _dialogs.ClearAsync(dialog, ct);

        if (!result.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(result.Error)), null, ct);
            return true;
        }

        var game = await _db.Games.SingleAsync(g => g.Id == result.Value.GameId, ct);
        await _announcements.RefreshAsync(game, team, ct);

        await _sender.SendAsync(
            chatId,
            strings.Text("Guest.Named", new { Name = WebUtility.HtmlEncode(result.Value.GuestName) }),
            null,
            ct
        );
        return true;
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
            case CallbackData.ManageGuests:
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
            case CallbackData.CustomDate:
            case CallbackData.EditField:
            case CallbackData.Confirm:
            case CallbackData.CancelDialog:
            case CallbackData.PickGameToEdit:
                await HandleCaptainFlowCallbackAsync(verb, callbackQuery, ct);
                break;

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

            case CallbackData.AddTeamGuest:
            case CallbackData.RemoveGuestOnBehalf:
                await HandleManageGuestsCallbackAsync(verb, callbackQuery, ct);
                break;

            case CallbackData.CycleReminderChannel:
            case CallbackData.ToggleReserveReminder:
                await HandleReminderSettingsCallbackAsync(verb, callbackQuery, ct);
                break;

            case CallbackData.ToggleCaptain:
                await HandleManageCaptainsCallbackAsync(callbackQuery, ct);
                break;

            case CallbackData.CloseView:
                await HandleCloseViewAsync(callbackQuery, ct);
                break;

            case CallbackData.Skip:
                await HandleSkipAsync(callbackQuery, ct);
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
        var scope = await ScopeForAsync(team, callbackQuery, ct);

        switch (verb)
        {
            case CallbackData.Join:
                await HandleJoinAsync(game, scope, callbackQuery, ct);
                break;

            case CallbackData.Guest:
                await HandleBringGuestAsync(game, scope, callbackQuery, ct);
                break;

            case CallbackData.Drop:
                await HandleDropPromptAsync(game, scope, callbackQuery, ct);
                break;

            case CallbackData.ConfirmDrop:
                await HandleConfirmDropAsync(game, scope, callbackQuery, ct);
                break;

            case CallbackData.Stay:
                await HandleStayAsync(scope, callbackQuery, ct);
                break;

            case CallbackData.MyGuests:
                await HandleMyGuestsAsync(game, scope, callbackQuery, ct);
                break;

            case CallbackData.Nudge:
                await HandleNudgeButtonAsync(game, scope, callbackQuery, ct);
                break;

            case CallbackData.ManageRoster:
                await HandleManageRosterButtonAsync(game, scope, callbackQuery, ct);
                break;

            case CallbackData.ManagePlayers:
                await HandleManagePlayersButtonAsync(game, scope, callbackQuery, ct);
                break;

            case CallbackData.ManageGuests:
                await HandleManageGuestsButtonAsync(game, scope, callbackQuery, ct);
                break;

            case CallbackData.DeclineGame:
                await HandleDeclinePromptAsync(game, scope, callbackQuery, ct);
                break;

            case CallbackData.ConfirmDecline:
                await HandleConfirmDeclineAsync(game, scope, callbackQuery, ct);
                break;

            case CallbackData.CancelDecline:
                await HandleCancelDeclineAsync(scope, callbackQuery, ct);
                break;

            case CallbackData.FinishGame:
                await HandleFinishButtonAsync(game, scope, callbackQuery, ct);
                break;
        }
    }

    private async Task HandleJoinAsync(
        Game game,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var result = await _signups.JoinAsync(game, scope.Player.Id, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        await _announcements.RefreshAsync(game, scope.Team, ct);
        await _bot.AnswerCallbackQuery(
            callbackQuery.Id,
            scope.Strings.Text("Announcement.Joined"),
            cancellationToken: ct
        );
    }

    private async Task HandleBringGuestAsync(
        Game game,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var result = await _signups.BringGuestAsync(game, scope.Player.Id, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        await _announcements.RefreshAsync(game, scope.Team, ct);
        await _bot.AnswerCallbackQuery(
            callbackQuery.Id,
            scope.Strings.Text("Announcement.GuestAdded"),
            cancellationToken: ct
        );

        await StartGuestNamingDialogAsync(scope, result.Value, ct);
    }

    private async Task StartGuestNamingDialogAsync(CallbackScope scope, Signup guestSignup, CancellationToken ct)
    {
        await _dialogs.StartAsync(
            scope.Team.Id,
            scope.Player.Id,
            scope.ChatId,
            DialogKinds.NameGuest,
            new GuestNameDialogData(guestSignup.Id),
            ct
        );

        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData(
                    scope.Strings.Text("Guest.SkipButton"),
                    CallbackData.Format(CallbackData.SkipGuestName, guestSignup.Id)
                ),
            ],
        ]);
        await _sender.SendAsync(scope.ChatId, scope.Strings.Text("Guest.NamePrompt"), keyboard, ct);
    }

    private async Task HandleDropPromptAsync(
        Game game,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var hasSignup = await _db.Signups.AnyAsync(
            s => s.GameId == game.Id && s.PlayerId == scope.Player.Id && s.CancelledAt == null,
            ct
        );
        if (!hasSignup)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text("Signup.NotSignedUp"), ct);
            return;
        }

        var keyboard = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData(
                    scope.Strings.Text("Drop.ConfirmYes"),
                    CallbackData.Format(CallbackData.ConfirmDrop, game.Id)
                ),
                InlineKeyboardButton.WithCallbackData(
                    scope.Strings.Text("Drop.ConfirmNo"),
                    CallbackData.Format(CallbackData.Stay, game.Id)
                ),
            ],
        ]);

        // Ephemeral: nobody else needs to watch someone weigh up leaving, and the answer to
        // either button lands back on this same private message rather than the group. The
        // roster change itself still shows up publicly, on the announcement.
        await _sender.SendEphemeralAsync(
            scope.ChatId,
            scope.Actor.TelegramUserId,
            scope.Strings.Text("Drop.ConfirmPrompt", new { Title = WebUtility.HtmlEncode(game.Title) }),
            keyboard,
            callbackQuery.Id,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleConfirmDropAsync(
        Game game,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var result = await _signups.DropAsync(game, scope.Player.Id, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        await _announcements.RefreshAsync(game, scope.Team, ct);

        await _sender.TryEditImmediatelyAsync(scope.Message, scope.Strings.Text("Drop.Cancelled"), null, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);

        var outcome = result.Value;
        foreach (var guest in outcome.NamedGuestsNeedingChoice)
        {
            await SendGuestChoicePromptAsync(scope.ChatId, scope.Actor.TelegramUserId, guest, scope.Strings, ct);
        }

        await SendPromotionMessagesAsync(scope.ChatId, outcome.NewlyPromoted, scope.Strings, ct);
    }

    private async Task HandleStayAsync(CallbackScope scope, CallbackQuery callbackQuery, CancellationToken ct)
    {
        await _sender.TryEditImmediatelyAsync(scope.Message, scope.Strings.Text("Drop.StillIn"), null, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleMyGuestsAsync(
        Game game,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var guests = await _signups.LoadLiveGuestsAsync(game, scope.Player.Id, ct);
        var (text, keyboard) = BuildMyGuestsView(game, guests, scope.Strings);

        await _sender.SendEphemeralAsync(
            scope.ChatId,
            scope.Actor.TelegramUserId,
            text,
            keyboard,
            callbackQuery.Id,
            ct
        );
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
        rows.Add(DoneButton.Row(strings));

        return (text, new InlineKeyboardMarkup(rows));
    }

    private async Task HandleRemoveGuestAsync(
        SignupId guestSignupId,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var result = await _signups.RemoveGuestAsync(guestSignupId, scope.Player.Id, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        var outcome = result.Value;
        var game = await _db.Games.SingleAsync(g => g.Id == outcome.Guest.GameId, ct);
        await _announcements.RefreshAsync(game, scope.Team, ct);

        var remaining = await _signups.LoadLiveGuestsAsync(game, scope.Player.Id, ct);
        var (text, keyboard) = BuildMyGuestsView(game, remaining, scope.Strings);
        await _sender.TryEditImmediatelyAsync(scope.Message, text, keyboard, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, scope.Strings.Text("MyGuests.Removed"), cancellationToken: ct);

        await SendPromotionMessagesAsync(scope.ChatId, outcome.NewlyPromoted, scope.Strings, ct);
    }

    private async Task HandleGuestCallbackAsync(char verb, CallbackQuery callbackQuery, CancellationToken ct)
    {
        if (await ResolveScopeAsync(callbackQuery, ct) is not { } scope)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        _ = CallbackData.TryParse(callbackQuery.Data!, out _, out SignupId signupId);

        switch (verb)
        {
            case CallbackData.SkipGuestName:
                await HandleSkipGuestNameAsync(scope, callbackQuery, ct);
                break;

            case CallbackData.KeepGuest:
            case CallbackData.RemoveGuestToo:
                await HandleGuestChoiceAsync(signupId, verb == CallbackData.KeepGuest, scope, callbackQuery, ct);
                break;

            case CallbackData.RemoveGuest:
                await HandleRemoveGuestAsync(signupId, scope, callbackQuery, ct);
                break;
        }
    }

    private async Task HandleSkipGuestNameAsync(CallbackScope scope, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var dialog = await _dialogs.LoadOfKindAsync(scope.ChatId, scope.Player.Id, DialogKinds.NameGuest, ct);
        if (dialog is not null)
        {
            await _dialogs.ClearAsync(dialog, ct);
        }

        await _sender.TryEditImmediatelyAsync(scope.Message, scope.Strings.Text("Guest.SkippedName"), null, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleGuestChoiceAsync(
        SignupId guestSignupId,
        bool keep,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var result = await _signups.ResolveGuestChoiceAsync(guestSignupId, scope.Player.Id, keep, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        var outcome = result.Value;
        var encodedName = WebUtility.HtmlEncode(outcome.Guest.GuestName);
        await _sender.TryEditImmediatelyAsync(
            scope.Message,
            scope.Strings.Text(keep ? "Guest.Kept" : "Guest.Removed", new { Name = encodedName }),
            null,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);

        var game = await _db.Games.SingleAsync(g => g.Id == outcome.Guest.GameId, ct);
        await _announcements.RefreshAsync(game, scope.Team, ct);

        await SendPromotionMessagesAsync(scope.ChatId, outcome.NewlyPromoted, scope.Strings, ct);
    }

    // Goes to the guest's inviter, who is not always the person whose drop caused it: a
    // captain dropping someone on their behalf leaves that someone's guests needing a
    // decision, and sending it to the captain instead would leave the guest hanging with
    // nobody able to resolve them.
    private async Task SendGuestChoicePromptAsync(
        TelegramChatId chatId,
        TelegramUserId inviter,
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
        await _sender.SendEphemeralAsync(
            chatId,
            inviter,
            strings.Text("Guest.KeepQuestion", new { Name = encodedName }),
            keyboard,
            null,
            ct
        );
    }

    // --- Captain flows: franchise pick/edit, game creation branch/date/confirm, game-edit
    // field pick. Which dialog is active decides how a shared verb like EditField is
    // interpreted — see CallbackData.cs.

    private async Task HandleCaptainFlowCallbackAsync(char verb, CallbackQuery callbackQuery, CancellationToken ct)
    {
        if (await ResolveScopeAsync(callbackQuery, ct) is not { } scope)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        // Reaching the dialog at all is the captain-only operation here: most of these verbs
        // only move dialog state along, so there is no other service call for the check to
        // live inside. See IDialogService.
        var loaded = await _dialogs.LoadForCaptainAsync(scope.Team, scope.Actor, ct);
        if (!loaded.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(loaded.Error)), ct);
            return;
        }

        var dialog = loaded.Value;
        _ = CallbackData.TryParse(callbackQuery.Data!, out _, out long value);

        switch (verb)
        {
            case CallbackData.PickFranchise:
                await HandlePickFranchiseAsync(dialog, scope, value, callbackQuery, ct);
                break;

            case CallbackData.ArchiveFranchise:
                await HandleArchiveFranchiseAsync(new FranchiseId(value), scope, callbackQuery, ct);
                break;

            case CallbackData.OneOff:
                await HandleOneOffAsync(dialog, scope, callbackQuery, ct);
                break;

            case CallbackData.PickDate:
                await HandlePickDateAsync(dialog, scope, (int)value, callbackQuery, ct);
                break;

            case CallbackData.CustomDate:
                await HandleCustomDateAsync(dialog, scope, callbackQuery, ct);
                break;

            case CallbackData.EditField:
                await HandleEditFieldAsync(dialog, scope, (int)value, callbackQuery, ct);
                break;

            case CallbackData.Confirm:
                await HandleConfirmNewGameAsync(dialog, scope, callbackQuery, ct);
                break;

            case CallbackData.CancelDialog:
                await HandleCancelDialogAsync(dialog, scope, callbackQuery, ct);
                break;

            case CallbackData.PickGameToEdit:
                await HandlePickGameToEditAsync(new GameId(value), scope, callbackQuery, ct);
                break;
        }
    }

    private async Task HandlePickFranchiseAsync(
        DialogState? dialog,
        CallbackScope scope,
        long rawFranchiseId,
        CallbackQuery callbackQuery,
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
                    scope,
                    callbackQuery,
                    ct
                );
                return;
            }
        }

        // Otherwise: the /editfranchise picker (design decision #1).
        await _dialogs.StartAsync(
            scope.Team.Id,
            scope.Player.Id,
            scope.ChatId,
            DialogKinds.EditFranchise,
            new EditFranchiseDialogData(franchiseId, null),
            ct
        );
        await _sender.SendAsync(
            scope.ChatId,
            FranchiseRenderer.RenderSummary(franchise, scope.Strings),
            FranchiseRenderer.RenderFieldPicker(franchise, scope.Strings),
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleFranchisePickedForNewGameAsync(
        DialogState dialog,
        NewGameDialogData data,
        Franchise franchise,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var strings = scope.Strings;
        var today = DateOnly.FromDateTime(TeamTime.ConvertToLocal(_clock.GetUtcNow(), scope.Team.TimeZoneId!).Date);
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
        await _dialogs.SaveDataAsync(dialog, updated, ct);

        var rows = candidateDates
            .Select(
                (date, index) =>
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData(
                            $"{date:yyyy-MM-dd}",
                            CallbackData.Format(CallbackData.PickDate, index)
                        ),
                    }
            )
            .ToList();
        rows.Add([
            InlineKeyboardButton.WithCallbackData(
                strings.Text("NewGame.CustomDateButton"),
                CallbackData.Format(CallbackData.CustomDate, 0L)
            ),
        ]);
        rows.Add(CancelButton.Row(strings));

        await _sender.SendAsync(scope.ChatId, strings.Text("NewGame.PickDate"), new InlineKeyboardMarkup(rows), ct);
        await RetirePickerAsync(scope, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleCustomDateAsync(
        DialogState? dialog,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        if (dialog is not { Kind: DialogKinds.NewGame })
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var data = JsonSerializer.Deserialize<NewGameDialogData>(dialog.Data)!;
        if (data.Step != NewGameDialogData.PickDate)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        await _dialogs.SaveDataAsync(dialog, data with { Step = NewGameDialogData.FranchiseCustomDate }, ct);
        await SendPromptAsync(
            dialog,
            scope.ChatId,
            scope.Strings.Text("NewGame.AskDate"),
            CancelButton.Keyboard(scope.Strings),
            ct
        );
        await RetirePickerAsync(scope, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleArchiveFranchiseAsync(
        FranchiseId franchiseId,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var franchise = await _db.Franchises.SingleOrDefaultAsync(f => f.Id == franchiseId, ct);
        if (franchise is null)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var result = await _franchises.ArchiveAsync(franchise, scope.Team, scope.Actor, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        await _sender.TryEditImmediatelyAsync(
            scope.Message,
            scope.Strings.Text("Franchise.ArchivedConfirm", new { Name = WebUtility.HtmlEncode(franchise.Name) }),
            null,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleOneOffAsync(
        DialogState? dialog,
        CallbackScope scope,
        CallbackQuery callbackQuery,
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

        await _dialogs.SaveDataAsync(dialog, data with { Step = NewGameDialogData.OneOffTitle }, ct);
        await SendPromptAsync(
            dialog,
            scope.ChatId,
            scope.Strings.Text("NewGame.AskTitle"),
            CancelButton.Keyboard(scope.Strings),
            ct
        );
        await RetirePickerAsync(scope, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandlePickDateAsync(
        DialogState? dialog,
        CallbackScope scope,
        int index,
        CallbackQuery callbackQuery,
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
        await _dialogs.SaveDataAsync(dialog, updated, ct);
        await SendConfirmScreenAsync(dialog, scope.ChatId, updated, scope.Strings, ct);
        await RetirePickerAsync(scope, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleEditFieldAsync(
        DialogState? dialog,
        CallbackScope scope,
        int fieldIndex,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var chatId = scope.ChatId;
        var strings = scope.Strings;

        // Reached by tapping an edit button on either the field-picker (its own keyboard is a
        // persistent parent menu, not superseded here) or the NewGame confirm screen (which
        // does get superseded by the Ask-field prompt below, and needs its own Cancel button
        // stripped once that happens).
        var previousMessageId = dialog?.MessageId;

        switch (dialog?.Kind)
        {
            case DialogKinds.EditFranchise:
            {
                var data = JsonSerializer.Deserialize<EditFranchiseDialogData>(dialog.Data)!;
                await _dialogs.SaveDataAsync(dialog, data with { FieldIndex = fieldIndex }, ct);
                await SendPromptAsync(
                    dialog,
                    chatId,
                    strings.Text(FranchiseFieldPromptKey(fieldIndex)),
                    IsFranchiseFieldSkippable(fieldIndex)
                        ? SkipButton.KeyboardWithCancel(strings)
                        : CancelButton.Keyboard(strings),
                    ct
                );
                break;
            }

            case DialogKinds.NewGame:
            {
                var data = JsonSerializer.Deserialize<NewGameDialogData>(dialog.Data)!;
                var updated = data with { Step = NewGameDialogData.EditingField, EditingFieldIndex = fieldIndex };
                await _dialogs.SaveDataAsync(dialog, updated, ct);
                await SendPromptAsync(
                    dialog,
                    chatId,
                    strings.Text(NewGameFieldPromptKey(fieldIndex)),
                    IsNewGameOverrideSkippable(fieldIndex)
                        ? SkipButton.KeyboardWithCancel(strings)
                        : CancelButton.Keyboard(strings),
                    ct
                );
                break;
            }

            case DialogKinds.EditGame:
            {
                var data = JsonSerializer.Deserialize<EditGameDialogData>(dialog.Data)!;
                await _dialogs.SaveDataAsync(dialog, data with { FieldIndex = fieldIndex }, ct);
                await SendPromptAsync(
                    dialog,
                    chatId,
                    strings.Text(EditGameFieldPromptKey(fieldIndex)),
                    IsEditGameFieldSkippable(fieldIndex)
                        ? SkipButton.KeyboardWithCancel(strings)
                        : CancelButton.Keyboard(strings),
                    ct
                );
                break;
            }
        }

        if (dialog is not null && previousMessageId is { } staleMessageId && dialog.MessageId != previousMessageId)
        {
            await _sender.RemoveKeyboardAsync(chatId, staleMessageId, ct);
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

    // Name isn't skippable — every franchise needs one. Venue, capacity, price, and schedule
    // all are.
    private static bool IsFranchiseFieldSkippable(int fieldIndex) =>
        fieldIndex
            is EditFranchiseDialogData.Venue
                or EditFranchiseDialogData.Capacity
                or EditFranchiseDialogData.Price
                or EditFranchiseDialogData.Schedule;

    // Venue and capacity aren't skippable here — Confirm requires both set (invariant: every
    // Game needs a concrete capacity to derive playing/reserve from), so an override on either
    // must replace it with something, not clear it.
    private static bool IsNewGameOverrideSkippable(int fieldIndex) =>
        fieldIndex
            is NewGameDialogData.OverridePrice
                or NewGameDialogData.OverrideNotes
                or NewGameDialogData.OverrideTags;

    // Title, venue, capacity, and start time all keep a live game valid — none are skippable.
    // Price, notes, and tags are.
    private static bool IsEditGameFieldSkippable(int fieldIndex) =>
        fieldIndex is EditGameDialogData.Price or EditGameDialogData.Notes or EditGameDialogData.Tags;

    private async Task HandleConfirmNewGameAsync(
        DialogState? dialog,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var team = scope.Team;
        var strings = scope.Strings;

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

        if (data.Venue is null || data.Capacity is null)
        {
            // Only reachable when a franchise left venue or capacity unset — the one-off flow
            // always collects both before Confirm is even shown.
            await AnswerAlertAsync(callbackQuery, strings.Text("NewGame.MissingRequiredFields"), ct);
            return;
        }

        Result<Game> created;
        if (data.FranchiseId is { } franchiseId)
        {
            var franchise = await _db.Franchises.SingleAsync(f => f.Id == franchiseId, ct);
            created = await _games.CreateFromFranchiseAsync(
                team,
                scope.Actor,
                franchise,
                data.Title!,
                data.Date!.Value,
                data.Time!.Value,
                data.Venue!,
                data.Capacity!.Value,
                data.Price,
                data.Notes,
                data.Tags ?? [],
                ct
            );
        }
        else
        {
            created = await _games.CreateOneOffAsync(
                team,
                scope.Actor,
                data.Title!,
                data.Venue!,
                data.Date!.Value,
                data.Time!.Value,
                data.Capacity!.Value,
                data.Price,
                ct
            );
        }

        if (!created.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(created.Error)), ct);
            return;
        }

        var game = created.Value;
        if (data.FranchiseId is null)
        {
            if (!string.IsNullOrWhiteSpace(data.Notes))
            {
                _ = await _games.SetNotesAsync(game, team, scope.Actor, data.Notes, ct);
            }

            if (data.Tags is { Count: > 0 })
            {
                _ = await _games.SetTagsAsync(game, team, scope.Actor, data.Tags, ct);
            }
        }

        await _dialogs.ClearAsync(dialog, ct);

        var messageId = await _announcements.PostAsync(game, team, ct);
        game.AnnouncementMessageId = messageId;
        await _db.SaveChangesAsync(ct);
        await _board.RefreshAsync(team, ct);

        await _sender.TryEditImmediatelyAsync(scope.Message, strings.Text("NewGame.Created"), null, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleCancelDialogAsync(
        DialogState? dialog,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var key = dialog?.Kind == DialogKinds.Nudge ? "Nudge.Cancelled" : "NewGame.Cancelled";

        if (dialog is not null)
        {
            await _dialogs.ClearAsync(dialog, ct);
        }

        await _sender.TryEditImmediatelyAsync(scope.Message, scope.Strings.Text(key), null, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    // Text-command escape hatch for the plain-text wizard steps (/newfranchise's Name->
    // Schedule walk, /newgame's one-off Title->Price walk) — those have no button to attach
    // a Cancel to the way the confirm screen and Nudge picker do, and an abandoned dialog
    // would otherwise silently swallow whatever the captain sends next.
    private async Task HandleCancelCommandAsync(Team team, Player player, TelegramChatId chatId, CancellationToken ct)
    {
        var strings = _strings.For(team.Locale);
        var dialog = await _dialogs.LoadAsync(chatId, player.Id, ct);
        if (dialog is null)
        {
            await _sender.SendAsync(chatId, strings.Text("Cancel.NothingToCancel"), null, ct);
            return;
        }

        var key = dialog.Kind == DialogKinds.Nudge ? "Nudge.Cancelled" : "NewGame.Cancelled";
        await _dialogs.ClearAsync(dialog, ct);

        // The dialog's own prompt (if any) had a Cancel/Skip keyboard of its own — this
        // command is the escape hatch for exactly the steps that show one (see this method's
        // own comment above), so it's stale the moment /cancel is processed.
        if (dialog.MessageId is { } staleMessageId)
        {
            await _sender.RemoveKeyboardAsync(chatId, staleMessageId, ct);
        }

        await _sender.SendAsync(chatId, strings.Text(key), null, ct);
    }

    private async Task HandlePickGameToEditAsync(
        GameId gameId,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var game = await _db.Games.SingleOrDefaultAsync(g => g.Id == gameId, ct);
        if (game is null)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        await _dialogs.StartAsync(
            scope.Team.Id,
            scope.Player.Id,
            scope.ChatId,
            DialogKinds.EditGame,
            new EditGameDialogData(gameId, null),
            ct
        );
        await _sender.SendAsync(
            scope.ChatId,
            scope.Strings.Text("EditGame.PickField", new { Title = WebUtility.HtmlEncode(game.Title) }),
            RenderEditGameFieldPicker(scope.Strings),
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
            DoneButton.Row(strings),
        ]);

    // --- Manage roster (design decision #4): a Played toggle plus Add player, on a
    // finished game's captain-only button. ---

    private async Task HandleManageRosterButtonAsync(
        Game game,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var roster = await _participations.LoadRosterAsync(game, scope.Team, scope.Actor, ct);
        if (!roster.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(roster.Error)), ct);
            return;
        }

        await SendRosterViewAsync(game, roster.Value, scope.ChatId, scope.Actor.TelegramUserId, scope.Strings, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task SendRosterViewAsync(
        Game game,
        IReadOnlyList<Participation> participations,
        TelegramChatId chatId,
        TelegramUserId receiver,
        IStringsFor strings,
        CancellationToken ct
    ) =>
        await _sender.SendEphemeralAsync(
            chatId,
            receiver,
            RosterManagementRenderer.RenderText(game, participations, strings),
            RosterManagementRenderer.RenderKeyboard(game, participations, strings),
            null,
            ct
        );

    private async Task HandleRosterCallbackAsync(char verb, CallbackQuery callbackQuery, CancellationToken ct)
    {
        if (await ResolveScopeAsync(callbackQuery, ct) is not { } scope)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        _ = CallbackData.TryParse(callbackQuery.Data!, out _, out long value);

        switch (verb)
        {
            case CallbackData.TogglePlayed:
                await HandleToggleParticipationAsync(new ParticipationId(value), scope, callbackQuery, ct);
                break;

            case CallbackData.AddPlayer:
                await HandleAddPlayerButtonAsync(new GameId(value), scope, callbackQuery, ct);
                break;
        }
    }

    private async Task HandleToggleParticipationAsync(
        ParticipationId participationId,
        CallbackScope scope,
        CallbackQuery callbackQuery,
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

        var toggled = await _participations.TogglePlayedAsync(participation, scope.Team, scope.Actor, ct);
        if (!toggled.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(toggled.Error)), ct);
            return;
        }

        var game = participation.Game;
        var roster = await _participations.LoadRosterAsync(game, scope.Team, scope.Actor, ct);
        if (!roster.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(roster.Error)), ct);
            return;
        }

        await _sender.TryEditImmediatelyAsync(
            scope.Message,
            RosterManagementRenderer.RenderText(game, roster.Value, scope.Strings),
            RosterManagementRenderer.RenderKeyboard(game, roster.Value, scope.Strings),
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleAddPlayerButtonAsync(
        GameId gameId,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var started = await _dialogs.StartForCaptainAsync(
            scope.Team,
            scope.Actor,
            DialogKinds.AddVenuePlayer,
            new AddVenuePlayerDialogData(gameId),
            ct
        );
        if (!started.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(started.Error)), ct);
            return;
        }

        await _sender.SendEphemeralAsync(
            scope.ChatId,
            scope.Actor.TelegramUserId,
            scope.Strings.Text("Roster.AskPlayerName"),
            null,
            callbackQuery.Id,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    // --- Nudge (design decision #5): a targeted multi-select, not a blind broadcast. ---

    private async Task HandleNudgeButtonAsync(
        Game game,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var loaded = await _games.LoadPlayingMembersAsync(game, scope.Team, scope.Actor, ct);
        if (!loaded.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(loaded.Error)), ct);
            return;
        }

        // The captain doing the nudging is presumably at the venue themselves — that's why
        // they're the one noticing who's late — so they're never a nudge target even if
        // they're also signed up to play.
        var playing = loaded.Value.Where(m => m.PlayerId != scope.Player.Id).ToList();
        if (playing.Count == 0)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text("Nudge.NobodyToNudge"), ct);
            return;
        }

        // Starts fully selected — the bot has no notion of who has actually arrived (no
        // check-in feature), so the captain's job is to uncheck whoever they can already see
        // is there, leaving the late arrivals checked before sending.
        var selected = playing.Select(m => m.PlayerId.Value).ToList();
        await _dialogs.StartAsync(
            scope.Team.Id,
            scope.Player.Id,
            scope.ChatId,
            DialogKinds.Nudge,
            new NudgeDialogData(game.Id, selected),
            ct
        );

        var (text, keyboard) = BuildNudgeView(game, playing, selected, scope.Strings);
        await _sender.SendEphemeralAsync(
            scope.ChatId,
            scope.Actor.TelegramUserId,
            text,
            keyboard,
            callbackQuery.Id,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private static (string Text, InlineKeyboardMarkup Keyboard) BuildNudgeView(
        Game game,
        IReadOnlyList<Membership> playing,
        List<long> selectedPlayerIds,
        IStringsFor strings
    )
    {
        var text = strings.Text("Nudge.Header", new { Title = WebUtility.HtmlEncode(game.Title) });

        var rows = new List<IEnumerable<InlineKeyboardButton>>();
        foreach (var membership in playing)
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
            .. CancelButton.Row(strings),
        ]);

        return (text, new InlineKeyboardMarkup(rows));
    }

    private async Task HandleNudgeCallbackAsync(char verb, CallbackQuery callbackQuery, CancellationToken ct)
    {
        if (await ResolveScopeAsync(callbackQuery, ct) is not { } scope)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var loaded = await _dialogs.LoadForCaptainAsync(scope.Team, scope.Actor, DialogKinds.Nudge, ct);
        if (!loaded.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(loaded.Error)), ct);
            return;
        }

        if (loaded.Value is not { } dialog)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var data = JsonSerializer.Deserialize<NudgeDialogData>(dialog.Data)!;
        var game = await _db.Games.SingleAsync(g => g.Id == data.GameId, ct);
        _ = CallbackData.TryParse(callbackQuery.Data!, out _, out long value);

        if (verb == CallbackData.ToggleNudgeTarget)
        {
            await HandleToggleNudgeTargetAsync(dialog, data, game, scope, value, callbackQuery, ct);
            return;
        }

        await HandleSendNudgeAsync(dialog, data, game, scope, callbackQuery, ct);
    }

    private async Task HandleToggleNudgeTargetAsync(
        DialogState dialog,
        NudgeDialogData data,
        Game game,
        CallbackScope scope,
        long targetPlayerId,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var selected = new List<long>(data.SelectedPlayerIds);
        if (!selected.Remove(targetPlayerId))
        {
            selected.Add(targetPlayerId);
        }

        await _dialogs.SaveDataAsync(dialog, data with { SelectedPlayerIds = selected }, ct);

        var loaded = await _games.LoadPlayingMembersAsync(game, scope.Team, scope.Actor, ct);
        if (!loaded.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(loaded.Error)), ct);
            return;
        }

        var playing = loaded.Value.Where(m => m.PlayerId != scope.Player.Id).ToList();
        var (text, keyboard) = BuildNudgeView(game, playing, selected, scope.Strings);
        await _sender.TryEditImmediatelyAsync(scope.Message, text, keyboard, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleSendNudgeAsync(
        DialogState dialog,
        NudgeDialogData data,
        Game game,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var strings = scope.Strings;

        if (data.SelectedPlayerIds.Count == 0)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text("Nudge.NoneSelected"), ct);
            return;
        }

        var result = await _games.TryNudgeAsync(game, scope.Team, scope.Actor, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        var selectedIds = data.SelectedPlayerIds.Select(id => new PlayerId(id)).ToList();
        var players = await _db.Players.AsNoTracking().Where(p => selectedIds.Contains(p.Id)).ToListAsync(ct);
        var mentions = string.Join(", ", players.Select(Mention.Of));

        await _dialogs.ClearAsync(dialog, ct);

        await _sender.SendAsync(
            scope.ChatId,
            strings.Text("Nudge.Sent", new { Mentions = mentions, Title = WebUtility.HtmlEncode(game.Title) }),
            null,
            ct
        );
        await _sender.TryEditImmediatelyAsync(scope.Message, strings.Text("Nudge.PickerClosed"), null, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

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
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var strings = scope.Strings;

        var allowed = await _games.EnsureCanManageAsync(scope.Team, scope.Actor, ct);
        if (!allowed.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(allowed.Error)), ct);
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
        await _sender.SendEphemeralAsync(
            scope.ChatId,
            scope.Actor.TelegramUserId,
            strings.Text("Decline.ConfirmPrompt", new { Title = WebUtility.HtmlEncode(game.Title) }),
            keyboard,
            callbackQuery.Id,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleConfirmDeclineAsync(
        Game game,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var result = await _games.DeclineAsync(game, scope.Team, scope.Actor, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        await _announcements.RefreshAsync(game, scope.Team, ct);
        await _board.RefreshAsync(scope.Team, ct);

        await _sender.TryEditImmediatelyAsync(scope.Message, scope.Strings.Text("Decline.Declined"), null, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleCancelDeclineAsync(CallbackScope scope, CallbackQuery callbackQuery, CancellationToken ct)
    {
        await _sender.TryEditImmediatelyAsync(scope.Message, scope.Strings.Text("Decline.Cancelled"), null, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleFinishButtonAsync(
        Game game,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var result = await _games.FinishAsync(game, scope.Team, scope.Actor, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        await _announcements.RefreshAsync(game, scope.Team, ct);
        await _board.RefreshAsync(scope.Team, ct);

        await _bot.AnswerCallbackQuery(callbackQuery.Id, scope.Strings.Text("Finish.Finished"), cancellationToken: ct);
    }

    // --- Act on behalf of a player ("Manage players", design decision #2 of M9) ---

    private async Task HandleManagePlayersButtonAsync(
        Game game,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var started = await _dialogs.StartForCaptainAsync(
            scope.Team,
            scope.Actor,
            DialogKinds.ManagePlayers,
            new ManagePlayersDialogData(game.Id),
            ct
        );
        if (!started.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(started.Error)), ct);
            return;
        }

        var statuses = await _games.LoadMemberStatusesAsync(game, scope.Team, scope.Actor, ct);
        if (!statuses.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(statuses.Error)), ct);
            return;
        }

        await _sender.SendEphemeralAsync(
            scope.ChatId,
            scope.Actor.TelegramUserId,
            ManagePlayersRenderer.RenderText(game, statuses.Value, scope.Strings),
            ManagePlayersRenderer.RenderKeyboard(statuses.Value, scope.Strings),
            callbackQuery.Id,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleManagePlayersCallbackAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        if (await ResolveScopeAsync(callbackQuery, ct) is not { } scope)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var loaded = await _dialogs.LoadForCaptainAsync(scope.Team, scope.Actor, DialogKinds.ManagePlayers, ct);
        if (!loaded.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(loaded.Error)), ct);
            return;
        }

        if (loaded.Value is not { } dialog)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var strings = scope.Strings;
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
                scope.Team.Id,
                game.Id,
                scope.Player.Id,
                AuditActions.PlayerDroppedOnBehalf,
                new { TargetPlayerId = targetPlayerId.Value },
                _clock
            );
            await _db.SaveChangesAsync(ct);
            await _announcements.RefreshAsync(game, scope.Team, ct);

            var outcome = result.Value;
            if (outcome.NamedGuestsNeedingChoice.Count > 0)
            {
                // The dropped player, not the captain who dropped them — see the prompt's own
                // comment. Loaded only when there is actually a decision to hand over.
                var target = await _db.Players.AsNoTracking().SingleAsync(p => p.Id == targetPlayerId, ct);
                foreach (var guest in outcome.NamedGuestsNeedingChoice)
                {
                    await SendGuestChoicePromptAsync(scope.ChatId, target.TelegramUserId, guest, strings, ct);
                }
            }

            await SendPromotionMessagesAsync(scope.ChatId, outcome.NewlyPromoted, strings, ct);
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
                scope.Team.Id,
                game.Id,
                scope.Player.Id,
                AuditActions.PlayerRegisteredOnBehalf,
                new { TargetPlayerId = targetPlayerId.Value },
                _clock
            );
            await _db.SaveChangesAsync(ct);
            await _announcements.RefreshAsync(game, scope.Team, ct);
        }

        var statuses = await _games.LoadMemberStatusesAsync(game, scope.Team, scope.Actor, ct);
        if (!statuses.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(statuses.Error)), ct);
            return;
        }

        await _sender.TryEditImmediatelyAsync(
            scope.Message,
            ManagePlayersRenderer.RenderText(game, statuses.Value, strings),
            ManagePlayersRenderer.RenderKeyboard(statuses.Value, strings),
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    // --- Manage guests (captain-only): every live guest for the game, including ones a
    // captain isn't signed up for themselves — the one guest self-service RemoveGuest can
    // never reach, since a team guest has no owner to check against. No dialog for the list
    // itself: RemoveGuestOnBehalf carries the guest's own SignupId and AddTeamGuest carries
    // the GameId, so nothing needs remembering between taps — only naming a new team guest
    // does, since that's the one step needing a text reply. ---

    private async Task HandleManageGuestsButtonAsync(
        Game game,
        CallbackScope scope,
        CallbackQuery callbackQuery,
        CancellationToken ct
    )
    {
        var guests = await _signups.LoadAllLiveGuestsAsync(game, scope.Team, scope.Actor, ct);
        if (!guests.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(guests.Error)), ct);
            return;
        }

        var (text, keyboard) = BuildManageGuestsView(game, guests.Value, scope.Strings);
        await _sender.SendEphemeralAsync(
            scope.ChatId,
            scope.Actor.TelegramUserId,
            text,
            keyboard,
            callbackQuery.Id,
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    private static (string Text, InlineKeyboardMarkup Keyboard) BuildManageGuestsView(
        Game game,
        IReadOnlyList<Signup> guests,
        IStringsFor strings
    )
    {
        var encodedTitle = WebUtility.HtmlEncode(game.Title);
        var text =
            guests.Count == 0
                ? strings.Text("ManageGuests.Empty", new { Title = encodedTitle })
                : strings.Text("ManageGuests.Header", new { Title = encodedTitle });

        var rows = new List<IEnumerable<InlineKeyboardButton>>();
        for (var i = 0; i < guests.Count; i++)
        {
            var guest = guests[i];
            string label;
            if (guest.GuestName is { } name)
            {
                label = guest.HasInviter
                    ? strings.Text(
                        "ManageGuests.RemoveNamedButton",
                        new { Name = name, Inviter = guest.InvitedByPlayer!.DisplayName }
                    )
                    : strings.Text("ManageGuests.RemoveTeamGuestButton", new { Name = name });
            }
            else
            {
                label = strings.Text(
                    "ManageGuests.RemoveAnonymousButton",
                    new { Index = i + 1, Inviter = guest.InvitedByPlayer!.DisplayName }
                );
            }

            rows.Add([
                InlineKeyboardButton.WithCallbackData(
                    label,
                    CallbackData.Format(CallbackData.RemoveGuestOnBehalf, guest.Id)
                ),
            ]);
        }

        rows.Add([
            InlineKeyboardButton.WithCallbackData(
                strings.Text("ManageGuests.AddTeamGuestButton"),
                CallbackData.Format(CallbackData.AddTeamGuest, game.Id)
            ),
        ]);
        rows.Add(DoneButton.Row(strings));

        return (text, new InlineKeyboardMarkup(rows));
    }

    private async Task HandleManageGuestsCallbackAsync(char verb, CallbackQuery callbackQuery, CancellationToken ct)
    {
        if (await ResolveScopeAsync(callbackQuery, ct) is not { } scope)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var strings = scope.Strings;

        if (verb == CallbackData.AddTeamGuest)
        {
            _ = CallbackData.TryParse(callbackQuery.Data!, out _, out GameId gameId);
            var started = await _dialogs.StartForCaptainAsync(
                scope.Team,
                scope.Actor,
                DialogKinds.AddTeamGuest,
                new AddTeamGuestDialogData(gameId),
                ct
            );
            if (!started.IsSuccess)
            {
                await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(started.Error)), ct);
                return;
            }

            await _sender.SendEphemeralAsync(
                scope.ChatId,
                scope.Actor.TelegramUserId,
                strings.Text("ManageGuests.AskName"),
                null,
                callbackQuery.Id,
                ct
            );
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        _ = CallbackData.TryParse(callbackQuery.Data!, out _, out SignupId guestSignupId);
        var result = await _signups.RemoveGuestOnBehalfAsync(guestSignupId, scope.Team, scope.Actor, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        var outcome = result.Value;
        var game = await _db.Games.SingleAsync(g => g.Id == outcome.Guest.GameId, ct);

        AuditRecorder.Record(
            _db,
            scope.Team.Id,
            game.Id,
            scope.Player.Id,
            AuditActions.GuestRemovedOnBehalf,
            new { GuestSignupId = guestSignupId.Value },
            _clock
        );
        await _db.SaveChangesAsync(ct);
        await _announcements.RefreshAsync(game, scope.Team, ct);

        var remaining = await _signups.LoadAllLiveGuestsAsync(game, scope.Team, scope.Actor, ct);
        if (!remaining.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, strings.Text(ErrorKey(remaining.Error)), ct);
            return;
        }

        var (text, keyboard) = BuildManageGuestsView(game, remaining.Value, strings);
        await _sender.TryEditImmediatelyAsync(scope.Message, text, keyboard, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);

        await SendPromotionMessagesAsync(scope.ChatId, outcome.NewlyPromoted, strings, ct);
    }

    // --- Reminder settings (/myreminders) — self-service, no captain check, no dialog. ---

    private async Task HandleMyRemindersCommandAsync(
        Team team,
        Actor actor,
        TelegramChatId chatId,
        CancellationToken ct
    )
    {
        var membership = await _teams.LoadOwnMembershipAsync(team, actor.PlayerId, ct);
        var strings = _strings.For(team.Locale);
        var (text, keyboard) = BuildReminderSettingsView(membership, strings);

        // Somebody's own reminder preferences concern nobody else in the chat. The command
        // they typed is still theirs and still public — ephemeral only covers the bot's half.
        await _sender.SendEphemeralAsync(chatId, actor.TelegramUserId, text, keyboard, null, ct);
    }

    private async Task HandleReminderSettingsCallbackAsync(char verb, CallbackQuery callbackQuery, CancellationToken ct)
    {
        if (await ResolveScopeAsync(callbackQuery, ct) is not { } scope)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var membership = await _teams.LoadOwnMembershipAsync(scope.Team, scope.Player.Id, ct);
        var strings = scope.Strings;

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
        await _sender.TryEditImmediatelyAsync(scope.Message, text, keyboard, ct);
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
            DoneButton.Row(strings),
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
        Actor actor,
        TelegramChatId chatId,
        CancellationToken ct
    )
    {
        var strings = _strings.For(team.Locale);

        var result = await _teams.LoadMembersAsync(team, actor, ct);
        if (!result.IsSuccess)
        {
            await _sender.SendAsync(chatId, strings.Text(ErrorKey(result.Error)), null, ct);
            return;
        }

        var (text, keyboard) = BuildManageCaptainsView(result.Value, strings);
        await _sender.SendAsync(chatId, text, keyboard, ct);
    }

    private async Task HandleManageCaptainsCallbackAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        if (await ResolveScopeAsync(callbackQuery, ct) is not { } scope)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        _ = CallbackData.TryParse(callbackQuery.Data!, out _, out PlayerId targetPlayerId);

        var result = await _teams.ToggleCaptainAsync(scope.Team, scope.Actor, targetPlayerId, ct);
        if (!result.IsSuccess)
        {
            await AnswerAlertAsync(callbackQuery, scope.Strings.Text(ErrorKey(result.Error)), ct);
            return;
        }

        var (text, keyboard) = BuildManageCaptainsView(result.Value, scope.Strings);
        await _sender.TryEditImmediatelyAsync(scope.Message, text, keyboard, ct);
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

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
        rows.Add(DoneButton.Row(strings));

        return (strings.Text("Captains.Header"), new InlineKeyboardMarkup(rows));
    }

    // Built from the same two lists CommandMenu.RegisterAsync uses for Telegram's own "/"
    // suggestion menu, so there's exactly one place a new command gets named and described —
    // nowhere for this view and that one to drift apart. Argument syntax isn't repeated here;
    // each command that takes one already explains it in its own validation message (e.g.
    // Setup.RemindersUsage) when run without it.
    private static string BuildHelpText(IStringsFor strings)
    {
        var text = new StringBuilder();
        text.Append(strings.Text("Help.Intro")).Append("\n\n");

        text.Append("<b>").Append(strings.Text("Help.EveryoneHeader")).Append("</b>\n");
        AppendCommandList(text, CommandMenu.EveryoneCommands, strings);

        text.Append("\n<b>").Append(strings.Text("Help.CaptainsHeader")).Append("</b>\n");
        AppendCommandList(text, CommandMenu.CaptainOnlyCommands, strings);

        return text.ToString().TrimEnd();
    }

    private static void AppendCommandList(
        StringBuilder text,
        IEnumerable<(string Command, string DescriptionKey)> commands,
        IStringsFor strings
    )
    {
        foreach (var (command, descriptionKey) in commands)
        {
            text.Append("<code>/")
                .Append(command)
                .Append("</code> — ")
                .Append(strings.Text(descriptionKey))
                .Append('\n');
        }
    }

    private async Task AnswerAlertAsync(CallbackQuery callbackQuery, string text, CancellationToken ct) =>
        await _bot.AnswerCallbackQuery(callbackQuery.Id, text, showAlert: true, cancellationToken: ct);

    // Shared "Done" for every open-ended view (Manage guests, Manage players): strips the
    // keyboard via editMessageReplyMarkup rather than editMessageText, so whatever the view
    // last showed — who your guests are, who's on the roster — stays visible as a record
    // instead of being replaced by a generic confirmation. Clears any dialog behind the view
    // too, since a captain-only one (Manage players) would otherwise sit there forever with
    // no text-reply step to ever end it.
    private async Task HandleCloseViewAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = await ResolveChatIdAsync(new TelegramChatId(callbackQuery.Message!.Chat.Id), ct);
        var player = await _playerBootstrap.GetOrCreateAsync(callbackQuery.From, ct);

        if (await _dialogs.LoadAsync(chatId, player.Id, ct) is { } dialog)
        {
            await _dialogs.ClearAsync(dialog, ct);
        }

        await _bot.SendRequest(
            new EditMessageReplyMarkupRequest
            {
                ChatId = chatId.Value,
                MessageId = callbackQuery.Message.MessageId,
                ReplyMarkup = null,
            },
            ct
        );
        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    // A tap-to-skip alternative to typing "skip" on any optional text prompt — Telegram
    // clients won't let a captain send a genuinely empty message, so typing the word is the
    // only reliable fallback, but a button is faster and needs no locale-specific keyword.
    // Reuses whichever step-reply handler a real "skip" reply would hit, by building the
    // exact same synthetic input those handlers already read (message.Chat.Id, message.Text)
    // — no separate skip logic to keep in sync with each field's own parser.
    private async Task HandleSkipAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = await ResolveChatIdAsync(new TelegramChatId(callbackQuery.Message!.Chat.Id), ct);
        var player = await _playerBootstrap.GetOrCreateAsync(callbackQuery.From, ct);
        if (await _dialogs.LoadAsync(chatId, player.Id, ct) is not { } dialog)
        {
            await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var syntheticReply = new Message
        {
            Id = callbackQuery.Message.MessageId,
            Chat = callbackQuery.Message.Chat,
            From = callbackQuery.From,
            Date = DateTime.UtcNow,
            Text = "skip",
        };

        // Routed through the same dispatch every real text reply goes through — Skip is
        // shown only on the four kinds that switch handles, so this reaches the exact same
        // handler a real "skip" reply would, and inherits its stale-keyboard cleanup too.
        await HandleDialogReplyAsync(dialog, syntheticReply, ct);

        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    // The edit wizards funnel both a parse failure and a rejected write through one errorKey,
    // so a Result reads back as "no key" on success.
    private static string? FailureKey<T>(Result<T> result) => result.IsSuccess ? null : ErrorKey(result.Error);

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
            BusinessError.FranchiseNameTaken => "Franchise.NameTaken",
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
