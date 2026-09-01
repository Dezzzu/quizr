using AwesomeAssertions;
using Quizr.App.Localization;
using Quizr.App.Rendering;
using Quizr.App.Telegram;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Tests;

public class AnnouncementRendererTests
{
    private static readonly GameId GameId = new(1);
    private static readonly IStringsFor Strings = new Strings().For("en");

    [Test]
    public void ListsPlayersInQueueOrderUnderThePlayingHeader()
    {
        var alice = Player(1, "Alice");
        var bob = Player(2, "Bob");
        var game = GameWithCapacity(2);
        var roster = new RosterSplit([Signup(1, alice), Signup(2, bob)], []);

        var text = AnnouncementRenderer.RenderText(game, roster, "Europe/Berlin", Strings);

        text.Should().Contain("Playing (2/2)");
        text.IndexOf("Alice", StringComparison.Ordinal)
            .Should()
            .BeLessThan(text.IndexOf("Bob", StringComparison.Ordinal));
    }

    [Test]
    public void TheStartTimeIsFormattedInTheTeamsLocalZone()
    {
        var game = GameWithCapacity(1);
        game.StartsAt = new DateTimeOffset(2026, 3, 6, 18, 5, 0, TimeSpan.Zero); // 19:05 in Berlin
        var roster = new RosterSplit([], []);

        var text = AnnouncementRenderer.RenderText(game, roster, "Europe/Berlin", Strings);

        text.Should().Contain("Fri, 06 Mar, 19:05");
    }

    [Test]
    public void ShowsTheReserveSectionOnlyWhenSomeoneIsWaiting()
    {
        var alice = Player(1, "Alice");
        var game = GameWithCapacity(1);
        var roster = new RosterSplit([Signup(1, alice)], []);

        var text = AnnouncementRenderer.RenderText(game, roster, "Europe/Berlin", Strings);

        text.Should().NotContain("Reserve");
    }

    [Test]
    public void HtmlEncodesUserSuppliedNames()
    {
        var alice = Player(1, "<b>Alice</b>");
        var game = GameWithCapacity(1);
        var roster = new RosterSplit([Signup(1, alice)], []);

        var text = AnnouncementRenderer.RenderText(game, roster, "Europe/Berlin", Strings);

        text.Should().NotContain("<b>Alice</b>");
        text.Should().Contain("&lt;b&gt;Alice&lt;/b&gt;");
    }

    [Test]
    public void AnAnonymousGuestIsCreditedToTheirInviter()
    {
        var alice = Player(1, "Alice");
        var game = GameWithCapacity(1);
        var guest = new Signup
        {
            Id = new SignupId(1),
            GameId = GameId,
            InvitedByPlayerId = alice.Id,
            InvitedByPlayer = alice,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var roster = new RosterSplit([guest], []);

        var text = AnnouncementRenderer.RenderText(game, roster, "Europe/Berlin", Strings);

        text.Should().Contain("<a href=\"tg://user?id=1\">Alice</a>'s guest");
    }

    [Test]
    public void ANamedGuestShowsBothTheirNameAndTheirInviter()
    {
        var alice = Player(1, "Alice");
        var game = GameWithCapacity(1);
        var guest = new Signup
        {
            Id = new SignupId(1),
            GameId = GameId,
            InvitedByPlayerId = alice.Id,
            InvitedByPlayer = alice,
            GuestName = "Sasha",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var roster = new RosterSplit([guest], []);

        var text = AnnouncementRenderer.RenderText(game, roster, "Europe/Berlin", Strings);

        text.Should().Contain("Sasha").And.Contain("guest of <a href=\"tg://user?id=1\">Alice</a>");
    }

    [Test]
    public void ATeamGuestHasNoInviterMentioned()
    {
        var game = GameWithCapacity(1);
        var guest = new Signup
        {
            Id = new SignupId(1),
            GameId = GameId,
            GuestName = "Sasha",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var roster = new RosterSplit([guest], []);

        var text = AnnouncementRenderer.RenderText(game, roster, "Europe/Berlin", Strings);

        text.Should().Contain("Sasha").And.Contain("team guest");
    }

    // Members are rendered as tg://user?id= mentions so that two people sharing a display name
    // are still tellable apart by tapping through — the id is the stable identity, the name is
    // only the label.
    [Test]
    public void MembersAreRenderedAsProfileLinksKeyedOnTheirTelegramId()
    {
        var roster = new RosterSplit([Signup(1, Player(7, "Alice"))], []);

        var text = AnnouncementRenderer.RenderText(GameWithCapacity(2), roster, "Europe/Berlin", Strings);

        text.Should().Contain("<a href=\"tg://user?id=7\">Alice</a>");
    }

    // Two members with the same display name stay individually addressable: each link points
    // at its own profile, which is the whole reason the id is what's rendered.
    [Test]
    public void TwoMembersSharingADisplayNameGetSeparateProfileLinks()
    {
        var roster = new RosterSplit([Signup(1, Player(11, "Anna")), Signup(2, Player(22, "Anna"))], []);

        var text = AnnouncementRenderer.RenderText(GameWithCapacity(2), roster, "Europe/Berlin", Strings);

        text.Should().Contain("<a href=\"tg://user?id=11\">Anna</a>");
        text.Should().Contain("<a href=\"tg://user?id=22\">Anna</a>");
    }

    // The display name is a label inside markup now, so it still has to be encoded — an
    // unescaped '<' would break the whole message's HTML parse, not just the one name.
    [Test]
    public void ADisplayNameContainingMarkupIsStillEncodedInsideTheLink()
    {
        var roster = new RosterSplit([Signup(1, Player(9, "<b>Mallory</b>"))], []);

        var text = AnnouncementRenderer.RenderText(GameWithCapacity(2), roster, "Europe/Berlin", Strings);

        text.Should().Contain("<a href=\"tg://user?id=9\">&lt;b&gt;Mallory&lt;/b&gt;</a>");
    }

    // Telegram shows one keyboard to everyone, so the captain-only actions sit behind a single
    // door rather than as five buttons most of the team can only be refused by.
    [Test]
    public void TheAnnouncementCarriesOnlySelfServeButtonsAndOneManageDoor()
    {
        var keyboard = AnnouncementRenderer.RenderKeyboard(GameWithCapacity(5), Strings);

        var verbs = keyboard.InlineKeyboard.SelectMany(row => row).Select(b => b.CallbackData![0]).ToList();

        verbs
            .Should()
            .Equal(
                CallbackData.Join,
                CallbackData.Drop,
                CallbackData.Guest,
                CallbackData.MyGuests,
                CallbackData.Nudge,
                CallbackData.Manage
            );
    }

    // Nudge is everyone's: whoever is waiting on a late player can chase them, and the
    // cooldown in GameService is what keeps that from becoming a bludgeon.
    [Test]
    public void NoCaptainOnlyActionAppearsOnTheAnnouncementItself()
    {
        var keyboard = AnnouncementRenderer.RenderKeyboard(GameWithCapacity(5), Strings);

        var verbs = keyboard.InlineKeyboard.SelectMany(row => row).Select(b => b.CallbackData![0]).ToList();

        verbs
            .Should()
            .NotContain([
                CallbackData.ManagePlayers,
                CallbackData.ManageGuests,
                CallbackData.FinishGame,
                CallbackData.DeclineGame,
                CallbackData.ManageRoster,
            ]);
    }

    [Test]
    public void TheManagePanelHoldsTheCaptainActionsForALiveGame()
    {
        var keyboard = AnnouncementRenderer.RenderManagePanel(GameWithCapacity(5), Strings);

        var verbs = keyboard.InlineKeyboard.SelectMany(row => row).Select(b => b.CallbackData![0]).ToList();

        verbs
            .Should()
            .Contain([
                CallbackData.ManagePlayers,
                CallbackData.ManageGuests,
                CallbackData.FinishGame,
                CallbackData.DeclineGame,
            ]);
    }

    // The headline and the Board entry name a game the same way, or the pinned list and the
    // post it links to disagree about what the game is called.
    [Test]
    public void PrefixesARenamedFranchiseGameWithItsFranchiseNameInTheHeadline()
    {
        var game = GameWithCapacity(10);
        game.Title = "Halloween special";

        var text = AnnouncementRenderer.RenderText(
            game,
            Roster.Split([], game.Capacity),
            "Europe/Berlin",
            Strings,
            franchiseName: "Квиз, плиз!"
        );

        text.Should().StartWith("<b>Квиз, плиз! · Halloween special</b>");
    }

    [Test]
    public void DoesNotRepeatAFranchiseNameTheHeadlineAlreadyCarries()
    {
        var game = GameWithCapacity(10);
        game.Title = "Квиз, плиз! #12";

        var text = AnnouncementRenderer.RenderText(
            game,
            Roster.Split([], game.Capacity),
            "Europe/Berlin",
            Strings,
            franchiseName: "Квиз, плиз!"
        );

        text.Should().StartWith("<b>Квиз, плиз! #12</b>");
    }

    // A one-off has no franchise at all, which is why the parameter defaults.
    [Test]
    public void LeavesAOneOffHeadlineAlone()
    {
        var game = GameWithCapacity(10);
        game.Title = "Pub trivia";

        var text = AnnouncementRenderer.RenderText(game, Roster.Split([], game.Capacity), "Europe/Berlin", Strings);

        text.Should().StartWith("<b>Pub trivia</b>");
    }

    // Reaching /editgame used to mean leaving the game you were already looking at, running the
    // command, and picking that same game back out of a list.
    [Test]
    public void TheManagePanelLeadsStraightIntoEditingItsOwnGame()
    {
        var game = GameWithCapacity(5);

        var button = AnnouncementRenderer
            .RenderManagePanel(game, Strings)
            .InlineKeyboard.SelectMany(row => row)
            .Single(b => b.CallbackData![0] == CallbackData.PickGameToEdit);

        button.CallbackData.Should().Be(CallbackData.Format(CallbackData.PickGameToEdit, game.Id));
    }

    // A finished game has only its roster left to edit (invariant 11), so that's all the door
    // opens onto — and the announcement still shows the same neutral Manage button.
    [Test]
    public void AFinishedGameOffersOnlyTheRosterBehindTheSameDoor()
    {
        var game = GameWithCapacity(5);
        game.FinishedAt = DateTimeOffset.UtcNow;

        var announcement = AnnouncementRenderer.RenderKeyboard(game, Strings);
        var panel = AnnouncementRenderer.RenderManagePanel(game, Strings);

        announcement
            .InlineKeyboard.SelectMany(row => row)
            .Select(b => b.CallbackData![0])
            .Should()
            .Equal(CallbackData.Manage);
        panel
            .InlineKeyboard.SelectMany(row => row)
            .Select(b => b.CallbackData![0])
            .Should()
            .Contain(CallbackData.ManageRoster)
            .And.NotContain(CallbackData.PickGameToEdit);
    }

    private static Player Player(long id, string displayName) =>
        new()
        {
            Id = new PlayerId(id),
            TelegramUserId = new TelegramUserId(id),
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static Signup Signup(long id, Player player) =>
        new()
        {
            Id = new SignupId(id),
            GameId = GameId,
            PlayerId = player.Id,
            Player = player,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(id),
        };

    private static Game GameWithCapacity(int capacity) =>
        new()
        {
            Id = GameId,
            TeamId = new TeamId(1),
            Title = "Quiz Night",
            Venue = "The Pub",
            StartsAt = DateTimeOffset.UtcNow.AddDays(1),
            Capacity = capacity,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = new PlayerId(1),
        };
}
