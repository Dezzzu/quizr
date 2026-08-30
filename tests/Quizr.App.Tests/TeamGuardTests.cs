using AwesomeAssertions;
using NSubstitute;
using Quizr.App.Data;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Quizr.App.Tests;

public class TeamGuardTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public TeamGuardTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public void EnsureTimeZoneConfiguredSucceedsWhenSet()
    {
        var team = TeamWithTimeZone("Europe/Berlin");

        TeamGuard.EnsureTimeZoneConfigured(team).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureTimeZoneConfiguredFailsWhenNotSet()
    {
        var team = TeamWithTimeZone(null);

        var result = TeamGuard.EnsureTimeZoneConfigured(team);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<BusinessError.TeamNotConfigured>();
    }

    [Fact]
    public async Task IsCaptainAsyncIsTrueForAnExplicitGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (team, player) = await SeedTeamAndPlayerAsync(db, chatId: 2001, telegramUserId: 1, ct);
        db.Memberships.Add(
            new Membership
            {
                TeamId = team.Id,
                PlayerId = player.Id,
                IsCaptain = true,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);

        var guard = new TeamGuard(db, TelegramBotClientTestHelper.Create());

        var isCaptain = await guard.IsCaptainAsync(
            team.Id,
            player.Id,
            new TelegramChatId(2001),
            new TelegramUserId(1),
            ct
        );

        isCaptain.Should().BeTrue();
    }

    [Fact]
    public async Task IsCaptainAsyncIsTrueForAChatAdminWithNoExplicitGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (team, player) = await SeedTeamAndPlayerAsync(db, chatId: 2002, telegramUserId: 2, ct);
        db.Memberships.Add(
            new Membership
            {
                TeamId = team.Id,
                PlayerId = player.Id,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);

        var bot = Substitute.For<ITelegramBotClient>();
        bot.SendRequest(Arg.Any<GetChatMemberRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatMemberAdministrator
                {
                    User = new User { Id = 2, FirstName = "Admin" },
                }
            );
        var guard = new TeamGuard(db, bot);

        var isCaptain = await guard.IsCaptainAsync(
            team.Id,
            player.Id,
            new TelegramChatId(2002),
            new TelegramUserId(2),
            ct
        );

        isCaptain.Should().BeTrue();
    }

    [Fact]
    public async Task IsCaptainAsyncIsFalseOtherwise()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var (team, player) = await SeedTeamAndPlayerAsync(db, chatId: 2003, telegramUserId: 3, ct);
        db.Memberships.Add(
            new Membership
            {
                TeamId = team.Id,
                PlayerId = player.Id,
                JoinedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync(ct);

        // TelegramBotClientTestHelper's default GetChatMember response is a plain member.
        var guard = new TeamGuard(db, TelegramBotClientTestHelper.Create());

        var isCaptain = await guard.IsCaptainAsync(
            team.Id,
            player.Id,
            new TelegramChatId(2003),
            new TelegramUserId(3),
            ct
        );

        isCaptain.Should().BeFalse();
    }

    private static Team TeamWithTimeZone(string? timeZoneId) =>
        new()
        {
            ChatId = new TelegramChatId(1),
            Name = "Test team",
            Locale = "en",
            TimeZoneId = timeZoneId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static async Task<(Team Team, Player Player)> SeedTeamAndPlayerAsync(
        QuizrDb db,
        long chatId,
        long telegramUserId,
        CancellationToken ct
    )
    {
        var team = new Team
        {
            ChatId = new TelegramChatId(chatId),
            Name = "Test team",
            Locale = "en",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Teams.Add(team);

        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = "Player",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(player);

        await db.SaveChangesAsync(ct);
        return (team, player);
    }
}
