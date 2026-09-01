using AwesomeAssertions;
using Quizr.App.Localization;
using Quizr.App.Rendering;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Tests;

public class BoardRendererTests
{
    private static readonly TelegramChatId SupergroupChatId = new(-1001234567890);
    private static readonly TelegramChatId BasicGroupChatId = new(-1234567890);
    private static readonly IStringsFor Strings = new Strings().For("en");

    [Test]
    public void ShowsANoGamesMessageWhenNothingIsUpcoming()
    {
        var text = BoardRenderer.RenderText([], SupergroupChatId, "Europe/Berlin", Strings);

        text.Should().Contain("No upcoming games yet");
    }

    [Test]
    public void ListsGamesInTheOrderGiven()
    {
        var earlier = Game(1, "Квиз, плиз! #1", new DateTimeOffset(2026, 3, 6, 18, 0, 0, TimeSpan.Zero));
        var later = Game(2, "Квиз, плиз! #2", new DateTimeOffset(2026, 3, 13, 18, 0, 0, TimeSpan.Zero));

        var text = BoardRenderer.RenderText([Entry(earlier), Entry(later)], SupergroupChatId, "Europe/Berlin", Strings);

        text.IndexOf("#1", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("#2", StringComparison.Ordinal));
    }

    [Test]
    public void LinksToTheAnnouncementInASupergroup()
    {
        var game = Game(1, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1), announcementMessageId: 42);

        var text = BoardRenderer.RenderText([Entry(game)], SupergroupChatId, "Europe/Berlin", Strings);

        text.Should().Contain("""<a href="https://t.me/c/1234567890/42">Quiz Night</a>""");
    }

    [Test]
    public void DoesNotLinkInABasicGroupSinceTelegramHasNoMessageLinkSchemeForThem()
    {
        var game = Game(1, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1), announcementMessageId: 42);

        var text = BoardRenderer.RenderText([Entry(game)], BasicGroupChatId, "Europe/Berlin", Strings);

        text.Should().NotContain("<a href");
        text.Should().Contain("Quiz Night");
    }

    [Test]
    public void DoesNotLinkWhenTheGameHasNoAnnouncementYet()
    {
        var game = Game(1, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1));

        var text = BoardRenderer.RenderText([Entry(game)], SupergroupChatId, "Europe/Berlin", Strings);

        text.Should().NotContain("<a href");
        text.Should().Contain("Quiz Night");
    }

    // The day of the month is zero-padded so that stacked Board entries line up — a list is the
    // one place the width of a date matters, and "6 Mar" against "13 Mar" ragged the column.
    [Test]
    public void PadsASingleDigitDayOfTheMonth()
    {
        var game = Game(1, "Quiz Night", new DateTimeOffset(2026, 3, 6, 18, 0, 0, TimeSpan.Zero));

        var text = BoardRenderer.RenderText([Entry(game)], SupergroupChatId, "Europe/Berlin", Strings);

        text.Should().Contain("Fri, 06 Mar, 19:00");
    }

    // How full each game is, so the pinned Board answers "is there still a seat" without
    // opening every announcement.
    [Test]
    public void ShowsSeatsTakenAgainstCapacity()
    {
        var game = Game(1, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1));

        var text = BoardRenderer.RenderText([Entry(game, playing: 3)], SupergroupChatId, "Europe/Berlin", Strings);

        text.Should().Contain("(3/10)");
    }

    // Over-subscription is worth seeing from the pinned Board: "8/8" alone reads the same as a
    // game that just filled, when in fact two more people are already queued behind it.
    [Test]
    public void ShowsTheReserveCountWhenSomeoneIsQueuedBehindTheSeats()
    {
        var game = Game(1, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1));

        var text = BoardRenderer.RenderText(
            [Entry(game, playing: 10, reserve: 2)],
            SupergroupChatId,
            "Europe/Berlin",
            Strings
        );

        text.Should().Contain("(10/10 +2)");
    }

    // A game with seats to spare says nothing about a reserve rather than "+0".
    [Test]
    public void OmitsTheReserveCountWhenNobodyIsWaiting()
    {
        var game = Game(1, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1));

        var text = BoardRenderer.RenderText([Entry(game, playing: 3)], SupergroupChatId, "Europe/Berlin", Strings);

        text.Should().Contain("(3/10)").And.NotContain("+");
    }

    // A captain who renames a franchise game usually drops the brand out of the title, which
    // reads fine on the announcement and badly in a list mixing three franchises.
    [Test]
    public void PrefixesARenamedFranchiseGameWithItsFranchiseName()
    {
        var game = Game(1, "Halloween special", DateTimeOffset.UtcNow.AddDays(1));

        var text = BoardRenderer.RenderText(
            [Entry(game, franchiseName: "Квиз, плиз!")],
            SupergroupChatId,
            "Europe/Berlin",
            Strings
        );

        text.Should().Contain("Квиз, плиз! · Halloween special");
    }

    // The derived title a franchise game is created with already leads with the brand.
    [Test]
    public void DoesNotRepeatAFranchiseNameTheTitleAlreadyCarries()
    {
        var game = Game(1, "Квиз, плиз! #12", DateTimeOffset.UtcNow.AddDays(1));

        var text = BoardRenderer.RenderText(
            [Entry(game, franchiseName: "Квиз, плиз!")],
            SupergroupChatId,
            "Europe/Berlin",
            Strings
        );

        text.Should().Contain("Квиз, плиз! #12").And.NotContain("·");
    }

    // Contains, not StartsWith: a title that mentions the brand anywhere already answers the
    // question the prefix exists to answer.
    [Test]
    public void DoesNotPrefixWhenTheFranchiseNameAppearsLaterInTheTitle()
    {
        var game = Game(1, "Осенний Квиз, плиз!", DateTimeOffset.UtcNow.AddDays(1));

        var text = BoardRenderer.RenderText(
            [Entry(game, franchiseName: "Квиз, плиз!")],
            SupergroupChatId,
            "Europe/Berlin",
            Strings
        );

        text.Should().NotContain("·");
    }

    [Test]
    public void LeavesAOneOffGameUnprefixed()
    {
        var game = Game(1, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1));

        var text = BoardRenderer.RenderText([Entry(game)], SupergroupChatId, "Europe/Berlin", Strings);

        text.Should().Contain("Quiz Night").And.NotContain("·");
    }

    // The whole label is the link, not just the half of it that was already there.
    [Test]
    public void LinksThePrefixedTitleAsOnePiece()
    {
        var game = Game(1, "Halloween special", DateTimeOffset.UtcNow.AddDays(1), announcementMessageId: 42);

        var text = BoardRenderer.RenderText(
            [Entry(game, franchiseName: "Kviz")],
            SupergroupChatId,
            "Europe/Berlin",
            Strings
        );

        text.Should().Contain("""<a href="https://t.me/c/1234567890/42">Kviz · Halloween special</a>""");
    }

    [Test]
    public void HtmlEncodesTheFranchiseNameItPrefixes()
    {
        var game = Game(1, "Halloween special", DateTimeOffset.UtcNow.AddDays(1));

        var text = BoardRenderer.RenderText(
            [Entry(game, franchiseName: "<b>Kviz</b>")],
            SupergroupChatId,
            "Europe/Berlin",
            Strings
        );

        text.Should().NotContain("<b>Kviz</b>");
        text.Should().Contain("&lt;b&gt;Kviz&lt;/b&gt;");
    }

    [Test]
    public void HtmlEncodesTheTitle()
    {
        var game = Game(1, "<b>Quiz</b>", DateTimeOffset.UtcNow.AddDays(1));

        var text = BoardRenderer.RenderText([Entry(game)], SupergroupChatId, "Europe/Berlin", Strings);

        text.Should().NotContain("<b>Quiz</b>");
        text.Should().Contain("&lt;b&gt;Quiz&lt;/b&gt;");
    }

    private static BoardEntry Entry(Game game, int playing = 0, int reserve = 0, string? franchiseName = null) =>
        new(game, playing, reserve, franchiseName);

    private static Game Game(long id, string title, DateTimeOffset startsAt, int? announcementMessageId = null) =>
        new()
        {
            Id = new GameId(id),
            TeamId = new TeamId(1),
            Title = title,
            Venue = "The Pub",
            StartsAt = startsAt,
            Capacity = 10,
            AnnouncementMessageId = announcementMessageId is { } id2 ? new TelegramMessageId(id2) : null,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = new PlayerId(1),
        };
}
