using AwesomeAssertions;
using Quizr.App.Localization;
using Quizr.App.Rendering;
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

        var text = BoardRenderer.RenderText([earlier, later], SupergroupChatId, "Europe/Berlin", Strings);

        text.IndexOf("#1", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("#2", StringComparison.Ordinal));
    }

    [Test]
    public void LinksToTheAnnouncementInASupergroup()
    {
        var game = Game(1, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1), announcementMessageId: 42);

        var text = BoardRenderer.RenderText([game], SupergroupChatId, "Europe/Berlin", Strings);

        text.Should().Contain("""<a href="https://t.me/c/1234567890/42">Quiz Night</a>""");
    }

    [Test]
    public void DoesNotLinkInABasicGroupSinceTelegramHasNoMessageLinkSchemeForThem()
    {
        var game = Game(1, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1), announcementMessageId: 42);

        var text = BoardRenderer.RenderText([game], BasicGroupChatId, "Europe/Berlin", Strings);

        text.Should().NotContain("<a href");
        text.Should().Contain("Quiz Night");
    }

    [Test]
    public void DoesNotLinkWhenTheGameHasNoAnnouncementYet()
    {
        var game = Game(1, "Quiz Night", DateTimeOffset.UtcNow.AddDays(1));

        var text = BoardRenderer.RenderText([game], SupergroupChatId, "Europe/Berlin", Strings);

        text.Should().NotContain("<a href");
        text.Should().Contain("Quiz Night");
    }

    [Test]
    public void HtmlEncodesTheTitle()
    {
        var game = Game(1, "<b>Quiz</b>", DateTimeOffset.UtcNow.AddDays(1));

        var text = BoardRenderer.RenderText([game], SupergroupChatId, "Europe/Berlin", Strings);

        text.Should().NotContain("<b>Quiz</b>");
        text.Should().Contain("&lt;b&gt;Quiz&lt;/b&gt;");
    }

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
