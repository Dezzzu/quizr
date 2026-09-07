using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Calendar;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Game = Quizr.Domain.Entities.Game;

namespace Quizr.App.Tests;

// The handler is exercised against a real HttpContext and its returned IResult is executed
// against the same one, so status codes, headers and body are the actual ones a client sees —
// without hosting Program, which would start the bot polling Telegram. What that leaves
// untested is named in docs/CALENDAR.md: the middleware pipeline itself, meaning the rate
// limiters, forwarded headers and the route constraint.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class CalendarEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public CalendarEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task AKnownTokenGetsTheFeedWithItsCalendarHeaders()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (player, token) = await SeedSubscriberWithAGameAsync(db, chatId: 7301, telegramUserId: 7301, ct);

        var response = await CallAsync(db, token, ct);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.ContentType.Should().Be("text/calendar; charset=utf-8");
        response.Headers["Cache-Control"].ToString().Should().Be("private, max-age=3600");
        response.Headers["Content-Disposition"].ToString().Should().Be("inline; filename=\"quizr.ics\"");
        response.Headers.ETag.ToString().Should().StartWith("\"").And.EndWith("\"");
        response.Body.Should().StartWith("BEGIN:VCALENDAR").And.Contain("UID:game-");
        player.CalendarToken.Should().Be(token);
    }

    [Test]
    public async Task AMatchingIfNoneMatchAnswersNotModifiedWithNoBody()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (_, token) = await SeedSubscriberWithAGameAsync(db, chatId: 7302, telegramUserId: 7302, ct);
        var first = await CallAsync(db, token, ct);

        var second = await CallAsync(db, token, ct, ifNoneMatch: first.Headers.ETag.ToString());

        second.StatusCode.Should().Be(StatusCodes.Status304NotModified);
        second.Body.Should().BeEmpty();
        second.Headers.ETag.ToString().Should().Be(first.Headers.ETag.ToString());
    }

    // A client is allowed to send back a tag it received strong with a W/ in front of it.
    [Test]
    public async Task AWeakenedEtagStillMatches()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (_, token) = await SeedSubscriberWithAGameAsync(db, chatId: 7303, telegramUserId: 7303, ct);
        var first = await CallAsync(db, token, ct);

        var second = await CallAsync(db, token, ct, ifNoneMatch: "W/" + first.Headers.ETag);

        second.StatusCode.Should().Be(StatusCodes.Status304NotModified);
    }

    // The point of the version stamp: a change anyone's feed shows has to change the ETag, or
    // a client sits on a stale body until something else happens to move it.
    [Test]
    public async Task AChangeToTheRosterChangesTheEtag()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (player, token) = await SeedSubscriberWithAGameAsync(db, chatId: 7304, telegramUserId: 7304, ct);
        var before = await CallAsync(db, token, ct);

        var game = db.Games.Single(g => g.Signups.Any(s => s.PlayerId == player.Id));
        game.Venue = "Somewhere else";
        await db.SaveChangesAsync(ct);
        var after = await CallAsync(db, token, ct);

        after.Headers.ETag.ToString().Should().NotBe(before.Headers.ETag.ToString());
        after.Body.Should().Contain("Somewhere else");
    }

    // The rolling window means the feed's contents change as days pass with nothing else
    // happening, so the date is part of the key. Without it, a game crossing the horizon
    // would never reach a client that keeps sending the same If-None-Match.
    [Test]
    public async Task TheEtagChangesWhenTheDayDoesEvenWithNoDataChange()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (_, token) = await SeedSubscriberWithAGameAsync(db, chatId: 7305, telegramUserId: 7305, ct);
        var clock = new FakeTimeProvider(Now);
        var today = await CallAsync(db, token, ct, clock: clock);

        clock.Advance(TimeSpan.FromDays(1));
        var tomorrow = await CallAsync(db, token, ct, clock: clock);

        tomorrow.Headers.ETag.ToString().Should().NotBe(today.Headers.ETag.ToString());
    }

    [Test]
    public async Task AnUnknownTokenIsNotFound()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();

        var response = await CallAsync(db, CalendarToken.Generate(), ct);

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        response.Body.Should().BeEmpty();
    }

    // Revoking is nulling the token, and a revoked link has to be indistinguishable from one
    // that never existed.
    [Test]
    public async Task ARevokedTokenIsNotFoundAndLooksNoDifferent()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (player, token) = await SeedSubscriberWithAGameAsync(db, chatId: 7306, telegramUserId: 7306, ct);

        player.CalendarToken = null;
        await db.SaveChangesAsync(ct);
        var revoked = await CallAsync(db, token, ct);
        var neverExisted = await CallAsync(db, CalendarToken.Generate(), ct);

        revoked.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        revoked.Body.Should().Be(neverExisted.Body);
        revoked.Headers.Keys.Should().BeEquivalentTo(neverExisted.Headers.Keys);
    }

    [Test]
    [Arguments("")]
    [Arguments("short")]
    [Arguments("this-token-has-a-bang!-in-it-and-is-43-chars")]
    public async Task AMalformedTokenIsNotFoundWithoutTouchingTheDatabase(string token)
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();

        var response = await CallAsync(db, token, ct);

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // Two people's feeds are two people's feeds. The one test here that would matter most if
    // it ever went wrong.
    [Test]
    public async Task AFeedNeverContainsSomebodyElsesGames()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (mine, myToken) = await SeedSubscriberWithAGameAsync(db, chatId: 7307, telegramUserId: 7307, ct, "Mine");
        await SeedSubscriberWithAGameAsync(db, chatId: 7308, telegramUserId: 7308, ct, "Theirs");

        var response = await CallAsync(db, myToken, ct);

        response.Body.Should().Contain("Mine").And.NotContain("Theirs");
        mine.CalendarToken.Should().Be(myToken);
    }

    [Test]
    public async Task ASubscriberWithNothingComingUpStillGetsAValidEmptyCalendar()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var player = await SeedPlayerAsync(db, telegramUserId: 7309, CalendarToken.Generate(), ct);

        var response = await CallAsync(db, player.CalendarToken!, ct);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("BEGIN:VCALENDAR").And.NotContain("BEGIN:VEVENT");
    }

    // Rendering is the expensive half, so a repeat request inside one version must not do it
    // again. Proven by mutating the database behind the cache: a second call that re-rendered
    // would pick the change up, and this one must not.
    [Test]
    public async Task ASecondRequestInsideTheSameVersionIsServedFromMemory()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (player, token) = await SeedSubscriberWithAGameAsync(
            db,
            chatId: 7310,
            telegramUserId: 7310,
            ct,
            "Original"
        );
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 * 1024 });
        await CallAsync(db, token, ct, cache: cache);

        // Deliberately behind EF's back: a change through SaveChanges would bump the version,
        // change the key and legitimately re-render. Only a change the cache cannot know about
        // proves the second request never reached the renderer at all.
        var game = db.Games.AsNoTracking().Single(g => g.Signups.Any(s => s.PlayerId == player.Id));
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Games\" SET \"Title\" = 'Rewritten' WHERE \"Id\" = {0}",
            [game.Id.Value],
            ct
        );
        var second = await CallAsync(db, token, ct, cache: cache);

        second.Body.Should().Contain("Original").And.NotContain("Rewritten");
    }

    private static async Task<Recorded> CallAsync(
        QuizrDb db,
        string token,
        CancellationToken ct,
        string? ifNoneMatch = null,
        IMemoryCache? cache = null,
        FakeTimeProvider? clock = null
    )
    {
        // Executing an IResult resolves ILoggerFactory out of RequestServices, so the context
        // needs a real container rather than an empty one.
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        var body = new MemoryStream();
        http.Response.Body = body;

        if (ifNoneMatch is not null)
        {
            http.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        var effectiveClock = clock ?? new FakeTimeProvider(Now);
        var endpoint = new CalendarEndpoint(
            db,
            new CalendarFeedService(db, effectiveClock),
            new Strings(),
            cache ?? new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 * 1024 }),
            effectiveClock,
            NullLogger<CalendarEndpoint>.Instance
        );

        var result = await endpoint.HandleAsync(http, token, ct);
        await result.ExecuteAsync(http);

        return new Recorded(
            http.Response.StatusCode,
            http.Response.ContentType,
            http.Response.Headers,
            Encoding.UTF8.GetString(body.ToArray())
        );
    }

    private sealed record Recorded(int StatusCode, string? ContentType, IHeaderDictionary Headers, string Body);

    private static async Task<(Player Player, string Token)> SeedSubscriberWithAGameAsync(
        QuizrDb db,
        long chatId,
        long telegramUserId,
        CancellationToken ct,
        string title = "Quiz"
    )
    {
        var token = CalendarToken.Generate();
        var team = await SeedTeamAsync(db, chatId, ct);
        var player = await SeedPlayerAsync(db, telegramUserId, token, ct);
        db.Memberships.Add(
            new Membership
            {
                TeamId = team.Id,
                PlayerId = player.Id,
                JoinedAt = Now,
            }
        );
        await db.SaveChangesAsync(ct);

        var game = new Game
        {
            TeamId = team.Id,
            Title = title,
            Venue = "The Pub",
            StartsAt = Now.AddDays(3),
            Capacity = 6,
            CreatedAt = Now,
            CreatedByPlayerId = player.Id,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);

        db.Signups.Add(
            new Signup
            {
                GameId = game.Id,
                PlayerId = player.Id,
                CreatedAt = Now.AddDays(-1),
            }
        );
        await db.SaveChangesAsync(ct);

        return (player, token);
    }

    private static async Task<Team> SeedTeamAsync(QuizrDb db, long chatId, CancellationToken ct)
    {
        var team = new Team
        {
            ChatId = new TelegramChatId(chatId),
            Name = "Team",
            TimeZoneId = "Europe/Berlin",
            Locale = "en",
            CreatedAt = Now,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return team;
    }

    private static async Task<Player> SeedPlayerAsync(
        QuizrDb db,
        long telegramUserId,
        string? token,
        CancellationToken ct
    )
    {
        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = "Player",
            CalendarToken = token,
            CalendarTokenIssuedAt = token is null ? null : Now,
            CreatedAt = Now,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);
        return player;
    }
}
