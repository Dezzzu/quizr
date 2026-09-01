using System.Globalization;
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
using Game = Quizr.Domain.Entities.Game;

namespace Quizr.App.Tests;

// M9: reminders, act-on-behalf, decline, finish, captain grant/revoke — one full-flow test per
// feature, matching CLAUDE.md invariant 13's audit trail everywhere a captain acts on someone
// else's behalf.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class UpdateRouterM9Tests
{
    private readonly PostgresFixture _fixture;

    public UpdateRouterM9Tests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task MyRemindersCyclesAChannelThroughOffGroupDmAndBack()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8001, ct);
        var player = await SeedMemberAsync(db, team.Id, telegramUserId: 8001, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(8001, 8001, "/myreminders"), ct);

        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == player.Id, ct))
            .EveningBefore.Should()
            .Be(ReminderChannel.Off);

        await router.RouteAsync(
            CallbackUpdate(8001, 8001, CallbackData.Format(CallbackData.CycleReminderChannel, 0L)),
            ct
        );
        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == player.Id, ct))
            .EveningBefore.Should()
            .Be(ReminderChannel.Group);

        await router.RouteAsync(
            CallbackUpdate(8001, 8001, CallbackData.Format(CallbackData.CycleReminderChannel, 0L)),
            ct
        );
        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == player.Id, ct))
            .EveningBefore.Should()
            .Be(ReminderChannel.Dm);

        await router.RouteAsync(
            CallbackUpdate(8001, 8001, CallbackData.Format(CallbackData.CycleReminderChannel, 0L)),
            ct
        );
        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == player.Id, ct))
            .EveningBefore.Should()
            .Be(ReminderChannel.Off);
    }

    [Test]
    public async Task MyRemindersTogglesTheReserveReminderFlag()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8002, ct);
        var player = await SeedMemberAsync(db, team.Id, telegramUserId: 8002, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(8002, 8002, "/myreminders"), ct);
        await router.RouteAsync(
            CallbackUpdate(8002, 8002, CallbackData.Format(CallbackData.ToggleReserveReminder, 0L)),
            ct
        );

        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == player.Id, ct))
            .RemindWhenReserve.Should()
            .BeTrue();
    }

    // Reminder settings previously had no way to end the interaction either — same
    // never-ending-menu bug as Manage players/guests, just missed in that pass.
    [Test]
    public async Task DoneClearsTheReminderSettingsKeyboard()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8018, ct);
        await SeedMemberAsync(db, team.Id, telegramUserId: 8018, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(8018, 8018, "/myreminders"), ct);
        await router.RouteAsync(CallbackUpdate(8018, 8018, CallbackData.Format(CallbackData.CloseView, 0L)), ct);

        bot.ClearedKeyboards().Should().ContainSingle();
    }

    [Test]
    public async Task ManagePlayersRegistersAndThenDropsAMemberOnTheirBehalfWithAnAuditTrail()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8003, capacity: 5, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80031, ct);
        var target = await SeedMemberAsync(db, game.TeamId, telegramUserId: 80032, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8003, 80031, CallbackData.Format(CallbackData.ManagePlayers, game.Id)),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(8003, 80031, CallbackData.Format(CallbackData.TogglePlayerSignup, target.Id)),
            ct
        );

        (
            await db
                .Signups.AsNoTracking()
                .CountAsync(s => s.GameId == game.Id && s.PlayerId == target.Id && s.CancelledAt == null, ct)
        )
            .Should()
            .Be(1);
        var registerEntry = await db.AuditEntries.SingleAsync(
            e => e.GameId == game.Id && e.Action == AuditActions.PlayerRegisteredOnBehalf,
            ct
        );
        registerEntry.ActorPlayerId.Should().Be(captain.Id);

        await router.RouteAsync(
            CallbackUpdate(8003, 80031, CallbackData.Format(CallbackData.TogglePlayerSignup, target.Id)),
            ct
        );

        (
            await db
                .Signups.AsNoTracking()
                .CountAsync(s => s.GameId == game.Id && s.PlayerId == target.Id && s.CancelledAt == null, ct)
        )
            .Should()
            .Be(0);
        var dropEntry = await db.AuditEntries.SingleAsync(
            e => e.GameId == game.Id && e.Action == AuditActions.PlayerDroppedOnBehalf,
            ct
        );
        dropEntry.ActorPlayerId.Should().Be(captain.Id);
    }

    [Test]
    public async Task NonCaptainsCannotOpenManagePlayers()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8004, capacity: 5, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8004, 80041, CallbackData.Format(CallbackData.ManagePlayers, game.Id)),
            ct
        );

        bot.AnsweredCallbackAlerts().Should().ContainSingle();
        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(8004), ct)).Should().Be(0);
    }

    // Manage players previously had no way to end the interaction either — every toggle just
    // re-rendered the same never-ending roster menu.
    [Test]
    public async Task DoneClearsTheManagePlayersKeyboardAndTheDialogBehindIt()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8012, capacity: 5, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80121, ct);
        var (router, bot) = CreateRouter(db);
        await router.RouteAsync(
            CallbackUpdate(8012, 80121, CallbackData.Format(CallbackData.ManagePlayers, game.Id)),
            ct
        );

        await router.RouteAsync(CallbackUpdate(8012, 80121, CallbackData.Format(CallbackData.CloseView, 0L)), ct);

        bot.ClearedKeyboards().Should().ContainSingle();
        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(8012) && d.PlayerId == captain.Id, ct))
            .Should()
            .Be(0);
    }

    [Test]
    public async Task DeclineRequiresConfirmationAndRecordsAnAuditEntry()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8005, capacity: 5, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80051, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8005, 80051, CallbackData.Format(CallbackData.DeclineGame, game.Id)),
            ct
        );
        (await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct)).DeclinedAt.Should().BeNull();

        await router.RouteAsync(
            CallbackUpdate(8005, 80051, CallbackData.Format(CallbackData.ConfirmDecline, game.Id)),
            ct
        );

        (await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct)).DeclinedAt.Should().NotBeNull();
        var entry = await db.AuditEntries.SingleAsync(
            e => e.GameId == game.Id && e.Action == AuditActions.GameDeclined,
            ct
        );
        entry.ActorPlayerId.Should().Be(captain.Id);
    }

    [Test]
    public async Task CancellingTheDeclinePromptLeavesTheGameUntouched()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8006, capacity: 5, ct);
        await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80061, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8006, 80061, CallbackData.Format(CallbackData.DeclineGame, game.Id)),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(8006, 80061, CallbackData.Format(CallbackData.CancelDecline, game.Id)),
            ct
        );

        (await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct)).DeclinedAt.Should().BeNull();
        (await db.AuditEntries.CountAsync(e => e.GameId == game.Id, ct)).Should().Be(0);
    }

    [Test]
    public async Task FinishButtonMaterializesParticipationAndRecordsAnAuditEntry()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8007, capacity: 5, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80071, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(CallbackUpdate(8007, 80071, CallbackData.Format(CallbackData.FinishGame, game.Id)), ct);

        (await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id, ct)).FinishedAt.Should().NotBeNull();
        var entry = await db.AuditEntries.SingleAsync(
            e => e.GameId == game.Id && e.Action == AuditActions.GameFinished,
            ct
        );
        entry.ActorPlayerId.Should().Be(captain.Id);
    }

    [Test]
    public async Task ManageCaptainsGrantsAndThenRevokesCaptaincyWithAnAuditTrail()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8008, ct);
        var captain = await SeedCaptainAsync(db, team.Id, telegramUserId: 80081, ct);
        var target = await SeedMemberAsync(db, team.Id, telegramUserId: 80082, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(8008, 80081, "/managecaptains"), ct);

        // The member list itself is the captain's own business, like the manage-players one.
        bot.EphemeralTexts().Should().ContainSingle(e => e.ReceiverUserId == 80081);
        bot.SentTexts(8008).Should().BeEmpty();

        await router.RouteAsync(
            CallbackUpdate(8008, 80081, CallbackData.Format(CallbackData.ToggleCaptain, target.Id)),
            ct
        );

        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == target.Id, ct)).IsCaptain.Should().BeTrue();
        var grantEntry = await db.AuditEntries.SingleAsync(e => e.Action == AuditActions.CaptainGranted, ct);
        grantEntry.ActorPlayerId.Should().Be(captain.Id);

        await router.RouteAsync(
            CallbackUpdate(8008, 80081, CallbackData.Format(CallbackData.ToggleCaptain, target.Id)),
            ct
        );

        (await db.Memberships.AsNoTracking().SingleAsync(m => m.PlayerId == target.Id, ct))
            .IsCaptain.Should()
            .BeFalse();
        var revokeEntry = await db.AuditEntries.SingleAsync(e => e.Action == AuditActions.CaptainRevoked, ct);
        revokeEntry.ActorPlayerId.Should().Be(captain.Id);
    }

    [Test]
    public async Task NonCaptainsCannotManageCaptains()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8009, ct);
        await SeedMemberAsync(db, team.Id, telegramUserId: 8009, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(8009, 8009, "/managecaptains"), ct);

        // As private as the view it refuses: the command is taken away, so a public refusal
        // would be left with nothing in the chat to explain it.
        bot.EphemeralTexts()
            .Should()
            .ContainSingle(e => e.Text.Contains("captain", StringComparison.OrdinalIgnoreCase));
    }

    // /managecaptains was another view with no way to end the interaction — found while
    // building a shared Done row for the views that already had one.
    [Test]
    public async Task DoneClearsTheManageCaptainsKeyboard()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8021, ct);
        await SeedCaptainAsync(db, team.Id, telegramUserId: 8021, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(8021, 8021, "/managecaptains"), ct);
        await router.RouteAsync(CallbackUpdate(8021, 8021, CallbackData.Format(CallbackData.CloseView, 0L)), ct);

        bot.ClearedKeyboards().Should().ContainSingle();
    }

    // Editing a finished game's roster (invariant 11's second half) is also a captain action
    // that affects someone else's record — added to invariant 13's list after the first pass
    // at audit logging turned out to have missed it.
    [Test]
    public async Task TogglingPlayedOnAFinishedGamesRosterRecordsAnAuditEntry()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (game, participation) = await SeedFinishedGameWithParticipationAsync(db, chatId: 8010, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80101, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8010, 80101, CallbackData.Format(CallbackData.TogglePlayed, participation.Id)),
            ct
        );

        (await db.Participations.AsNoTracking().SingleAsync(p => p.Id == participation.Id, ct))
            .Played.Should()
            .BeFalse();
        var entry = await db.AuditEntries.SingleAsync(e => e.GameId == game.Id, ct);
        entry.Action.Should().Be(AuditActions.ParticipationPlayedToggled);
        entry.ActorPlayerId.Should().Be(captain.Id);
    }

    [Test]
    public async Task AddingAVenuePlayerRecordsAnAuditEntry()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (game, _) = await SeedFinishedGameWithParticipationAsync(db, chatId: 8011, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80111, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(CallbackUpdate(8011, 80111, CallbackData.Format(CallbackData.AddPlayer, game.Id)), ct);
        await router.RouteAsync(MessageUpdate(8011, 80111, "Walk-in Wendy"), ct);

        var added = await db
            .Participations.AsNoTracking()
            .SingleAsync(p => p.GameId == game.Id && p.Name == "Walk-in Wendy", ct);
        added.Kind.Should().Be(ParticipationKind.VenueAssigned);
        var entry = await db.AuditEntries.SingleAsync(
            e => e.GameId == game.Id && e.Action == AuditActions.VenuePlayerAdded,
            ct
        );
        entry.ActorPlayerId.Should().Be(captain.Id);
    }

    // The roster view's own "Add player" button used to hardcode GameId 0 rather than the
    // actual game — every prior test (including the one above) built its own correct callback
    // data by hand instead of tapping the real rendered button, so nothing caught it. This one
    // taps the button as actually sent.
    [Test]
    public async Task TappingAddPlayerFromTheActualRosterKeyboardTargetsTheRightGame()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (game, _) = await SeedFinishedGameWithParticipationAsync(db, chatId: 8022, ct);
        await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80221, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8022, 80221, CallbackData.Format(CallbackData.ManageRoster, game.Id)),
            ct
        );

        var addPlayerData = bot.LastSentKeyboard(8022)!
            .InlineKeyboard.SelectMany(row => row)
            .Single(b => b.CallbackData!.StartsWith($"{CallbackData.AddPlayer}:", StringComparison.Ordinal))
            .CallbackData!;
        addPlayerData.Should().Be(CallbackData.Format(CallbackData.AddPlayer, game.Id));

        await router.RouteAsync(CallbackUpdate(8022, 80221, addPlayerData), ct);
        await router.RouteAsync(MessageUpdate(8022, 80221, "Walk-in Wendy"), ct);

        (await db.Participations.AsNoTracking().SingleAsync(p => p.GameId == game.Id && p.Name == "Walk-in Wendy", ct))
            .Kind.Should()
            .Be(ParticipationKind.VenueAssigned);
    }

    // Manage guests (captain-only): a captain can add a team guest and remove anyone's guest —
    // including a guest they don't own themselves — even while not signed up for the game.
    [Test]
    public async Task CaptainAddsATeamGuestAndRemovesAnyGuestWithoutBeingSignedUpThemself()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8013, capacity: 5, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80131, ct);
        var member = await SeedMemberAsync(db, game.TeamId, telegramUserId: 80132, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(CallbackUpdate(8013, 80132, CallbackData.Format(CallbackData.Join, game.Id)), ct);
        await router.RouteAsync(CallbackUpdate(8013, 80132, CallbackData.Format(CallbackData.Guest, game.Id)), ct);
        var memberGuest = await db
            .Signups.AsNoTracking()
            .SingleAsync(s => s.GameId == game.Id && s.InvitedByPlayerId == member.Id, ct);

        // The captain isn't signed up for the game themselves — Manage guests works anyway.
        await router.RouteAsync(
            CallbackUpdate(8013, 80131, CallbackData.Format(CallbackData.ManageGuests, game.Id)),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(8013, 80131, CallbackData.Format(CallbackData.AddTeamGuest, game.Id)),
            ct
        );
        await router.RouteAsync(MessageUpdate(8013, 80131, "Walk-in Wendy"), ct);

        var teamGuest = await db
            .Signups.AsNoTracking()
            .SingleAsync(s => s.GameId == game.Id && s.GuestName == "Walk-in Wendy", ct);
        teamGuest.PlayerId.Should().BeNull();
        teamGuest.InvitedByPlayerId.Should().BeNull();
        var addEntry = await db.AuditEntries.SingleAsync(
            e => e.GameId == game.Id && e.Action == AuditActions.TeamGuestAdded,
            ct
        );
        addEntry.ActorPlayerId.Should().Be(captain.Id);

        // Removing the member's own guest on their behalf is the one path self-service can
        // never reach.
        await router.RouteAsync(
            CallbackUpdate(8013, 80131, CallbackData.Format(CallbackData.RemoveGuestOnBehalf, memberGuest.Id)),
            ct
        );

        (await db.Signups.AsNoTracking().SingleAsync(s => s.Id == memberGuest.Id, ct)).CancelledAt.Should().NotBeNull();
        var removeEntry = await db.AuditEntries.SingleAsync(
            e => e.GameId == game.Id && e.Action == AuditActions.GuestRemovedOnBehalf,
            ct
        );
        removeEntry.ActorPlayerId.Should().Be(captain.Id);
    }

    [Test]
    public async Task NonCaptainsCannotManageGuests()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8014, capacity: 5, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8014, 80141, CallbackData.Format(CallbackData.ManageGuests, game.Id)),
            ct
        );

        bot.AnsweredCallbackAlerts().Should().ContainSingle();
        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(8014), ct)).Should().Be(0);
    }

    // --- Nudge (issue #5): pings only the still-selected players who signed up and are late —
    // never the captain themselves, since they're the one at the venue noticing who's missing. ---

    [Test]
    public async Task NudgeExcludesTheCaptainAndOnlySendsToPlayersStillSelected()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8015, capacity: 5, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80151, ct);
        var alice = await SeedMemberAsync(db, game.TeamId, telegramUserId: 80152, ct);
        var bob = await SeedMemberAsync(db, game.TeamId, telegramUserId: 80153, ct);
        var (router, bot) = CreateRouter(db);

        // The captain is playing too, but presumably already at the venue — never a target.
        await router.RouteAsync(CallbackUpdate(8015, 80151, CallbackData.Format(CallbackData.Join, game.Id)), ct);
        await router.RouteAsync(CallbackUpdate(8015, 80152, CallbackData.Format(CallbackData.Join, game.Id)), ct);
        await router.RouteAsync(CallbackUpdate(8015, 80153, CallbackData.Format(CallbackData.Join, game.Id)), ct);

        await router.RouteAsync(CallbackUpdate(8015, 80151, CallbackData.Format(CallbackData.Nudge, game.Id)), ct);
        // Uncheck Bob — only Alice should get pinged.
        await router.RouteAsync(
            CallbackUpdate(8015, 80151, CallbackData.Format(CallbackData.ToggleNudgeTarget, bob.Id)),
            ct
        );
        await router.RouteAsync(CallbackUpdate(8015, 80151, CallbackData.Format(CallbackData.SendNudge, game.Id)), ct);

        var sent = bot.SentTexts(8015)
            .Should()
            .ContainSingle(text => text.Contains("waiting for you", StringComparison.Ordinal))
            .Subject;
        sent.Should().Contain(alice.DisplayName);
        sent.Should().NotContain(bob.DisplayName);
        sent.Should().NotContain(captain.DisplayName);
        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(8015) && d.PlayerId == captain.Id, ct))
            .Should()
            .Be(0);
    }

    [Test]
    public async Task NudgeAnswersAnAlertWhenOnlyTheCaptainIsPlaying()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8016, capacity: 5, ct);
        var captain = await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80161, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(CallbackUpdate(8016, 80161, CallbackData.Format(CallbackData.Join, game.Id)), ct);
        await router.RouteAsync(CallbackUpdate(8016, 80161, CallbackData.Format(CallbackData.Nudge, game.Id)), ct);

        bot.AnsweredCallbackAlerts().Should().ContainSingle();
        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(8016) && d.PlayerId == captain.Id, ct))
            .Should()
            .Be(0);
    }

    [Test]
    public async Task AnyoneCanOpenNudgeNotJustCaptains()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8017, capacity: 5, ct);
        await SeedMemberAsync(db, game.TeamId, telegramUserId: 80171, ct);
        var (router, bot) = CreateRouter(db);
        await router.RouteAsync(CallbackUpdate(8017, 80171, CallbackData.Format(CallbackData.Join, game.Id)), ct);

        // 80172 is an ordinary member with no captaincy of any kind.
        await router.RouteAsync(CallbackUpdate(8017, 80172, CallbackData.Format(CallbackData.Nudge, game.Id)), ct);

        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(8017), ct)).Should().Be(1);
        bot.EphemeralTexts().Should().Contain(e => e.ReceiverUserId == 80172);
    }

    // The field-picker keyboard shown right after /newfranchise finishes is the same one
    // /editfranchise shows, and its buttons only mean something with an EditFranchise dialog
    // behind them — a regression test for the one that was missing right after creation.
    [Test]
    public async Task TheFieldButtonsShownAfterCreatingAFranchiseActuallyEditIt()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8019, ct);
        await SeedCaptainAsync(db, team.Id, telegramUserId: 8019, ct);
        var (router, _) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(8019, 8019, "/newfranchise"), ct);
        await router.RouteAsync(MessageUpdate(8019, 8019, "Quiz Masters"), ct);
        await router.RouteAsync(MessageUpdate(8019, 8019, "The Original Pub"), ct);
        await router.RouteAsync(MessageUpdate(8019, 8019, "20"), ct);
        await router.RouteAsync(MessageUpdate(8019, 8019, "skip"), ct);
        await router.RouteAsync(MessageUpdate(8019, 8019, "Mon-Fri: 19:00, Sat: 16:00, Sun: 16:00"), ct);

        await router.RouteAsync(
            CallbackUpdate(8019, 8019, CallbackData.Format(CallbackData.EditField, EditFranchiseDialogData.Venue)),
            ct
        );
        await router.RouteAsync(MessageUpdate(8019, 8019, "The New Pub"), ct);

        (await db.Franchises.AsNoTracking().SingleAsync(f => f.TeamId == team.Id, ct))
            .DefaultVenue.Should()
            .Be("The New Pub");

        // A second edit in the same sitting — the field-picker keyboard shown after applying
        // an edit only means anything while its EditFranchise dialog is still alive, so this
        // one only passes if that dialog survives the first edit rather than being torn down.
        await router.RouteAsync(
            CallbackUpdate(8019, 8019, CallbackData.Format(CallbackData.EditField, EditFranchiseDialogData.Capacity)),
            ct
        );
        await router.RouteAsync(MessageUpdate(8019, 8019, "30"), ct);

        (await db.Franchises.AsNoTracking().SingleAsync(f => f.TeamId == team.Id, ct)).DefaultCapacity.Should().Be(30);
    }

    [Test]
    public async Task DoneClearsTheFranchiseEditKeyboardAndTheDialogBehindIt()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8020, ct);
        var captain = await SeedCaptainAsync(db, team.Id, telegramUserId: 8020, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(MessageUpdate(8020, 8020, "/newfranchise"), ct);
        await router.RouteAsync(MessageUpdate(8020, 8020, "Quiz Masters"), ct);
        await router.RouteAsync(MessageUpdate(8020, 8020, "The Original Pub"), ct);
        await router.RouteAsync(MessageUpdate(8020, 8020, "20"), ct);
        await router.RouteAsync(MessageUpdate(8020, 8020, "skip"), ct);
        await router.RouteAsync(MessageUpdate(8020, 8020, "Mon-Fri: 19:00, Sat: 16:00, Sun: 16:00"), ct);

        // Each text reply above already cleared its own now-answered prompt's keyboard (the
        // stale-keyboard fix this test predates) — what's left to prove here is that Done adds
        // exactly one more: the field-picker's own.
        var clearedBeforeDone = bot.ClearedKeyboards().Count;

        await router.RouteAsync(CallbackUpdate(8020, 8020, CallbackData.Format(CallbackData.CloseView, 0L)), ct);

        bot.ClearedKeyboards().Count.Should().Be(clearedBeforeDone + 1);
        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(8020) && d.PlayerId == captain.Id, ct))
            .Should()
            .Be(0);
    }

    private static (UpdateRouter Router, ITelegramBotClient Bot) CreateRouter(QuizrDb db)
    {
        var bot = TelegramBotClientTestHelper.Create();
        var clock = new FakeTimeProvider();
        var sender = new MessageSender(
            bot,
            new MessageEditDebouncer(
                bot,
                clock,
                TelegramBotClientTestHelper.NullScopeFactory(),
                NullLogger<MessageEditDebouncer>.Instance
            )
        );
        var strings = new Strings();
        var teamBootstrap = new TeamBootstrapService(db, sender, strings, clock);
        var playerBootstrap = new PlayerBootstrapService(db, clock);
        var teamGuard = new TeamGuard(db, bot);
        var teams = new TeamService(db, teamGuard, clock);
        var dialogs = new DialogService(db, teamGuard, clock);
        var signups = new SignupService(db, teamGuard, clock);
        var franchises = new FranchiseService(db, teamGuard, clock);
        var games = new GameService(db, teamGuard, clock);
        var participations = new ParticipationService(db, teamGuard, clock);
        var announcements = new AnnouncementService(db, sender, strings);
        var board = new BoardService(db, sender, bot, strings, NullLogger<BoardService>.Instance);

        var router = new UpdateRouter(
            db,
            sender,
            bot,
            strings,
            teamBootstrap,
            playerBootstrap,
            teams,
            dialogs,
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

    // The captain-only views are private to the captain who opened them now — the team has no
    // reason to watch someone scroll a member list.
    [Test]
    public async Task ManagePlayersOpensPrivatelyForTheCaptainWhoAskedForIt()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8023, capacity: 5, ct);
        await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80231, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8023, 80231, CallbackData.Format(CallbackData.ManagePlayers, game.Id)),
            ct
        );

        bot.EphemeralTexts().Should().ContainSingle(e => e.ReceiverUserId == 80231);
        bot.SentTexts(8023).Should().BeEmpty();
    }

    // The keep-or-drop decision belongs to whoever invited the guest, which is not the person
    // who caused it when a captain drops someone on their behalf. Sending it to the captain
    // would leave the guest unresolved with nobody able to answer for them.
    [Test]
    public async Task AGuestChoiceRaisedByADropOnBehalfGoesToTheInviterNotTheCaptain()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8024, capacity: 5, ct);
        await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80241, ct);
        var target = await SeedMemberAsync(db, game.TeamId, telegramUserId: 80242, ct);
        var (router, bot) = CreateRouter(db);

        // The target signs up and brings a named guest, who therefore survives their drop only
        // if they say so.
        await router.RouteAsync(CallbackUpdate(8024, 80242, CallbackData.Format(CallbackData.Join, game.Id)), ct);
        await router.RouteAsync(CallbackUpdate(8024, 80242, CallbackData.Format(CallbackData.Guest, game.Id)), ct);
        var guest = await db.Signups.SingleAsync(s => s.GameId == game.Id && s.PlayerId == null, ct);
        guest.GuestName = "Sasha";
        await db.SaveChangesAsync(ct);

        await router.RouteAsync(
            CallbackUpdate(8024, 80241, CallbackData.Format(CallbackData.ManagePlayers, game.Id)),
            ct
        );
        await router.RouteAsync(
            CallbackUpdate(8024, 80241, CallbackData.Format(CallbackData.TogglePlayerSignup, target.Id)),
            ct
        );

        var choice = bot.EphemeralTexts()
            .Should()
            .ContainSingle(e => e.Text.Contains("Sasha", StringComparison.Ordinal))
            .Subject;
        choice.ReceiverUserId.Should().Be(80242, "the guest belongs to the dropped player, not the captain");
    }

    // The one captain-only button on the announcement opens privately, so the rest of the team
    // never sees what is behind it.
    [Test]
    public async Task TheManageDoorOpensPrivatelyForACaptainAndRefusesEveryoneElse()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8025, capacity: 5, ct);
        await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80251, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(CallbackUpdate(8025, 80252, CallbackData.Format(CallbackData.Manage, game.Id)), ct);
        bot.AnsweredCallbackAlerts().Should().ContainSingle();
        bot.EphemeralTexts().Should().BeEmpty();

        await router.RouteAsync(CallbackUpdate(8025, 80251, CallbackData.Format(CallbackData.Manage, game.Id)), ct);

        bot.EphemeralTexts().Should().ContainSingle(e => e.ReceiverUserId == 80251);
        bot.SentTexts(8025).Should().BeEmpty();
    }

    // The panel's Edit game button is /editgame with its pick-a-game step already answered, so
    // it has to land on the same field picker the command's own list leads to.
    [Test]
    public async Task TheManagePanelsEditGameButtonOpensTheFieldPickerForThatGame()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8027, capacity: 5, ct);
        await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80271, ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8027, 80271, CallbackData.Format(CallbackData.PickGameToEdit, game.Id)),
            ct
        );

        var dialog = await db.DialogStates.AsNoTracking().SingleAsync(d => d.ChatId == new TelegramChatId(8027), ct);
        dialog.Kind.Should().Be(DialogKinds.EditGame);
        dialog.Data.Should().Contain(game.Id.Value.ToString(CultureInfo.InvariantCulture));
        bot.EphemeralTexts().Should().ContainSingle(e => e.ReceiverUserId == 80271);
    }

    // A panel outlives its game: the 4-hour auto-finish (invariant 8) lands while it sits open,
    // and invariant 11 has turned that game's signups into history by then.
    [Test]
    public async Task TheEditGameButtonRefusesOnceItsGameHasFinished()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8028, capacity: 5, ct);
        await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80281, ct);
        game.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var (router, bot) = CreateRouter(db);

        await router.RouteAsync(
            CallbackUpdate(8028, 80281, CallbackData.Format(CallbackData.PickGameToEdit, game.Id)),
            ct
        );

        bot.AnsweredCallbackAlerts().Should().ContainSingle();
        (await db.DialogStates.CountAsync(d => d.ChatId == new TelegramChatId(8028), ct)).Should().Be(0);
    }

    // Done is tapped on the private view it closes, so the callback arrives with Id 0 and the
    // real handle on EphemeralMessageId. Closing used to edit the message id straight off the
    // callback, which meant asking Telegram to change message 0 — every ephemeral view's Done
    // button threw "message to edit not found", while tests that simulated the tap as though
    // it came from an ordinary message stayed green.
    [Test]
    public async Task DoneClosesAViewThatWasOpenedPrivately()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 8026, capacity: 5, ct);
        await SeedCaptainAsync(db, game.TeamId, telegramUserId: 80261, ct);
        var (router, bot) = CreateRouter(db);
        await router.RouteAsync(CallbackUpdate(8026, 80261, CallbackData.Format(CallbackData.Manage, game.Id)), ct);

        await router.RouteAsync(
            EphemeralCallbackUpdate(8026, 80261, CallbackData.Format(CallbackData.CloseView, 0L), 55),
            ct
        );

        bot.ClearedKeyboardCount().Should().Be(1);
    }

    private static async Task<Team> SeedTeamAsync(QuizrDb db, long chatId, CancellationToken ct)
    {
        var team = new Team
        {
            ChatId = new TelegramChatId(chatId),
            Name = "Test team",
            TimeZoneId = "Europe/Berlin",
            Locale = "en",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return team;
    }

    private static async Task<Player> SeedMemberAsync(
        QuizrDb db,
        TeamId teamId,
        long telegramUserId,
        CancellationToken ct
    )
    {
        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = $"Player {telegramUserId}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);

        db.Memberships.Add(
            new Membership
            {
                TeamId = teamId,
                PlayerId = player.Id,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);
        return player;
    }

    private static async Task<Player> SeedCaptainAsync(
        QuizrDb db,
        TeamId teamId,
        long telegramUserId,
        CancellationToken ct
    )
    {
        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = $"Captain {telegramUserId}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);

        db.Memberships.Add(
            new Membership
            {
                TeamId = teamId,
                PlayerId = player.Id,
                IsCaptain = true,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);
        return player;
    }

    private static async Task<Game> SeedGameAsync(QuizrDb db, long chatId, int capacity, CancellationToken ct)
    {
        var team = await SeedTeamAsync(db, chatId, ct);
        var creator = await SeedMemberAsync(db, team.Id, telegramUserId: chatId * 1000, ct);

        var game = new Game
        {
            TeamId = team.Id,
            Title = "Quiz Night",
            Venue = "The Pub",
            StartsAt = DateTimeOffset.UtcNow.AddDays(1),
            Capacity = capacity,
            AnnouncementMessageId = new TelegramMessageId(1),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = creator.Id,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);

        return game;
    }

    private static async Task<(Game Game, Participation Participation)> SeedFinishedGameWithParticipationAsync(
        QuizrDb db,
        long chatId,
        CancellationToken ct
    )
    {
        var team = await SeedTeamAsync(db, chatId, ct);
        var creator = await SeedMemberAsync(db, team.Id, telegramUserId: chatId * 1000, ct);

        var game = new Game
        {
            TeamId = team.Id,
            Title = "Quiz Night",
            Venue = "The Pub",
            StartsAt = DateTimeOffset.UtcNow.AddHours(-5),
            FinishedAt = DateTimeOffset.UtcNow,
            Capacity = 10,
            AnnouncementMessageId = new TelegramMessageId(1),
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-5),
            CreatedByPlayerId = creator.Id,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);

        var participation = new Participation
        {
            GameId = game.Id,
            PlayerId = creator.Id,
            Kind = ParticipationKind.Member,
            Played = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Participations.Add(participation);
        await db.SaveChangesAsync(ct);

        return (game, participation);
    }

    // A tap on a private message. Telegram reports Id as 0 and puts the real handle on
    // EphemeralMessageId — the shape every button in an ephemeral view actually arrives with,
    // and the one the ordinary builder below cannot represent.
    private static Update EphemeralCallbackUpdate(
        long chatId,
        long telegramUserId,
        string data,
        int ephemeralMessageId
    ) =>
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
                    Id = 0,
                    EphemeralMessageId = ephemeralMessageId,
                    Chat = new Chat { Id = chatId },
                    Date = DateTime.UtcNow,
                },
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

    private static Update MessageUpdate(long chatId, long telegramUserId, string text) =>
        new()
        {
            Id = 1,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = chatId },
                From = new User { Id = telegramUserId, FirstName = "Test" },
                Text = text,
                Date = DateTime.UtcNow,
            },
        };
}
