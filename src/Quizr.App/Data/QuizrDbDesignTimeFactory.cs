using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Quizr.App.Data;

// Lets `dotnet ef migrations add` build a model without running the host.
// The connection string here is never dialled — migrations add only builds
// the model, it doesn't connect — so a placeholder is fine.
internal sealed class QuizrDbDesignTimeFactory : IDesignTimeDbContextFactory<QuizrDb>
{
    public QuizrDb CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<QuizrDb>()
            .UseNpgsql("Host=localhost;Database=quizr;Username=quizr;Password=quizr")
            .Options;

        return new QuizrDb(options);
    }
}
