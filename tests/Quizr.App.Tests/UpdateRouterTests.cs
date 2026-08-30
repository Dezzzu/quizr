using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.App.Telegram;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Quizr.App.Tests;

[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class UpdateRouterTests
{
    private readonly PostgresFixture _fixture;

    public UpdateRouterTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task SetTimeZoneRejectsAnUnrecognisedId()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4001, telegramUserId: 4001, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4001, 4001, "/settimezone Nowhere/Fake"), ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).TimeZoneId.Should().BeNull();
        bot.SentTexts().Should().ContainSingle(text => text.Contains("Nowhere/Fake", StringComparison.Ordinal));
    }

    [Test]
    public async Task SetTimeZoneAcceptsAValidIanaId()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4002, telegramUserId: 4002, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4002, 4002, "/settimezone Europe/Berlin"), ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).TimeZoneId.Should().Be("Europe/Berlin");
        bot.SentTexts().Should().ContainSingle(text => text.Contains("Europe/Berlin", StringComparison.Ordinal));
    }

    [Test]
    public async Task NewGameIsRefusedBeforeATimeZoneIsSet()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        await SeedCaptainedTeamAsync(db, chatId: 4003, telegramUserId: 4003, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4003, 4003, "/newgame"), ct);

        bot.SentTexts().Should().ContainSingle(text => text.Contains("timezone", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task NewGameOffersTheBranchChoiceOnceATimeZoneIsSet()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4004, telegramUserId: 4004, ct);
        team.TimeZoneId = "Europe/Berlin";
        await db.SaveChangesAsync(ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4004, 4004, "/newgame"), ct);

        bot.SentTexts().Should().ContainSingle(text => text.Contains("franchise", StringComparison.OrdinalIgnoreCase));
        (await db.DialogStates.SingleOrDefaultAsync(d => d.ChatId == new TelegramChatId(4004), ct))
            .Should()
            .NotBeNull();
    }

    // "skip" is the path every real user actually has — Telegram clients won't let you send a
    // truly empty message, so a blank reply is only reachable from a test or API client.
    [Test]
    public async Task NewFranchiseAcceptsSkipForVenueCapacityAndSchedule()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4025, telegramUserId: 4025, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4025, 4025, "/newfranchise"), ct);
        await router.RouteAsync(MessageUpdate(4025, 4025, "Travelling Quiz"), ct);
        await router.RouteAsync(MessageUpdate(4025, 4025, "skip"), ct); // no venue
        await router.RouteAsync(MessageUpdate(4025, 4025, "SKIP"), ct); // no capacity
        await router.RouteAsync(MessageUpdate(4025, 4025, "skip"), ct); // no price
        await router.RouteAsync(MessageUpdate(4025, 4025, "skip"), ct); // no schedule

        var franchise = await db.Franchises.AsNoTracking().SingleAsync(f => f.TeamId == team.Id, ct);
        franchise.DefaultVenue.Should().BeNull();
        franchise.DefaultCapacity.Should().BeNull();
        franchise.Schedule.Should().BeEmpty();
    }

    // Taps the actual rendered Skip button rather than typing the word — proves the button is
    // really wired to the same skip behavior, not just that the keyword works.
    [Test]
    public async Task TappingTheSkipButtonSkipsTheVenuePrompt()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4029, telegramUserId: 4029, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4029, 4029, "/newfranchise"), ct);
        await router.RouteAsync(MessageUpdate(4029, 4029, "Travelling Quiz"), ct);

        var skipData = bot.LastSentKeyboard(4029)!
            .InlineKeyboard.SelectMany(row => row)
            .Single(b => b.CallbackData!.StartsWith($"{CallbackData.Skip}:", StringComparison.Ordinal))
            .CallbackData!;
        await router.RouteAsync(CallbackUpdate(4029, 4029, skipData), ct);
        await router.RouteAsync(MessageUpdate(4029, 4029, "20"), ct); // capacity
        await router.RouteAsync(MessageUpdate(4029, 4029, "skip"), ct); // price
        await router.RouteAsync(MessageUpdate(4029, 4029, "skip"), ct); // schedule

        var franchise = await db.Franchises.AsNoTracking().SingleAsync(f => f.TeamId == team.Id, ct);
        franchise.DefaultVenue.Should().BeNull();
        franchise.DefaultCapacity.Should().Be(20);
    }

    // Venue and capacity overrides on the confirm screen aren't skippable — Confirm requires
    // both, so the prompt must not offer a way to clear them back to unset.
    [Test]
    public async Task TheVenueOverridePromptHasCancelButNoSkipButton()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4030, telegramUserId: 4030, ct);
        team.TimeZoneId = "Europe/Berlin";
        await db.SaveChangesAsync(ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4030, 4030, "/newgame"), ct);
        await router.RouteAsync(CallbackUpdate(4030, 4030, CallbackData.Format(CallbackData.OneOff, 0L)), ct);
        await router.RouteAsync(MessageUpdate(4030, 4030, "One-off quiz"), ct);
        await router.RouteAsync(MessageUpdate(4030, 4030, "The Pub"), ct);
        await router.RouteAsync(MessageUpdate(4030, 4030, "2026-09-12"), ct);
        await router.RouteAsync(MessageUpdate(4030, 4030, "19:00"), ct);
        await router.RouteAsync(MessageUpdate(4030, 4030, "20"), ct);
        await router.RouteAsync(MessageUpdate(4030, 4030, "skip"), ct); // price -> lands on Confirm

        await router.RouteAsync(
            CallbackUpdate(4030, 4030, CallbackData.Format(CallbackData.EditField, NewGameDialogData.OverrideVenue)),
            ct
        );

        var buttons = bot.LastSentKeyboard(4030)!.InlineKeyboard.SelectMany(row => row).ToList();
        buttons
            .Should()
            .ContainSingle(b => b.CallbackData!.StartsWith($"{CallbackData.CancelDialog}:", StringComparison.Ordinal));
        buttons.Should().NotContain(b => b.CallbackData!.StartsWith($"{CallbackData.Skip}:", StringComparison.Ordinal));
    }

    // Answering a Cancel/Skip prompt with an ordinary text reply moved the wizard on but left
    // the old prompt's keyboard sitting in the chat, still tappable, no longer pointing at
    // anything the dialog was still waiting on — reported as "the cancel button remains".
    [Test]
    public async Task AnsweringAPromptWithTextClearsItsOwnCancelKeyboard()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        await SeedCaptainedTeamAsync(db, chatId: 4034, telegramUserId: 4034, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4034, 4034, "/newfranchise"), ct);
        bot.ClearedKeyboards().Should().BeEmpty();

        await router.RouteAsync(MessageUpdate(4034, 4034, "Travelling Quiz"), ct);

        bot.ClearedKeyboards().Should().ContainSingle();
    }

    // A validation failure re-shows the very same prompt for a retry — its keyboard is still
    // exactly what's needed, so it must not be stripped the way a real advance would strip it.
    [Test]
    public async Task AValidationErrorRetryLeavesThePromptsKeyboardAlone()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        await SeedCaptainedTeamAsync(db, chatId: 4035, telegramUserId: 4035, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4035, 4035, "/newfranchise"), ct);
        await router.RouteAsync(MessageUpdate(4035, 4035, "   "), ct); // blank name is rejected

        bot.ClearedKeyboards().Should().BeEmpty();
    }

    // Tapping Cancel abandons the whole creation, from any step — not just the confirm screen.
    [Test]
    public async Task CancelButtonAbandonsANewGameMidWizard()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4031, telegramUserId: 4031, ct);
        team.TimeZoneId = "Europe/Berlin";
        await db.SaveChangesAsync(ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4031, 4031, "/newgame"), ct);
        await router.RouteAsync(CallbackUpdate(4031, 4031, CallbackData.Format(CallbackData.OneOff, 0L)), ct);
        await router.RouteAsync(MessageUpdate(4031, 4031, "One-off quiz"), ct);

        var cancelData = bot.LastSentKeyboard(4031)!
            .InlineKeyboard.SelectMany(row => row)
            .Single(b => b.CallbackData!.StartsWith($"{CallbackData.CancelDialog}:", StringComparison.Ordinal))
            .CallbackData!;
        await router.RouteAsync(CallbackUpdate(4031, 4031, cancelData), ct);

        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(4031), ct)).Should().Be(0);
        (await db.Games.CountAsync(g => g.TeamId == team.Id, ct)).Should().Be(0);
    }

    // Covers both new capabilities together: a franchise with no fixed schedule needs a
    // custom date (there are no predefined ones to pick from), and one with no default
    // venue/capacity must have both filled in as overrides before Confirm is allowed to
    // create anything.
    [Test]
    public async Task NewGameFromAFranchiseWithNoDefaultsNeedsACustomDateAndBothOverridesBeforeCreating()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4026, telegramUserId: 4026, ct);
        team.TimeZoneId = "Europe/Berlin";
        var franchise = new Franchise
        {
            TeamId = team.Id,
            Name = "Travelling Quiz",
            DefaultVenue = null,
            DefaultCapacity = null,
            Schedule = [],
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Franchises.Add(franchise);
        await db.SaveChangesAsync(ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4026, 4026, "/newgame"), ct);
        await router.RouteAsync(
            CallbackUpdate(4026, 4026, CallbackData.Format(CallbackData.PickFranchise, franchise.Id)),
            ct
        );
        await router.RouteAsync(CallbackUpdate(4026, 4026, CallbackData.Format(CallbackData.CustomDate, 0L)), ct);
        await router.RouteAsync(MessageUpdate(4026, 4026, "2026-09-12"), ct);
        await router.RouteAsync(MessageUpdate(4026, 4026, "19:00"), ct);

        // Confirm is rejected — neither Venue nor Capacity is set yet.
        var confirmData = CallbackData.Format(CallbackData.Confirm, 0L);
        await router.RouteAsync(CallbackUpdate(4026, 4026, confirmData), ct);
        bot.AnsweredCallbackAlerts().Should().ContainSingle();
        (await db.Games.CountAsync(g => g.TeamId == team.Id, ct)).Should().Be(0);

        await router.RouteAsync(
            CallbackUpdate(4026, 4026, CallbackData.Format(CallbackData.EditField, NewGameDialogData.OverrideVenue)),
            ct
        );
        await router.RouteAsync(MessageUpdate(4026, 4026, "The Travelling Pub"), ct);
        await router.RouteAsync(
            CallbackUpdate(4026, 4026, CallbackData.Format(CallbackData.EditField, NewGameDialogData.OverrideCapacity)),
            ct
        );
        await router.RouteAsync(MessageUpdate(4026, 4026, "15"), ct);

        await router.RouteAsync(CallbackUpdate(4026, 4026, confirmData), ct);

        var game = await db.Games.AsNoTracking().SingleAsync(g => g.TeamId == team.Id, ct);
        game.Venue.Should().Be("The Travelling Pub");
        game.Capacity.Should().Be(15);
    }

    [Test]
    public async Task CancelCommandClearsAnInProgressFranchiseWizard()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4027, telegramUserId: 4027, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4027, 4027, "/newfranchise"), ct);
        await router.RouteAsync(MessageUpdate(4027, 4027, "Abandoned Franchise"), ct);
        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(4027), ct)).Should().Be(1);

        await router.RouteAsync(MessageUpdate(4027, 4027, "/cancel"), ct);

        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(4027), ct)).Should().Be(0);
        (await db.Franchises.CountAsync(f => f.TeamId == team.Id, ct)).Should().Be(0);
        bot.SentTexts(4027).Should().Contain(text => text.Contains("Cancelled", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task CancelCommandWithNoActiveDialogSaysSo()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        await SeedCaptainedTeamAsync(db, chatId: 4028, telegramUserId: 4028, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4028, 4028, "/cancel"), ct);

        bot.SentTexts(4028)
            .Should()
            .ContainSingle(text => text.Contains("Nothing", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task SetLanguageRejectsAnUnsupportedCode()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4010, telegramUserId: 4010, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4010, 4010, "/setlanguage fr"), ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).Locale.Should().Be("en");
        bot.SentTexts().Should().ContainSingle(text => text.Contains("fr", StringComparison.Ordinal));
    }

    [Test]
    public async Task SetLanguageAcceptsASupportedCodeAndConfirmsInTheNewLanguage()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4011, telegramUserId: 4011, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4011, 4011, "/setlanguage ru"), ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).Locale.Should().Be("ru");
        bot.SentTexts().Should().ContainSingle(text => text.Contains("ru", StringComparison.Ordinal));
    }

    [Test]
    public async Task NonCaptainsCannotSetTheLanguage()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 4012, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4012, 4012, "/setlanguage ru"), ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).Locale.Should().Be("en");
        bot.SentTexts().Should().ContainSingle(text => text.Contains("captain", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task SetRemindersAcceptsAllThreeSlotsTogether()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4020, telegramUserId: 4020, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4020, 4020, "/setreminders 21:00 08:30 01:30"), ct);

        var refreshed = await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct);
        refreshed.EveningBeforeAt.Should().Be(new TimeOnly(21, 0));
        refreshed.MorningOfAt.Should().Be(new TimeOnly(8, 30));
        refreshed.BeforeStartLead.Should().Be(TimeSpan.FromMinutes(90));
        bot.SentTexts().Should().ContainSingle();
    }

    [Test]
    public async Task SetRemindersRejectsTheWrongNumberOfArguments()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedCaptainedTeamAsync(db, chatId: 4021, telegramUserId: 4021, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4021, 4021, "/setreminders 21:00 08:30"), ct);

        var refreshed = await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct);
        refreshed.EveningBeforeAt.Should().Be(default(TimeOnly));
        bot.SentTexts().Should().ContainSingle(text => text.Contains("HH:mm", StringComparison.Ordinal));
    }

    [Test]
    public async Task NonCaptainsCannotSetReminders()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 4022, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4022, 4022, "/setreminders 21:00 08:30 01:30"), ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct))
            .EveningBeforeAt.Should()
            .Be(default(TimeOnly));
        bot.SentTexts().Should().ContainSingle(text => text.Contains("captain", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task MyLanguageSetsThePlayersOwnLocaleWithoutTouchingTheTeams()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 4013, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4013, 4013, "/mylanguage de"), ct);

        (await db.Players.AsNoTracking().SingleAsync(p => p.TelegramUserId == new TelegramUserId(4013), ct))
            .Locale.Should()
            .Be("de");
        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct)).Locale.Should().Be("en");
        bot.SentTexts().Should().ContainSingle();
    }

    [Test]
    public async Task MyLanguageRejectsAnUnsupportedCode()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        await SeedTeamAsync(db, chatId: 4014, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4014, 4014, "/mylanguage klingon"), ct);

        (await db.Players.AsNoTracking().SingleAsync(p => p.TelegramUserId == new TelegramUserId(4014), ct))
            .Locale.Should()
            .BeNull();
        bot.SentTexts().Should().ContainSingle(text => text.Contains("klingon", StringComparison.Ordinal));
    }

    [Test]
    public async Task NonCaptainsCannotSetTheTimeZone()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        await SeedTeamAsync(db, chatId: 4005, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4005, 4005, "/settimezone Europe/Berlin"), ct);

        bot.SentTexts().Should().ContainSingle(text => text.Contains("captain", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task StartGreetsAndLazilyCreatesThePlayer()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        await SeedTeamAsync(db, chatId: 4006, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4006, 42, "/start", ChatType.Private), ct);

        bot.SentTexts().Should().ContainSingle();
        (await db.Players.SingleOrDefaultAsync(p => p.TelegramUserId == new TelegramUserId(42), ct))
            .Should()
            .NotBeNull();
    }

    [Test]
    public async Task HelpListsTheCommandsInTheGroupsLanguage()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 4007, ct);
        team.Locale = "ru";
        await db.SaveChangesAsync(ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4007, 43, "/help"), ct);

        bot.SentTexts().Should().ContainSingle(text => text.Contains("/newgame", StringComparison.Ordinal));
        bot.SentTexts().Should().ContainSingle(text => text.Contains("Капитанам", StringComparison.Ordinal));
    }

    [Test]
    public async Task HelpWorksWithNoTeamAtAll()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4008, 44, "/help", ChatType.Private), ct);

        bot.SentTexts().Should().ContainSingle(text => text.Contains("/managecaptains", StringComparison.Ordinal));
    }

    // The actual bug this guards against: a captain who set their own DM language to German
    // running /help in the team's Russian-language group must still see it in Russian —
    // CLAUDE.md's "group messages use the team's language" is not a preference the captain's
    // own /mylanguage choice can override.
    [Test]
    public async Task HelpInAGroupUsesTheTeamsLanguageEvenWhenThePlayerSetADifferentPersonalLanguage()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 4009, ct);
        team.Locale = "ru";
        await db.SaveChangesAsync(ct);
        var (router, bot) = CreateRouter(db);
        await router.RouteAsync(MessageUpdate(4009, 45, "/mylanguage de"), ct);

        await router.RouteAsync(MessageUpdate(4009, 45, "/help"), ct);

        bot.SentTexts().Should().ContainSingle(text => text.Contains("Капитанам", StringComparison.Ordinal));
    }

    // The DM side of the same split: with no group to defer to, the player's own choice
    // still wins, exactly as before this behavior was split by chat type.
    [Test]
    public async Task HelpInADmStillUsesThePlayersOwnLanguage()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (router, bot) = CreateRouter(db);
        await router.RouteAsync(MessageUpdate(4010, 46, "/mylanguage de", ChatType.Private), ct);

        await router.RouteAsync(MessageUpdate(4010, 46, "/help", ChatType.Private), ct);

        bot.SentTexts().Should().ContainSingle(text => text.Contains("Für Captains", StringComparison.Ordinal));
    }

    // The proactive half of TeamChatMigration: the migrate notice Telegram delivers as a
    // textless Message, which RouteAsync's ordinary message filter would otherwise discard
    // before anything sees it.
    [Test]
    public async Task ChatMigrationUpdatesTheTeamsStoredChatId()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 4023, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(MigrateUpdate(oldChatId: 4023, newChatId: -1004023999999), ct);

        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct))
            .ChatId.Should()
            .Be(new TelegramChatId(-1004023999999));
    }

    // The common conflict case: TeamBootstrapService's own my_chat_member "added" handler
    // already bootstrapped a fresh team for the new chat id before this migrate message was
    // processed, but nobody's actually configured it (no timezone, no Board) — it's the one
    // that retires, and the real team (with its history) takes over the new chat id.
    [Test]
    public async Task ChatMigrationRetiresTheUnusedBootstrapTeamAndMovesTheRealOneOver()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var realTeam = await SeedTeamAsync(db, chatId: 4024, ct);
        var bootstrapTeam = await SeedTeamAsync(db, chatId: -1004024999999, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(MigrateUpdate(oldChatId: 4024, newChatId: -1004024999999), ct);

        var refreshedReal = await db.Teams.AsNoTracking().SingleAsync(t => t.Id == realTeam.Id, ct);
        refreshedReal.ChatId.Should().Be(new TelegramChatId(-1004024999999));
        refreshedReal.DeactivatedAt.Should().BeNull();
        var refreshedBootstrap = await db
            .Teams.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(t => t.Id == bootstrapTeam.Id, ct);
        refreshedBootstrap.DeactivatedAt.Should().NotBeNull();
    }

    // The rarer case: a captain actually reached the fresh bootstrap team first (set its
    // timezone, and it's already posted a Board) — it's the real active continuation, so the
    // old team retires instead, same as before this fix.
    [Test]
    public async Task ChatMigrationRetiresTheOldTeamWhenTheFreshOneWasActuallyConfigured()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var staleTeam = await SeedTeamAsync(db, chatId: 4032, ct);
        var configuredTeam = new Team
        {
            ChatId = new TelegramChatId(-1004032999999),
            Name = "Test team",
            Locale = "en",
            TimeZoneId = "Europe/Berlin",
            BoardMessageId = new TelegramMessageId(1),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Teams.Add(configuredTeam);
        await db.SaveChangesAsync(ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(MigrateUpdate(oldChatId: 4032, newChatId: -1004032999999), ct);

        var refreshedStale = await db
            .Teams.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(t => t.Id == staleTeam.Id, ct);
        refreshedStale.ChatId.Should().Be(new TelegramChatId(4032));
        refreshedStale.DeactivatedAt.Should().NotBeNull();
        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == configuredTeam.Id, ct)).DeactivatedAt.Should().BeNull();
    }

    // The exact shape TeamChatMigration's filtered index makes possible: an active team and
    // a retired one sharing the same chat id (the retired one lost the chat id fight but,
    // per invariant 7, is never deleted — it just keeps the id it had). Team's global query
    // filter (TeamConfiguration) needs to resolve every ordinary interaction to the active
    // one without throwing on the retired one sharing its chat id.
    [Test]
    public async Task AnOrdinaryMessageResolvesTheActiveTeamEvenWhenARetiredOneSharesItsChatId()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var activeTeam = await SeedCaptainedTeamAsync(db, chatId: 4033, telegramUserId: 4033, ct);
        var retiredTeam = new Team
        {
            ChatId = new TelegramChatId(4033),
            Name = "Retired duplicate",
            Locale = "en",
            CreatedAt = DateTimeOffset.UtcNow,
            DeactivatedAt = DateTimeOffset.UtcNow,
        };
        db.Teams.Add(retiredTeam);
        await db.SaveChangesAsync(ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(4033, 4033, "/help"), ct);

        bot.SentTexts(4033).Should().ContainSingle();
        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == activeTeam.Id, ct)).DeactivatedAt.Should().BeNull();
    }

    private static Update MigrateUpdate(long oldChatId, long newChatId) =>
        new()
        {
            Id = 1,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = oldChatId, Type = ChatType.Group },
                MigrateToChatId = newChatId,
                Date = DateTime.UtcNow,
            },
        };

    private static (UpdateRouter Router, ITelegramBotClient Bot) CreateRouter(QuizrDb db)
    {
        var bot = TelegramBotClientTestHelper.Create();
        var clock = new FakeTimeProvider();
        var sender = new MessageSender(
            bot,
            new MessageEditDebouncer(bot, clock, NullLogger<MessageEditDebouncer>.Instance)
        );
        var strings = new Strings();
        var teamBootstrap = new TeamBootstrapService(db, sender, strings, clock);
        var playerBootstrap = new PlayerBootstrapService(db, clock);
        var teamGuard = new TeamGuard(db, bot);
        var signups = new SignupService(db, clock);
        var franchises = new FranchiseService(db, clock);
        var games = new GameService(db, clock);
        var participations = new ParticipationService(db, clock);
        var announcements = new AnnouncementService(db, sender, strings);
        var board = new BoardService(db, sender, bot, strings, NullLogger<BoardService>.Instance);

        var router = new UpdateRouter(
            db,
            sender,
            bot,
            strings,
            teamBootstrap,
            playerBootstrap,
            teamGuard,
            signups,
            franchises,
            games,
            participations,
            announcements,
            board,
            clock,
            NullLogger<UpdateRouter>.Instance
        );

        return (router, bot);
    }

    private static async Task<Team> SeedTeamAsync(QuizrDb db, long chatId, CancellationToken ct)
    {
        var team = new Team
        {
            ChatId = new TelegramChatId(chatId),
            Name = "Test team",
            Locale = "en",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return team;
    }

    private static async Task<Team> SeedCaptainedTeamAsync(
        QuizrDb db,
        long chatId,
        long telegramUserId,
        CancellationToken ct
    )
    {
        var team = await SeedTeamAsync(db, chatId, ct);

        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = "Captain",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);

        db.Memberships.Add(
            new Membership
            {
                TeamId = team.Id,
                PlayerId = player.Id,
                IsCaptain = true,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);

        return team;
    }

    private static Update MessageUpdate(
        long chatId,
        long telegramUserId,
        string text,
        ChatType chatType = ChatType.Supergroup
    ) =>
        new()
        {
            Id = 1,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = chatId, Type = chatType },
                From = new User { Id = telegramUserId, FirstName = "Test" },
                Text = text,
                Date = DateTime.UtcNow,
            },
        };

    private static Update CallbackUpdate(long chatId, long telegramUserId, string data) =>
        new()
        {
            Id = 1,
            CallbackQuery = new CallbackQuery
            {
                Id = "cq1",
                From = new User { Id = telegramUserId, FirstName = "Test" },
                Data = data,
                Message = new Message
                {
                    Id = 1,
                    Chat = new Chat { Id = chatId },
                    Date = DateTime.UtcNow,
                },
            },
        };
}
