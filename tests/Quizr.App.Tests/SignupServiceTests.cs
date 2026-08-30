using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Data;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Tests;

public class SignupServiceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public SignupServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task JoinAsyncAddsALiveSignupForThePlayer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5001, capacity: 5, ct);
        var player = await SeedPlayerAsync(db, telegramUserId: 5001, ct);
        var service = new SignupService(db, new FakeTimeProvider());

        var result = await service.JoinAsync(game, player.Id, ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.PlayerId.Should().Be(player.Id);
        result.Value.CancelledAt.Should().BeNull();
    }

    [Fact]
    public async Task JoinAsyncRejectsADuplicateSignup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5002, capacity: 5, ct);
        var player = await SeedPlayerAsync(db, telegramUserId: 5002, ct);
        var service = new SignupService(db, new FakeTimeProvider());
        await service.JoinAsync(game, player.Id, ct);

        var result = await service.JoinAsync(game, player.Id, ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<BusinessError.AlreadySignedUp>();
    }

    [Fact]
    public async Task JoinAsyncRejectsAFinishedGame()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5003, capacity: 5, ct);
        game.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var player = await SeedPlayerAsync(db, telegramUserId: 5003, ct);
        var service = new SignupService(db, new FakeTimeProvider());

        var result = await service.JoinAsync(game, player.Id, ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<BusinessError.GameAlreadyFinished>();
    }

    [Fact]
    public async Task BringGuestAsyncCreatesAnAnonymousGuestOwnedByTheInviter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5004, capacity: 5, ct);
        var inviter = await SeedPlayerAsync(db, telegramUserId: 5004, ct);
        var service = new SignupService(db, new FakeTimeProvider());

        var result = await service.BringGuestAsync(game, inviter.Id, ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.PlayerId.Should().BeNull();
        result.Value.GuestName.Should().BeNull();
        result.Value.InvitedByPlayerId.Should().Be(inviter.Id);
    }

    [Fact]
    public async Task NameGuestAsyncSetsTheGuestsName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5005, capacity: 5, ct);
        var inviter = await SeedPlayerAsync(db, telegramUserId: 5005, ct);
        var service = new SignupService(db, new FakeTimeProvider());
        var guest = (await service.BringGuestAsync(game, inviter.Id, ct)).Value;

        var result = await service.NameGuestAsync(guest.Id, inviter.Id, "Sasha", ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.GuestName.Should().Be("Sasha");
    }

    [Fact]
    public async Task NameGuestAsyncRejectsSomeoneElsesGuest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5006, capacity: 5, ct);
        var inviter = await SeedPlayerAsync(db, telegramUserId: 5006, ct);
        var stranger = await SeedPlayerAsync(db, telegramUserId: 50060, ct);
        var service = new SignupService(db, new FakeTimeProvider());
        var guest = (await service.BringGuestAsync(game, inviter.Id, ct)).Value;

        var result = await service.NameGuestAsync(guest.Id, stranger.Id, "Sasha", ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<BusinessError.NotYourGuest>();
    }

    [Fact]
    public async Task DropAsyncCancelsTheCallersOwnSignup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5007, capacity: 5, ct);
        var player = await SeedPlayerAsync(db, telegramUserId: 5007, ct);
        var service = new SignupService(db, new FakeTimeProvider());
        await service.JoinAsync(game, player.Id, ct);

        var result = await service.DropAsync(game, player.Id, ct);

        result.IsSuccess.Should().BeTrue();
        var live = await db
            .Signups.AsNoTracking()
            .Where(s => s.GameId == game.Id && s.CancelledAt == null)
            .ToListAsync(ct);
        live.Should().BeEmpty();
    }

    [Fact]
    public async Task DropAsyncFailsWhenThePlayerHasNoLiveSignup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5008, capacity: 5, ct);
        var player = await SeedPlayerAsync(db, telegramUserId: 5008, ct);
        var service = new SignupService(db, new FakeTimeProvider());

        var result = await service.DropAsync(game, player.Id, ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<BusinessError.NotSignedUp>();
    }

    [Fact]
    public async Task DropAsyncPromotesTheFirstReserveAndRecordsExactlyOneNotification()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5009, capacity: 1, ct);
        var playing = await SeedPlayerAsync(db, telegramUserId: 5009, ct);
        var reserve = await SeedPlayerAsync(db, telegramUserId: 50090, ct);
        var service = new SignupService(db, new FakeTimeProvider());
        await service.JoinAsync(game, playing.Id, ct);
        await service.JoinAsync(game, reserve.Id, ct);

        var result = await service.DropAsync(game, playing.Id, ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.NewlyPromoted.Should().ContainSingle(s => s.PlayerId == reserve.Id);

        var notifications = await db
            .Notifications.AsNoTracking()
            .Where(n => n.Kind == NotificationKind.ReservePromotion)
            .ToListAsync(ct);
        notifications.Should().ContainSingle();
    }

    [Fact]
    public async Task DropAsyncAutoCancelsAnUnnamedGuestOfTheDroppingPlayer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5010, capacity: 5, ct);
        var inviter = await SeedPlayerAsync(db, telegramUserId: 5010, ct);
        var service = new SignupService(db, new FakeTimeProvider());
        await service.JoinAsync(game, inviter.Id, ct);
        var guest = (await service.BringGuestAsync(game, inviter.Id, ct)).Value;

        var result = await service.DropAsync(game, inviter.Id, ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.AutoCancelledGuests.Should().ContainSingle(s => s.Id == guest.Id);
        result.Value.NamedGuestsNeedingChoice.Should().BeEmpty();
        (await db.Signups.AsNoTracking().SingleAsync(s => s.Id == guest.Id, ct)).CancelledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DropAsyncSurfacesANamedGuestInsteadOfCancellingThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5011, capacity: 5, ct);
        var inviter = await SeedPlayerAsync(db, telegramUserId: 5011, ct);
        var service = new SignupService(db, new FakeTimeProvider());
        await service.JoinAsync(game, inviter.Id, ct);
        var guest = (await service.BringGuestAsync(game, inviter.Id, ct)).Value;
        await service.NameGuestAsync(guest.Id, inviter.Id, "Sasha", ct);

        var result = await service.DropAsync(game, inviter.Id, ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.AutoCancelledGuests.Should().BeEmpty();
        result.Value.NamedGuestsNeedingChoice.Should().ContainSingle(s => s.Id == guest.Id);
        (await db.Signups.AsNoTracking().SingleAsync(s => s.Id == guest.Id, ct)).CancelledAt.Should().BeNull();
    }

    [Fact]
    public async Task ResolveGuestChoiceAsyncKeepingClearsTheInviterAndLeavesTheGuestLive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5012, capacity: 5, ct);
        var inviter = await SeedPlayerAsync(db, telegramUserId: 5012, ct);
        var service = new SignupService(db, new FakeTimeProvider());
        var guest = (await service.BringGuestAsync(game, inviter.Id, ct)).Value;
        await service.NameGuestAsync(guest.Id, inviter.Id, "Sasha", ct);

        var result = await service.ResolveGuestChoiceAsync(guest.Id, inviter.Id, keep: true, ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Kept.Should().BeTrue();
        var stored = await db.Signups.AsNoTracking().SingleAsync(s => s.Id == guest.Id, ct);
        stored.InvitedByPlayerId.Should().BeNull();
        stored.CancelledAt.Should().BeNull();
    }

    [Fact]
    public async Task ResolveGuestChoiceAsyncRemovingCancelsTheGuestAndCanPromote()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5013, capacity: 1, ct);
        var inviter = await SeedPlayerAsync(db, telegramUserId: 5013, ct);
        var reserve = await SeedPlayerAsync(db, telegramUserId: 50130, ct);
        var service = new SignupService(db, new FakeTimeProvider());
        var guest = (await service.BringGuestAsync(game, inviter.Id, ct)).Value; // takes the one seat
        await service.NameGuestAsync(guest.Id, inviter.Id, "Sasha", ct);
        await service.JoinAsync(game, reserve.Id, ct); // lands in reserve

        var result = await service.ResolveGuestChoiceAsync(guest.Id, inviter.Id, keep: false, ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Kept.Should().BeFalse();
        result.Value.NewlyPromoted.Should().ContainSingle(s => s.PlayerId == reserve.Id);
        (await db.Signups.AsNoTracking().SingleAsync(s => s.Id == guest.Id, ct)).CancelledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ResolveGuestChoiceAsyncRejectsAnyoneButTheInviter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var game = await SeedGameAsync(db, chatId: 5014, capacity: 5, ct);
        var inviter = await SeedPlayerAsync(db, telegramUserId: 5014, ct);
        var stranger = await SeedPlayerAsync(db, telegramUserId: 50140, ct);
        var service = new SignupService(db, new FakeTimeProvider());
        var guest = (await service.BringGuestAsync(game, inviter.Id, ct)).Value;
        await service.NameGuestAsync(guest.Id, inviter.Id, "Sasha", ct);

        var result = await service.ResolveGuestChoiceAsync(guest.Id, stranger.Id, keep: true, ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<BusinessError.NotYourGuest>();
    }

    private static async Task<Player> SeedPlayerAsync(QuizrDb db, long telegramUserId, CancellationToken ct)
    {
        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = $"Player {telegramUserId}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);
        return player;
    }

    private static async Task<Game> SeedGameAsync(QuizrDb db, long chatId, int capacity, CancellationToken ct)
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
            Capacity = capacity,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByPlayerId = creator.Id,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);

        return game;
    }
}
