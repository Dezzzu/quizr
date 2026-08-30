using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Data;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Tests;

public class NotificationRecorderTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public NotificationRecorderTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TheFirstRecordForASignupAndKindSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var signupId = await SeedSignupAsync(db, chatId: 6001, ct);

        var recorded = await NotificationRecorder.TryRecordAsync(
            db,
            signupId,
            NotificationKind.ReservePromotion,
            new FakeTimeProvider(),
            ct
        );

        recorded.Should().BeTrue();
    }

    [Fact]
    public async Task ASecondRecordForTheSameSignupAndKindIsRejectedAsADuplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var signupId = await SeedSignupAsync(db, chatId: 6002, ct);
        var clock = new FakeTimeProvider();
        await NotificationRecorder.TryRecordAsync(db, signupId, NotificationKind.ReservePromotion, clock, ct);

        var recordedAgain = await NotificationRecorder.TryRecordAsync(
            db,
            signupId,
            NotificationKind.ReservePromotion,
            clock,
            ct
        );

        recordedAgain.Should().BeFalse();
        (await db.Notifications.AsNoTracking().CountAsync(n => n.SignupId == signupId, ct)).Should().Be(1);
    }

    [Fact]
    public async Task TheDbContextStaysUsableAfterADuplicateIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var signupId = await SeedSignupAsync(db, chatId: 6003, ct);
        var clock = new FakeTimeProvider();
        await NotificationRecorder.TryRecordAsync(db, signupId, NotificationKind.ReservePromotion, clock, ct);
        await NotificationRecorder.TryRecordAsync(db, signupId, NotificationKind.ReservePromotion, clock, ct);

        var otherSignupId = await SeedSignupAsync(db, chatId: 6004, ct);
        var recorded = await NotificationRecorder.TryRecordAsync(
            db,
            otherSignupId,
            NotificationKind.ReservePromotion,
            clock,
            ct
        );

        recorded.Should().BeTrue();
    }

    private static async Task<SignupId> SeedSignupAsync(QuizrDb db, long chatId, CancellationToken ct)
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
            TelegramUserId = new TelegramUserId(chatId * 1000),
            DisplayName = "Creator",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(creator);
        await db.SaveChangesAsync(ct);

        var game = new Game
        {
            TeamId = team.Id,
            Title = "Quiz Night",
            Venue = "The Pub",
            StartsAt = DateTimeOffset.UtcNow.AddDays(1),
            Capacity = 5,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = creator.Id,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);

        var signup = new Signup { GameId = game.Id, CreatedAt = DateTimeOffset.UtcNow };
        db.Signups.Add(signup);
        await db.SaveChangesAsync(ct);

        return signup.Id;
    }
}
