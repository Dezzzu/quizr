using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Calendar;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Game = Quizr.Domain.Entities.Game;

namespace Quizr.App.Tests;

// What reaches a person's feed, and — more of the point — what doesn't. Each test seeds its
// own team, games and people: the fixture is shared and TUnit runs a class's tests in parallel.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class CalendarFeedServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public CalendarFeedServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task OnlyTheGamesThisPersonIsSignedUpToAppear()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7201, ct);
        var player = await SeedMemberAsync(db, team, telegramUserId: 7201, ct);
        var mine = await SeedGameAsync(db, team, "Mine", Now.AddDays(3), ct);
        await SeedGameAsync(db, team, "Somebody else's", Now.AddDays(4), ct);
        await SeedSignupAsync(db, mine, player, ct);

        var feed = await LoadAsync(db, player, ct);

        feed.Entries.Should().ContainSingle().Which.Title.Should().Be("Mine");
    }

    // The window's near edge, from both sides. Whole days, so a game 30 days back is still in.
    [Test]
    public async Task AGameJustInsideThePastWindowIsKeptAndOneJustOutsideIsDropped()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7202, ct);
        var player = await SeedMemberAsync(db, team, telegramUserId: 7202, ct);
        var justInside = await SeedFinishedGameAsync(db, team, player, "29 days ago", Now.AddDays(-29), true, ct);
        await SeedFinishedGameAsync(db, team, player, "31 days ago", Now.AddDays(-31), true, ct);

        var feed = await LoadAsync(db, player, ct);

        feed.Entries.Should().ContainSingle().Which.GameId.Should().Be(justInside.Id);
    }

    [Test]
    public async Task AGameBeyondTheTwelveMonthHorizonIsDropped()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7203, ct);
        var player = await SeedMemberAsync(db, team, telegramUserId: 7203, ct);
        var soon = await SeedGameAsync(db, team, "Eleven months out", Now.AddDays(330), ct);
        var tooFar = await SeedGameAsync(db, team, "Thirteen months out", Now.AddDays(400), ct);
        await SeedSignupAsync(db, soon, player, ct);
        await SeedSignupAsync(db, tooFar, player, ct);

        var feed = await LoadAsync(db, player, ct);

        feed.Entries.Should().ContainSingle().Which.Title.Should().Be("Eleven months out");
    }

    // Invariant 3: dropping out cancels the signup, so the evening leaves the calendar. There
    // is no tombstone — omitting it from the next response is how a subscription feed removes
    // anything.
    [Test]
    public async Task AGameTheyDroppedOutOfDisappears()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7204, ct);
        var player = await SeedMemberAsync(db, team, telegramUserId: 7204, ct);
        var game = await SeedGameAsync(db, team, "Dropped", Now.AddDays(2), ct);
        var signup = await SeedSignupAsync(db, game, player, ct);

        signup.CancelledAt = Now;
        await db.SaveChangesAsync(ct);
        var feed = await LoadAsync(db, player, ct);

        feed.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task ADeclinedGameNeverAppears()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7205, ct);
        var player = await SeedMemberAsync(db, team, telegramUserId: 7205, ct);
        var game = await SeedGameAsync(db, team, "Declined", Now.AddDays(2), ct);
        await SeedSignupAsync(db, game, player, ct);

        game.DeclinedAt = Now;
        await db.SaveChangesAsync(ct);
        var feed = await LoadAsync(db, player, ct);

        feed.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task SomeoneBeyondCapacityIsMarkedWithTheirReservePosition()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7206, ct);
        var game = await SeedGameAsync(db, team, "Full", Now.AddDays(2), ct, capacity: 1);
        var playing = await SeedMemberAsync(db, team, telegramUserId: 7206, ct);
        var first = await SeedMemberAsync(db, team, telegramUserId: 7207, ct);
        var second = await SeedMemberAsync(db, team, telegramUserId: 7208, ct);
        await SeedSignupAsync(db, game, playing, ct, Now.AddDays(-3));
        await SeedSignupAsync(db, game, first, ct, Now.AddDays(-2));
        await SeedSignupAsync(db, game, second, ct, Now.AddDays(-1));

        var playingFeed = await LoadAsync(db, playing, ct);
        var secondFeed = await LoadAsync(db, second, ct);

        playingFeed.Entries.Single().Status.Should().Be(CalendarEntryStatus.Playing);
        secondFeed.Entries.Single().Status.Should().Be(CalendarEntryStatus.Reserve);
        secondFeed.Entries.Single().ReservePosition.Should().Be(2);
    }

    // Invariant 10: once a game finishes, what happened is what the participation rows say
    // happened — including a captain's later correction — never what the signups said.
    [Test]
    public async Task AFinishedGameIsReadFromItsParticipationAndNotItsSignups()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7207_0, ct);
        var player = await SeedMemberAsync(db, team, telegramUserId: 7209, ct);
        var played = await SeedFinishedGameAsync(db, team, player, "Played", Now.AddDays(-5), true, ct);
        var missed = await SeedFinishedGameAsync(db, team, player, "Missed", Now.AddDays(-4), false, ct);

        var feed = await LoadAsync(db, player, ct);

        feed.Entries.Should().HaveCount(2);
        feed.Entries.Single(e => e.GameId == played.Id).Status.Should().Be(CalendarEntryStatus.Played);
        feed.Entries.Single(e => e.GameId == missed.Id).Status.Should().Be(CalendarEntryStatus.DidNotPlay);
    }

    // A finished game whose participation names someone else is not that person's business,
    // even though they were on the roster before it finished.
    [Test]
    public async Task AFinishedGameWithNoParticipationForThisPersonIsLeftOut()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7211, ct);
        var player = await SeedMemberAsync(db, team, telegramUserId: 7211, ct);
        var other = await SeedMemberAsync(db, team, telegramUserId: 7212, ct);
        var game = await SeedFinishedGameAsync(db, team, other, "Theirs", Now.AddDays(-2), true, ct);
        await SeedSignupAsync(db, game, player, ct);

        var feed = await LoadAsync(db, player, ct);

        feed.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task GuestsBroughtByThisPersonAreCountedAndOtherPeoplesAreNot()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7213, ct);
        var player = await SeedMemberAsync(db, team, telegramUserId: 7213, ct);
        var other = await SeedMemberAsync(db, team, telegramUserId: 7214, ct);
        var game = await SeedGameAsync(db, team, "With guests", Now.AddDays(2), ct);
        await SeedSignupAsync(db, game, player, ct);
        await SeedGuestAsync(db, game, player, ct);
        await SeedGuestAsync(db, game, player, ct);
        await SeedGuestAsync(db, game, other, ct);

        var feed = await LoadAsync(db, player, ct);

        feed.Entries.Single().GuestCount.Should().Be(2);
    }

    // A person in two teams has one calendar, the same way they have one Friday evening.
    [Test]
    public async Task TwoTeamsMergeIntoOneDateOrderedFeedWithNoSharedName()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var one = await SeedTeamAsync(db, chatId: 7215, ct, name: "Team One");
        var two = await SeedTeamAsync(db, chatId: 7216, ct, name: "Team Two");
        var player = await SeedMemberAsync(db, one, telegramUserId: 7215, ct);
        await SeedMembershipAsync(db, two, player, ct);
        var later = await SeedGameAsync(db, one, "Later", Now.AddDays(9), ct);
        var sooner = await SeedGameAsync(db, two, "Sooner", Now.AddDays(2), ct);
        await SeedSignupAsync(db, later, player, ct);
        await SeedSignupAsync(db, sooner, player, ct);

        var feed = await LoadAsync(db, player, ct);

        feed.Entries.Select(e => e.Title).Should().Equal("Sooner", "Later");
        feed.SharedTeamName.Should().BeNull();
    }

    [Test]
    public async Task AFeedInsideOneTeamCarriesThatTeamsName()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7217, ct, name: "Мозгобойня");
        var player = await SeedMemberAsync(db, team, telegramUserId: 7217, ct);
        var game = await SeedGameAsync(db, team, "One", Now.AddDays(2), ct);
        await SeedSignupAsync(db, game, player, ct);

        var feed = await LoadAsync(db, player, ct);

        feed.SharedTeamName.Should().Be("Мозгобойня");
    }

    [Test]
    public async Task SomebodyWithNoTeamsGetsAnEmptyFeedRatherThanAnError()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var stranger = await SeedPlayerAsync(db, telegramUserId: 7218, ct);

        var feed = await LoadAsync(db, stranger, ct);

        feed.Entries.Should().BeEmpty();
        feed.SharedTeamName.Should().BeNull();
    }

    [Test]
    public async Task TheEndOfAnEventIsThreeHoursAfterItsStart()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 7219, ct);
        var player = await SeedMemberAsync(db, team, telegramUserId: 7219, ct);
        var game = await SeedGameAsync(db, team, "One", Now.AddDays(2), ct);
        await SeedSignupAsync(db, game, player, ct);

        var entry = (await LoadAsync(db, player, ct)).Entries.Single();

        entry.EndsAt.Should().Be(entry.StartsAt.AddHours(3));
    }

    private static Task<CalendarFeed> LoadAsync(QuizrDb db, Player player, CancellationToken ct) =>
        new CalendarFeedService(db, new FakeTimeProvider(Now)).LoadAsync(player.Id, ct);

    private static async Task<Team> SeedTeamAsync(QuizrDb db, long chatId, CancellationToken ct, string name = "Team")
    {
        var team = new Team
        {
            ChatId = new TelegramChatId(chatId),
            Name = name,
            TimeZoneId = "Europe/Berlin",
            Locale = "en",
            CreatedAt = Now,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return team;
    }

    private static async Task<Game> SeedGameAsync(
        QuizrDb db,
        Team team,
        string title,
        DateTimeOffset startsAt,
        CancellationToken ct,
        int capacity = 6
    )
    {
        var game = new Game
        {
            TeamId = team.Id,
            Title = title,
            Venue = "The Pub",
            StartsAt = startsAt,
            Capacity = capacity,
            CreatedAt = Now,
            CreatedByPlayerId = await CreatorAsync(db, team, ct),
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);
        return game;
    }

    // Games.CreatedByPlayerId is a real foreign key, so a seeded game needs a real player
    // behind it — PlayerId(1) only happens to exist when another test got there first, which
    // under TUnit's in-class parallelism is luck rather than a fact. One creator per team,
    // keyed off the team's own chat id, which every test here already keeps unique; negated so
    // it cannot collide with the Telegram user ids the tests hand to their own players.
    private static async Task<PlayerId> CreatorAsync(QuizrDb db, Team team, CancellationToken ct)
    {
        var telegramUserId = new TelegramUserId(-team.ChatId.Value);
        var existing = await db.Players.SingleOrDefaultAsync(p => p.TelegramUserId == telegramUserId, ct);
        if (existing is not null)
        {
            return existing.Id;
        }

        var creator = new Player
        {
            TelegramUserId = telegramUserId,
            DisplayName = "Captain",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(creator);
        await db.SaveChangesAsync(ct);
        return creator.Id;
    }

    private static async Task<Game> SeedFinishedGameAsync(
        QuizrDb db,
        Team team,
        Player player,
        string title,
        DateTimeOffset startsAt,
        bool played,
        CancellationToken ct
    )
    {
        var game = await SeedGameAsync(db, team, title, startsAt, ct);
        game.FinishedAt = startsAt.AddHours(3);
        db.Participations.Add(
            new Participation
            {
                GameId = game.Id,
                PlayerId = player.Id,
                Kind = ParticipationKind.Member,
                Played = played,
                CreatedAt = startsAt,
            }
        );
        await db.SaveChangesAsync(ct);
        return game;
    }

    private static async Task<Player> SeedMemberAsync(QuizrDb db, Team team, long telegramUserId, CancellationToken ct)
    {
        var player = await SeedPlayerAsync(db, telegramUserId, ct);
        await SeedMembershipAsync(db, team, player, ct);
        return player;
    }

    private static async Task<Player> SeedPlayerAsync(QuizrDb db, long telegramUserId, CancellationToken ct)
    {
        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = "Player",
            CreatedAt = Now,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);
        return player;
    }

    private static async Task SeedMembershipAsync(QuizrDb db, Team team, Player player, CancellationToken ct)
    {
        db.Memberships.Add(
            new Membership
            {
                TeamId = team.Id,
                PlayerId = player.Id,
                JoinedAt = Now,
            }
        );
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Signup> SeedSignupAsync(
        QuizrDb db,
        Game game,
        Player player,
        CancellationToken ct,
        DateTimeOffset? createdAt = null
    )
    {
        var signup = new Signup
        {
            GameId = game.Id,
            PlayerId = player.Id,
            CreatedAt = createdAt ?? Now.AddDays(-7),
        };
        db.Signups.Add(signup);
        await db.SaveChangesAsync(ct);
        return signup;
    }

    private static async Task SeedGuestAsync(QuizrDb db, Game game, Player inviter, CancellationToken ct)
    {
        db.Signups.Add(
            new Signup
            {
                GameId = game.Id,
                InvitedByPlayerId = inviter.Id,
                CreatedAt = Now.AddDays(-6),
            }
        );
        await db.SaveChangesAsync(ct);
    }
}
