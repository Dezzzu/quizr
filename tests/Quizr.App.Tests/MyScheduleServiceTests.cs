using AwesomeAssertions;
using Quizr.App.Data;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Game = Quizr.Domain.Entities.Game;

namespace Quizr.App.Tests;

[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class MyScheduleServiceTests
{
    private readonly PostgresFixture _fixture;

    public MyScheduleServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task ReturnsOnlyTheGamesThePersonIsSignedUpTo()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8501, ct);
        var player = await SeedPlayerAsync(db, ct);
        var mine = await SeedGameAsync(db, team, "Mine", Days(1), ct);
        await SeedGameAsync(db, team, "Somebody else's", Days(2), ct);
        await SeedSignupAsync(db, mine, player, ct);

        var entries = await new MyScheduleService(db).LoadAsync(player.Id, [team], ct);

        entries.Should().ContainSingle().Which.Game.Title.Should().Be("Mine");
    }

    // Invariant 3: dropping out cancels the signup entirely, so the game leaves the schedule.
    [Test]
    public async Task LeavesOutAGameThePersonHasDroppedOutOf()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8502, ct);
        var player = await SeedPlayerAsync(db, ct);
        var game = await SeedGameAsync(db, team, "Dropped", Days(1), ct);
        var signup = await SeedSignupAsync(db, game, player, ct);
        signup.CancelledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var entries = await new MyScheduleService(db).LoadAsync(player.Id, [team], ct);

        entries.Should().BeEmpty();
    }

    [Test]
    public async Task LeavesOutFinishedAndDeclinedGames()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8503, ct);
        var player = await SeedPlayerAsync(db, ct);
        var finished = await SeedGameAsync(db, team, "Finished", Days(-1), ct);
        var declined = await SeedGameAsync(db, team, "Declined", Days(1), ct);
        var live = await SeedGameAsync(db, team, "Live", Days(2), ct);
        finished.FinishedAt = DateTimeOffset.UtcNow;
        declined.DeclinedAt = DateTimeOffset.UtcNow;
        await SeedSignupAsync(db, finished, player, ct);
        await SeedSignupAsync(db, declined, player, ct);
        await SeedSignupAsync(db, live, player, ct);
        await db.SaveChangesAsync(ct);

        var entries = await new MyScheduleService(db).LoadAsync(player.Id, [team], ct);

        entries.Should().ContainSingle().Which.Game.Title.Should().Be("Live");
    }

    [Test]
    public async Task OrdersByWhenTheGamesActuallyStart()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8504, ct);
        var player = await SeedPlayerAsync(db, ct);
        var later = await SeedGameAsync(db, team, "Later", Days(5), ct);
        var sooner = await SeedGameAsync(db, team, "Sooner", Days(2), ct);
        await SeedSignupAsync(db, later, player, ct);
        await SeedSignupAsync(db, sooner, player, ct);

        var entries = await new MyScheduleService(db).LoadAsync(player.Id, [team], ct);

        entries.Select(e => e.Game.Title).Should().Equal("Sooner", "Later");
    }

    // Invariant 2: the split is derived from the ordered queue, so the eleventh person into a
    // ten-seat game is on the reserve at position 1 — and only their own schedule can say so.
    [Test]
    public async Task ReportsThePositionOnTheReserveWhenTheSeatsAreGone()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8505, ct);
        var game = await SeedGameAsync(db, team, "Full house", Days(1), ct, capacity: 2);
        var first = await SeedPlayerAsync(db, ct);
        var second = await SeedPlayerAsync(db, ct);
        var third = await SeedPlayerAsync(db, ct);
        await SeedSignupAsync(db, game, first, ct, createdAt: Days(-3));
        await SeedSignupAsync(db, game, second, ct, createdAt: Days(-2));
        await SeedSignupAsync(db, game, third, ct, createdAt: Days(-1));
        var service = new MyScheduleService(db);

        var firstEntry = await service.LoadAsync(first.Id, [team], ct);
        var thirdEntry = await service.LoadAsync(third.Id, [team], ct);

        firstEntry.Should().ContainSingle().Which.Placement.IsPlaying.Should().BeTrue();
        var reserve = thirdEntry.Should().ContainSingle().Subject.Placement;
        reserve.IsPlaying.Should().BeFalse();
        reserve.Position.Should().Be(1);
    }

    [Test]
    public async Task CountsOnlyTheGuestsThisPersonBrought()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8506, ct);
        var game = await SeedGameAsync(db, team, "Guests", Days(1), ct);
        var host = await SeedPlayerAsync(db, ct);
        var other = await SeedPlayerAsync(db, ct);
        await SeedSignupAsync(db, game, host, ct);
        await SeedSignupAsync(db, game, other, ct);
        await SeedGuestAsync(db, game, host, ct);
        await SeedGuestAsync(db, game, host, ct);
        await SeedGuestAsync(db, game, other, ct);

        var entries = await new MyScheduleService(db).LoadAsync(host.Id, [team], ct);

        entries.Should().ContainSingle().Which.GuestCount.Should().Be(2);
    }

    // The reason /myschedule exists at all in a DM: two teams that deliberately cannot see
    // each other's Boards, merged into the one evening the person actually has.
    [Test]
    public async Task MergesGamesFromEveryTeamThePersonBelongsTo()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var berlin = await SeedTeamAsync(db, chatId: 8507, ct, name: "Berlin Quizzers");
        var moscow = await SeedTeamAsync(db, chatId: 8508, ct, name: "Moscow Nerds");
        var player = await SeedPlayerAsync(db, ct);
        await SeedMembershipAsync(db, berlin, player, ct);
        await SeedMembershipAsync(db, moscow, player, ct);
        var berlinGame = await SeedGameAsync(db, berlin, "Berlin game", Days(5), ct);
        var moscowGame = await SeedGameAsync(db, moscow, "Moscow game", Days(2), ct);
        await SeedSignupAsync(db, berlinGame, player, ct);
        await SeedSignupAsync(db, moscowGame, player, ct);
        var service = new MyScheduleService(db);

        var teams = await service.LoadTeamsAsync(player.Id, ct);
        var entries = await service.LoadAsync(player.Id, teams, ct);

        teams.Select(t => t.Name).Should().BeEquivalentTo(["Berlin Quizzers", "Moscow Nerds"]);
        entries.Select(e => e.Game.Title).Should().Equal("Moscow game", "Berlin game");
        entries.Select(e => e.Team.Name).Should().Equal("Moscow Nerds", "Berlin Quizzers");
    }

    // A group's /myschedule passes the one team it is in, and must not leak the person's
    // other teams into a chat that asked about its own games.
    [Test]
    public async Task ShowsOnlyTheGivenTeamsGamesWhenAskedInThatTeamsChat()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var here = await SeedTeamAsync(db, chatId: 8509, ct, name: "Here");
        var elsewhere = await SeedTeamAsync(db, chatId: 8510, ct, name: "Elsewhere");
        var player = await SeedPlayerAsync(db, ct);
        await SeedMembershipAsync(db, here, player, ct);
        await SeedMembershipAsync(db, elsewhere, player, ct);
        await SeedSignupAsync(db, await SeedGameAsync(db, here, "Here game", Days(1), ct), player, ct);
        await SeedSignupAsync(db, await SeedGameAsync(db, elsewhere, "Elsewhere game", Days(2), ct), player, ct);

        var entries = await new MyScheduleService(db).LoadAsync(player.Id, [here], ct);

        entries.Should().ContainSingle().Which.Game.Title.Should().Be("Here game");
    }

    [Test]
    public async Task FindsNoTeamsForSomebodyWhoHasNeverBeenInAChat()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var stranger = await SeedPlayerAsync(db, ct);

        var teams = await new MyScheduleService(db).LoadTeamsAsync(stranger.Id, ct);

        teams.Should().BeEmpty();
    }

    // The heads-up on joining: the other games already held that evening, minus the one just
    // joined — the caller has just said "you're in" about that one.
    [Test]
    public async Task SameDayListsTheOtherGamesThatEveningAndNotTheOneJustJoined()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8511, ct);
        var player = await SeedPlayerAsync(db, ct);
        await SeedMembershipAsync(db, team, player, ct);
        var joined = await SeedGameAsync(db, team, "Joined", At(2026, 10, 9, 19, 0), ct);
        var earlier = await SeedGameAsync(db, team, "Earlier that day", At(2026, 10, 9, 16, 0), ct);
        var nextDay = await SeedGameAsync(db, team, "Next day", At(2026, 10, 10, 19, 0), ct);
        await SeedSignupAsync(db, joined, player, ct);
        await SeedSignupAsync(db, earlier, player, ct);
        await SeedSignupAsync(db, nextDay, player, ct);

        var sameDay = await new MyScheduleService(db).LoadSameDayAsync(player.Id, joined, team, ct);

        sameDay.Should().ContainSingle().Which.Game.Title.Should().Be("Earlier that day");
    }

    [Test]
    public async Task SameDayIsEmptyWhenNothingElseIsHeldThatEvening()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var team = await SeedTeamAsync(db, chatId: 8512, ct);
        var player = await SeedPlayerAsync(db, ct);
        await SeedMembershipAsync(db, team, player, ct);
        var joined = await SeedGameAsync(db, team, "Joined", At(2026, 10, 9, 19, 0), ct);
        await SeedSignupAsync(db, joined, player, ct);

        var sameDay = await new MyScheduleService(db).LoadSameDayAsync(player.Id, joined, team, ct);

        sameDay.Should().BeEmpty();
    }

    // The clash worth warning about is the one this chat's Board cannot show: a seat held in a
    // different team the same evening.
    [Test]
    public async Task SameDayCrossesIntoThePersonsOtherTeams()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var here = await SeedTeamAsync(db, chatId: 8513, ct, name: "Here");
        var elsewhere = await SeedTeamAsync(db, chatId: 8514, ct, name: "Elsewhere");
        var player = await SeedPlayerAsync(db, ct);
        await SeedMembershipAsync(db, here, player, ct);
        await SeedMembershipAsync(db, elsewhere, player, ct);
        var joined = await SeedGameAsync(db, here, "Joined", At(2026, 10, 9, 19, 0), ct);
        var other = await SeedGameAsync(db, elsewhere, "Elsewhere game", At(2026, 10, 9, 20, 0), ct);
        await SeedSignupAsync(db, joined, player, ct);
        await SeedSignupAsync(db, other, player, ct);

        var sameDay = await new MyScheduleService(db).LoadSameDayAsync(player.Id, joined, here, ct);

        var entry = sameDay.Should().ContainSingle().Subject;
        entry.Game.Title.Should().Be("Elsewhere game");
        entry.Team.Name.Should().Be("Elsewhere");
    }

    // Each game is dated by its own team's clock — the date its own announcement shows — not by
    // the joining team's. A Tokyo team's early-Saturday game is a Saturday game to them, even
    // though the instant is still Friday evening in Berlin.
    [Test]
    public async Task SameDayDatesEachGameOnItsOwnTeamsClock()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var berlin = await SeedTeamAsync(db, chatId: 8515, ct, name: "Berlin");
        var tokyo = await SeedTeamAsync(db, chatId: 8516, ct, name: "Tokyo", timeZoneId: "Asia/Tokyo");
        var player = await SeedPlayerAsync(db, ct);
        await SeedMembershipAsync(db, berlin, player, ct);
        await SeedMembershipAsync(db, tokyo, player, ct);
        // Friday 22:00 in Berlin, and Saturday 05:00 in Tokyo.
        var joined = await SeedGameAsync(db, berlin, "Berlin Friday", At(2026, 10, 9, 20, 0), ct);
        var other = await SeedGameAsync(db, tokyo, "Tokyo Saturday", At(2026, 10, 9, 20, 0), ct);
        await SeedSignupAsync(db, joined, player, ct);
        await SeedSignupAsync(db, other, player, ct);

        var sameDay = await new MyScheduleService(db).LoadSameDayAsync(player.Id, joined, berlin, ct);

        sameDay.Should().BeEmpty();
    }

    private static DateTimeOffset Days(int offset) => DateTimeOffset.UtcNow.AddDays(offset);

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static long _idSequence = 9_500_000;

    private static long NextId() => Interlocked.Increment(ref _idSequence);

    private static async Task<Team> SeedTeamAsync(
        QuizrDb db,
        long chatId,
        CancellationToken ct,
        string name = "Test team",
        string timeZoneId = "Europe/Berlin"
    )
    {
        var team = new Team
        {
            ChatId = new TelegramChatId(chatId),
            Name = name,
            TimeZoneId = timeZoneId,
            Locale = "en",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return team;
    }

    private static async Task<Player> SeedPlayerAsync(QuizrDb db, CancellationToken ct)
    {
        var player = new Player
        {
            TelegramUserId = new TelegramUserId(NextId()),
            DisplayName = "Player",
            CreatedAt = DateTimeOffset.UtcNow,
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
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Game> SeedGameAsync(
        QuizrDb db,
        Team team,
        string title,
        DateTimeOffset startsAt,
        CancellationToken ct,
        int capacity = 10
    )
    {
        var creator = await SeedPlayerAsync(db, ct);
        var game = new Game
        {
            TeamId = team.Id,
            Title = title,
            Venue = "The Pub",
            StartsAt = startsAt,
            Capacity = capacity,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = creator.Id,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);
        return game;
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
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
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
                CreatedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);
    }
}
