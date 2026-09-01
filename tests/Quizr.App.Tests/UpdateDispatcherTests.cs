using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.App.Telegram;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Quizr.App.Tests;

// Builds a real DI container — the same registrations Program.cs uses — so this exercises
// the genuine UpdateDispatcher -> UpdateRouter call path rather than a substitute standing
// in for dispatch logic. Only the Telegram client and IAlertSender are faked.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerClass)]
public class UpdateDispatcherTests
{
    private readonly PostgresFixture _fixture;

    public UpdateDispatcherTests(PostgresFixture fixture) => _fixture = fixture;

    [Test]
    public async Task ARouterFailureIsCaughtAlertedAndDoesNotPropagate()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;

        var bot = Substitute.For<ITelegramBotClient>();
        bot.When(client => client.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("boom"));

        var alertSender = Substitute.For<IAlertSender>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _fixture.CreateContext());
        services.AddSingleton(bot);
        services.AddSingleton(alertSender);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton<IStrings, Strings>();
        services.AddSingleton<IMessageEditDebouncer, MessageEditDebouncer>();
        services.AddSingleton<IMessageSender, MessageSender>();
        services.AddScoped<TeamGuard>();
        services.AddScoped<TeamBootstrapService>();
        services.AddScoped<PlayerBootstrapService>();
        services.AddScoped<UpdateRouter>();
        services.AddSingleton(TestMeterFactory.Metrics());
        services.AddSingleton<UpdateDispatcher>();

        await using var provider = services.BuildServiceProvider();

        await using (var seedDb = _fixture.CreateContext())
        {
            seedDb.Teams.Add(
                new Team
                {
                    ChatId = new TelegramChatId(5001),
                    Name = "Test team",
                    Locale = "en",
                    CreatedAt = DateTimeOffset.UtcNow,
                }
            );
            await seedDb.SaveChangesAsync(ct);
        }

        var dispatcher = provider.GetRequiredService<UpdateDispatcher>();
        var update = new Update
        {
            Id = 1,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = 5001 },
                From = new User { Id = 1, FirstName = "Ann" },
                Text = "/start",
                Date = DateTime.UtcNow,
            },
        };

        var act = () => dispatcher.HandleUpdateAsync(bot, update, ct);

        await act.Should().NotThrowAsync();
        await alertSender.Received(1).AlertAsync(Arg.Any<Exception>(), update, Arg.Any<CancellationToken>());
    }
}
