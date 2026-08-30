using AwesomeAssertions;
using Quizr.App.Localization;
using Quizr.App.Rendering;
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

        text.Should().Contain("Fri, 6 Mar, 19:05");
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

        text.Should().Contain("Alice's guest");
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

        text.Should().Contain("Sasha").And.Contain("guest of Alice");
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
