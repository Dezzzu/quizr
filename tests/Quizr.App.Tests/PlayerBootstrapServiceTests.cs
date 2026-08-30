using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot.Types;

namespace Quizr.App.Tests;

[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class PlayerBootstrapServiceTests
{
    private readonly PostgresFixture _fixture;

    public PlayerBootstrapServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task GetOrCreateAsyncCreatesAPlayerForAnUnknownTelegramUser()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var service = new PlayerBootstrapService(db, new FakeTimeProvider());
        var telegramUser = new User
        {
            Id = 3001,
            FirstName = "Ann",
            LastName = "K",
            Username = "annk",
            LanguageCode = "ru",
        };

        var player = await service.GetOrCreateAsync(telegramUser, ct);

        player.TelegramUserId.Should().Be(new TelegramUserId(3001));
        player.DisplayName.Should().Be("Ann K");
        player.Username.Should().Be("annk");
        player.Locale.Should().BeNull();
        player.DmEnabled.Should().BeFalse();

        (await db.Players.CountAsync(p => p.TelegramUserId == new TelegramUserId(3001), ct)).Should().Be(1);
    }

    [Test]
    public async Task GetOrCreateAsyncIsIdempotentForTheSameTelegramUser()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var service = new PlayerBootstrapService(db, new FakeTimeProvider());
        var telegramUser = new User { Id = 3002, FirstName = "Bo" };

        var first = await service.GetOrCreateAsync(telegramUser, ct);
        var second = await service.GetOrCreateAsync(telegramUser, ct);

        first.Id.Should().Be(second.Id);
        (await db.Players.CountAsync(p => p.TelegramUserId == new TelegramUserId(3002), ct)).Should().Be(1);
    }

    [Test]
    public async Task EnsureMembershipAsyncIsIdempotent()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var db = _fixture.CreateContext();
        var service = new PlayerBootstrapService(db, new FakeTimeProvider());

        var team = new Team
        {
            ChatId = new TelegramChatId(3100),
            Name = "Test team",
            Locale = "en",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);

        var player = await service.GetOrCreateAsync(new User { Id = 3101, FirstName = "Cy" }, ct);

        await service.EnsureMembershipAsync(team.Id, player.Id, ct);
        await service.EnsureMembershipAsync(team.Id, player.Id, ct);

        (await db.Memberships.CountAsync(m => m.TeamId == team.Id && m.PlayerId == player.Id, ct)).Should().Be(1);
    }
}
