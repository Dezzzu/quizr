using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Services;
using Quizr.App.Telegram;
using Quizr.Domain;
using Telegram.Bot;

// Composition root. Generic host, not ASP.NET Core — nothing listens on a port
// in phase 1. See STACK.md before adding anything here.
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddJsonConsole();

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
builder.Services.AddScoped<AnnouncementService>();
builder.Services.AddScoped<BoardService>();
builder.Services.AddScoped<UpdateRouter>();
builder.Services.AddSingleton<UpdateDispatcher>();
builder.Services.AddHostedService<BotHostedService>();

var host = builder.Build();

// Migrations applied at startup — STACK.md.
using (var migrationScope = host.Services.CreateScope())
{
    await migrationScope.ServiceProvider.GetRequiredService<QuizrDb>().Database.MigrateAsync();
}

await host.RunAsync();
