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

    public QuizrDb CreateContext()
    {
        var options = new DbContextOptionsBuilder<QuizrDb>().UseNpgsql(_container.GetConnectionString()).Options;

        return new QuizrDb(options);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
