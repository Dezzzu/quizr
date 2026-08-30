using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Scheduling;
using Quizr.App.Services;
using Quizr.App.Telegram;
using Quizr.Domain;
using Telegram.Bot;

// Composition root. Generic host, not ASP.NET Core — nothing listens on a port
// in phase 1. See STACK.md before adding anything here.
var builder = Host.CreateApplicationBuilder(args);

// Host.CreateApplicationBuilder only auto-loads user secrets when EnvironmentName is
// "Development", which needs DOTNET_ENVIRONMENT set — easy to forget locally, unlike
// WebApplication.CreateBuilder's ASPNETCORE_ENVIRONMENT default. Added explicitly so
// CLAUDE.md's "user secrets locally" actually works without that extra env var. Optional:
// the secrets file won't exist in a real deployment, where env vars are used instead.
builder.Configuration.AddUserSecrets<Program>(optional: true);

// A human at an interactive terminal wants readable text; a log aggregator ingesting
// captured/piped stdout (Docker, systemd, CI) wants structured JSON it can parse.
// Console.IsOutputRedirected tells the two apart without an extra environment variable
// to remember — DOTNET_ENVIRONMENT quietly defaults to Production even when run locally,
// same footgun as the user-secrets loading above.
if (Console.IsOutputRedirected)
{
    builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
}
else
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.IncludeScopes = true;
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
}

// EF Core's per-command SQL, the Telegram HTTP client's per-request tracing, and Polly's
// per-attempt success logs are Information-level noise that floods every single update —
// and the HTTP client's logs include the bot token in the request URI on every line.
// Only warnings and actual failures need to surface here; CLAUDE.md's own structured
// LogInformation calls at application call sites are untouched by this.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Polly", LogLevel.Warning);

var botToken =
    builder.Configuration["QUIZR_BOT_TOKEN"] ?? throw new InvalidOperationException("QUIZR_BOT_TOKEN is not set.");
var connectionString = builder.Configuration["QUIZR_DB"] ?? throw new InvalidOperationException("QUIZR_DB is not set.");

var alertChatIdRaw = builder.Configuration["QUIZR_ALERT_CHAT_ID"];
TelegramChatId? alertChatId = alertChatIdRaw is null
    ? null
    : new TelegramChatId(long.Parse(alertChatIdRaw, CultureInfo.InvariantCulture));

builder.Services.AddDbContext<QuizrDb>(options => options.UseNpgsql(connectionString));

// Retries honouring Telegram's `retry_after` come from the standard handler's default
// retry strategy, which already respects the Retry-After response header. See STACK.md.
builder.Services.AddHttpClient("telegram-bot").AddStandardResilienceHandler();
builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("telegram-bot");
    return new TelegramBotClient(botToken, httpClient);
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IStrings, Strings>();
builder.Services.AddSingleton<IMessageEditDebouncer, MessageEditDebouncer>();
builder.Services.AddSingleton<IMessageSender, MessageSender>();
builder.Services.AddSingleton<IAlertSender>(sp => new AlertSender(
    sp.GetRequiredService<ITelegramBotClient>(),
    alertChatId,
    sp.GetRequiredService<ILogger<AlertSender>>()
));

builder.Services.AddScoped<TeamGuard>();
builder.Services.AddScoped<TeamBootstrapService>();
builder.Services.AddScoped<PlayerBootstrapService>();
builder.Services.AddScoped<ISignupService, SignupService>();
builder.Services.AddScoped<IFranchiseService, FranchiseService>();
builder.Services.AddScoped<IGameService, GameService>();
builder.Services.AddScoped<IParticipationService, ParticipationService>();
builder.Services.AddScoped<AnnouncementService>();
builder.Services.AddScoped<BoardService>();
builder.Services.AddScoped<SchedulerService>();
builder.Services.AddScoped<UpdateRouter>();
builder.Services.AddSingleton<UpdateDispatcher>();
builder.Services.AddHostedService<BotHostedService>();
builder.Services.AddHostedService<SchedulerHostedService>();

var host = builder.Build();

// Migrations applied at startup — STACK.md.
using (var migrationScope = host.Services.CreateScope())
{
    await migrationScope.ServiceProvider.GetRequiredService<QuizrDb>().Database.MigrateAsync();
}

await host.RunAsync();
