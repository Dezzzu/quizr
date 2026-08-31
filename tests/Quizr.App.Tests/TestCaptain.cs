using Quizr.App.Data;
using Quizr.App.Services;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Tests;

// Authorization now lives in the application services (STYLE.md), so a service test has to
// supply a TeamGuard and an Actor the same way the update router does.
//
// The membership is seeded with an explicit IsCaptain grant, which is what makes this cheap:
// TeamGuard answers from the database and never reaches GetChatMember, so a service test needs
// no Telegram fake set up beyond the one the guard is constructed with. Tests about the
// chat-admin fallback itself belong in TeamGuardTests.
internal sealed record TestCaptain(TeamGuard Guard, Actor Actor, Player Player)
{
    public PlayerId PlayerId => Player.Id;

    public static async Task<TestCaptain> SeedAsync(QuizrDb db, Team team, long telegramUserId, CancellationToken ct)
    {
        var player = new Player
        {
            TelegramUserId = new TelegramUserId(telegramUserId),
            DisplayName = "Captain",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Players.Add(player);
        await db.SaveChangesAsync(ct);

        return await PromoteAsync(db, team, player, ct);
    }

    // For a test that already seeded the person it wants acting — the creator of the game
    // under test, typically — rather than needing a second one.
    public static async Task<TestCaptain> PromoteAsync(QuizrDb db, Team team, Player player, CancellationToken ct)
    {
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
        return new TestCaptain(guard, new Actor(player.Id, player.TelegramUserId), player);
    }
}
