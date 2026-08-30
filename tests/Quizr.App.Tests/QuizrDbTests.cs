using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Tests;

[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class QuizrDbTests
{
    private readonly PostgresFixture _fixture;

    public QuizrDbTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task WritesATeamFranchiseGameAndTwentySignupsAndReadsTheRosterBackInOrder()
    {
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 1, telegramUserId: 1);

        var start = DateTimeOffset.UtcNow;
        var signups = Enumerable
            .Range(0, 20)
            .Select(i => new Signup { GameId = game.Id, CreatedAt = start.AddMinutes(i) })
            .ToList();
        db.Signups.AddRange(signups);
        await db.SaveChangesAsync(TestContext.Current!.Execution.CancellationToken);

        await using var readDb = _fixture.CreateContext();
        var roster = await readDb
            .Signups.AsNoTracking()
            .Where(s => s.GameId == game.Id)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(TestContext.Current.Execution.CancellationToken);

        roster.Should().HaveCount(20);
        roster.Select(s => s.Id).Should().Equal(signups.Select(s => s.Id));
    }

    [Test]
    public async Task TheUniqueConstraintOnSignupAndKindRejectsADuplicateNotification()
    {
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 2, telegramUserId: 2);

        var signup = new Signup { GameId = game.Id, CreatedAt = DateTimeOffset.UtcNow };
        db.Signups.Add(signup);
        await db.SaveChangesAsync(TestContext.Current!.Execution.CancellationToken);

        db.Notifications.Add(
            new Notification
            {
                SignupId = signup.Id,
                Kind = NotificationKind.ReservePromotion,
                SentAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.Execution.CancellationToken);

        db.Notifications.Add(
            new Notification
            {
                SignupId = signup.Id,
                Kind = NotificationKind.ReservePromotion,
                SentAt = DateTimeOffset.UtcNow,
            }
        );

        // Started eagerly so the closure below captures the resulting Task,
        // not db itself — db is disposed at the end of this method, and a
        // closure over it would outlive that as far as static analysis can
        // tell, even though AwesomeAssertions awaits it immediately.
        var savingDuplicate = db.SaveChangesAsync(TestContext.Current.Execution.CancellationToken);

        await FluentActions.Awaiting(() => savingDuplicate).Should().ThrowAsync<DbUpdateException>();
    }

    private static async Task<Game> SeedGameAsync(QuizrDb db, long chatId, long telegramUserId)
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

        var creator = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = "Creator",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(creator);
        await db.SaveChangesAsync();

        var franchise = new Franchise
        {
            TeamId = team.Id,
            Name = "Квиз, плиз!",
            DefaultVenue = "Bar",
            DefaultCapacity = 10,
            Schedule = new Dictionary<DayOfWeek, TimeOnly> { [DayOfWeek.Monday] = new(19, 0) },
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Franchises.Add(franchise);
        await db.SaveChangesAsync();

        var game = new Game
        {
            TeamId = team.Id,
            FranchiseId = franchise.Id,
            Title = "Квиз, плиз! #1",
            Venue = franchise.DefaultVenue!,
            StartsAt = DateTimeOffset.UtcNow.AddDays(1),
            Capacity = franchise.DefaultCapacity!.Value,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = creator.Id,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync();

        return game;
    }
}
