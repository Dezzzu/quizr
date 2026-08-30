using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;
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

            case "/newgame" when team is not null && player is not null && message.From is not null:
                await HandleNewGameAsync(team, player.Id, chatId, new TelegramUserId(message.From.Id), ct);
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

    private async Task HandleNewGameAsync(
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
        var text = guard.Match(_ => strings.Text("NewGame.NotBuiltYet"), _ => strings.Text("NewGame.NeedsTimeZone"));

        await _sender.SendAsync(chatId, text, null, ct);
    }

    // --- Dialog replies (currently: naming a guest) ---

    private async Task HandleDialogReplyAsync(DialogState dialog, Message message, CancellationToken ct)
    {
        switch (dialog.Kind)
        {
            case DialogKinds.NameGuest:
                await HandleGuestNameReplyAsync(dialog, message, ct);
                break;

            default:
                _logger.LogWarning("Discarding a dialog with unrecognised kind {Kind}", dialog.Kind);
                _db.DialogStates.Remove(dialog);
                await _db.SaveChangesAsync(ct);
                break;
        }
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
                await HandleGameCallbackAsync(verb, callbackQuery, ct);
                break;

            case CallbackData.SkipGuestName:
            case CallbackData.KeepGuest:
            case CallbackData.RemoveGuestToo:
            case CallbackData.RemoveGuest:
                await HandleGuestCallbackAsync(verb, callbackQuery, ct);
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

        foreach (var promoted in outcome.NewlyPromoted)
        {
            await SendPromotionMessageAsync(chatId, promoted, strings, ct);
        }
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

        foreach (var promoted in outcome.NewlyPromoted)
        {
            await SendPromotionMessageAsync(chatId, promoted, strings, ct);
        }
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

        foreach (var promoted in outcome.NewlyPromoted)
        {
            await SendPromotionMessageAsync(chatId, promoted, strings, ct);
        }
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

    private async Task SendPromotionMessageAsync(
        TelegramChatId chatId,
        Signup signup,
        IStringsFor strings,
        CancellationToken ct
    )
    {
        string who;
        if (signup.PlayerId is { } playerId)
        {
            var promoted = await _db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, ct);
            who = WebUtility.HtmlEncode(promoted.DisplayName);
        }
        else
        {
            who = WebUtility.HtmlEncode(signup.GuestName ?? "Guest");
        }

        await _sender.SendAsync(chatId, strings.Text("Promotion.Message", new { Who = who }), null, ct);
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
