using AwesomeAssertions;
using Quizr.App.Localization;
using Quizr.App.Rendering;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Tests;

public class MyScheduleRendererTests
{
    private static readonly IStringsFor Strings = new Strings().For("en");

    [Test]
    public void SaysNothingIsUpcomingWhenThePersonIsSignedUpToNothing()
    {
        var text = MyScheduleRenderer.RenderText([], Strings);

        text.Should().Contain("not signed up to anything upcoming");
    }

    [Test]
    public void ShowsTheVenueAndThatYouArePlaying()
    {
        var entry = Entry(Team(1), Game("Quiz Night", venue: "The Pub"), playing: true);

        var text = MyScheduleRenderer.RenderText([entry], Strings);

        text.Should().Contain("The Pub");
        text.Should().Contain("✅ Playing");
    }

    [Test]
    public void ShowsThePositionInTheQueueWhenOnTheReserve()
    {
        var entry = Entry(Team(1), Game("Quiz Night"), playing: false, position: 3);

        var text = MyScheduleRenderer.RenderText([entry], Strings);

        text.Should().Contain("⏳ Reserve #3");
    }

    [Test]
    public void CountsTheGuestsYouBroughtToThatGame()
    {
        var entry = Entry(Team(1), Game("Quiz Night"), playing: true, guestCount: 2);

        var text = MyScheduleRenderer.RenderText([entry], Strings);

        text.Should().Contain("+2 guests");
    }

    [Test]
    public void NamesEachTeamOnceTheScheduleSpansMoreThanOne()
    {
        var berlin = Entry(Team(1, "Berlin Quizzers"), Game("Quiz Night"), playing: true);
        var moscow = Entry(Team(2, "Moscow Nerds"), Game("Trivia Evening"), playing: true);

        var text = MyScheduleRenderer.RenderText([berlin, moscow], Strings);

        text.Should().Contain("Berlin Quizzers");
        text.Should().Contain("Moscow Nerds");
    }

    // In a group there is only ever one team, and someone in two teams signed up in only one
    // of them has nothing to disambiguate either — the name would be noise on every line.
    [Test]
    public void LeavesTheTeamNameOffWhenEveryGameBelongsToTheSameTeam()
    {
        var team = Team(1, "Berlin Quizzers");
        var first = Entry(team, Game("Quiz Night"), playing: true);
        var second = Entry(team, Game("Trivia Evening"), playing: true);

        var text = MyScheduleRenderer.RenderText([first, second], Strings);

        text.Should().NotContain("Berlin Quizzers");
    }

    [Test]
    public void ListsGamesInTheOrderGiven()
    {
        var team = Team(1);
        var earlier = Entry(
            team,
            Game("Quiz #1", startsAt: new DateTimeOffset(2026, 3, 6, 18, 0, 0, TimeSpan.Zero)),
            true
        );
        var later = Entry(
            team,
            Game("Quiz #2", startsAt: new DateTimeOffset(2026, 3, 13, 18, 0, 0, TimeSpan.Zero)),
            true
        );

        var text = MyScheduleRenderer.RenderText([earlier, later], Strings);

        text.IndexOf("Quiz #1", StringComparison.Ordinal)
            .Should()
            .BeLessThan(text.IndexOf("Quiz #2", StringComparison.Ordinal));
    }

    [Test]
    public void LinksToTheAnnouncementInASupergroup()
    {
        var entry = Entry(
            Team(1, chatId: -1001234567890),
            Game("Quiz Night", announcementMessageId: 42),
            playing: true
        );

        var text = MyScheduleRenderer.RenderText([entry], Strings);

        text.Should().Contain("""<a href="https://t.me/c/1234567890/42">Quiz Night</a>""");
    }

    // The link is what ties a DM line back to the game it names, so its absence in a basic
    // group matters more here than on the Board — but a broken link would be worse.
    [Test]
    public void DoesNotLinkInABasicGroupSinceTelegramHasNoMessageLinkSchemeForThem()
    {
        var entry = Entry(Team(1, chatId: -1234567890), Game("Quiz Night", announcementMessageId: 42), playing: true);

        var text = MyScheduleRenderer.RenderText([entry], Strings);

        text.Should().NotContain("<a href");
        text.Should().Contain("Quiz Night");
    }

    [Test]
    public void EncodesATeamNameAndVenueThatCarryHtml()
    {
        var berlin = Entry(Team(1, "Ben & Jerry's"), Game("Quiz Night", venue: "<b>The Pub</b>"), playing: true);
        var other = Entry(Team(2, "Moscow Nerds"), Game("Trivia Evening"), playing: true);

        var text = MyScheduleRenderer.RenderText([berlin, other], Strings);

        text.Should().Contain("Ben &amp; Jerry&#39;s");
        text.Should().Contain("&lt;b&gt;The Pub&lt;/b&gt;");
    }

    [Test]
    public void PutsTheFranchiseBackInFrontOfARenamedGame()
    {
        var entry = Entry(Team(1), Game("Halloween special"), playing: true, franchiseName: "Kviz, pliz!");

        var text = MyScheduleRenderer.RenderText([entry], Strings);

        text.Should().Contain("Kviz, pliz! · Halloween special");
    }

    private static Team Team(long id, string name = "Test team", long chatId = -1001234567890) =>
        new()
        {
            Id = new TeamId(id),
            ChatId = new TelegramChatId(chatId),
            Name = name,
            TimeZoneId = "Europe/Berlin",
            Locale = "en",
        };

    private static Game Game(
        string title,
        string venue = "The Pub",
        DateTimeOffset? startsAt = null,
        long? announcementMessageId = null
    ) =>
        new()
        {
            TeamId = new TeamId(1),
            Title = title,
            Venue = venue,
            StartsAt = startsAt ?? new DateTimeOffset(2026, 3, 6, 18, 0, 0, TimeSpan.Zero),
            Capacity = 10,
            AnnouncementMessageId = announcementMessageId is { } id ? new TelegramMessageId(id) : null,
            CreatedByPlayerId = new PlayerId(1),
        };

    private static MyScheduleEntry Entry(
        Team team,
        Game game,
        bool playing,
        int position = 1,
        int guestCount = 0,
        string? franchiseName = null
    ) => new(game, team, franchiseName, new SignupPlacement(playing, position), guestCount);
}
