using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Testcontainers.PostgreSql;
using TUnit.Core.Interfaces;

namespace Quizr.App.Tests;

// One container for the whole test class — migrating it once and reusing the
// connection is far cheaper than a container per test. Tests that share this
// fixture must use their own Team/Game rows so they don't collide.
public sealed class PostgresFixture : IAsyncInitializer, IAsyncDisposable
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18").Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    // For anything that needs a connection of its own rather than a DbContext —
    // BotInstanceLock holds one outside EF's pool, because a pooled connection handed back
    // would take its advisory lock with it.
    public string ConnectionString => _container.GetConnectionString();

    // The interceptor is on by default because it is on in production: a service test that
    // saved without it would be exercising a context this app never constructs. Pass a
    // FakeTimeProvider where a test asserts on Game.RevisedAt.
    public QuizrDb CreateContext(TimeProvider? clock = null)
    {
        var options = new DbContextOptionsBuilder<QuizrDb>()
            .UseNpgsql(_container.GetConnectionString())
            .AddInterceptors(new CalendarVersionInterceptor(clock ?? TimeProvider.System))
            .Options;

        return new QuizrDb(options);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
