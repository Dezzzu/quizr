using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Quizr.App.Calendar;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Scheduling;
using Quizr.App.Services;
using Quizr.App.Telegram;
using Quizr.App.Telemetry;
using Quizr.Domain;
using Telegram.Bot;

// Composition root. A WebApplication rather than the generic host, for exactly one reason:
// the per-player calendar feed is an HTTP endpoint (docs/CALENDAR.md), and STACK.md's own
// prediction that the swap would cost "a few lines with hosted services carrying over
// unchanged" held. The bot still long-polls and still dials outward for everything it does.
//
// One thing that came with the port and must not follow: DEPLOY.md forbids configuring a
// Coolify health check, because a passing one is what lets Coolify start a second container
// before stopping the first, and two pollers on one bot token collide.
var builder = WebApplication.CreateBuilder(args);

// Both builders only auto-load user secrets when EnvironmentName is "Development", which
// needs ASPNETCORE_ENVIRONMENT (or DOTNET_ENVIRONMENT) set — easy to forget locally. Added
// explicitly so CLAUDE.md's "user secrets locally" works without that extra env var.
// Optional: the secrets file won't exist in a real deployment, where env vars are used.
builder.Configuration.AddUserSecrets<Program>(optional: true);

// Readable text everywhere, including under Docker. Structure leaves over OTLP instead
// (below), and it leaves *better* — Seq reconstructs the message template and its named
// properties from the exporter, where an aggregator re-parsing a JSON console line only ever
// recovers whatever fields it was told to look for. That leaves stdout free to be what a
// person actually reads through `docker logs`, which is the one place the exporter cannot
// help: it batches, so a crash on the way up takes the last records with it.
builder.Logging.AddSimpleConsole(options =>
{
    options.IncludeScopes = true;
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

// EF Core's per-command SQL, the Telegram HTTP client's per-request tracing, and Polly's
// per-attempt success logs are Information-level noise that floods every single update —
// and the HTTP client's logs include the bot token in the request URI on every line.
// Only warnings and actual failures need to surface here; CLAUDE.md's own structured
// LogInformation calls at application call sites are untouched by this.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Update", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Polly", LogLevel.Warning);

// The calendar token is the credential and lives in the request path, so ASP.NET's own
// "Request starting HTTP/1.1 GET /cal/feed.ics?t=<token>" at Information prints the whole URL,
// query included, which would write the credential to stdout and ship it to Seq on every fetch
// — the same leak the HttpClient filter above exists for, now pointing inward. The token being
// in the query rather than the path is what handles the rest: see CalendarUrls.Route.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var botToken =
    builder.Configuration["QUIZR_BOT_TOKEN"] ?? throw new InvalidOperationException("QUIZR_BOT_TOKEN is not set.");
var connectionString = builder.Configuration["QUIZR_DB"] ?? throw new InvalidOperationException("QUIZR_DB is not set.");

// Unset means the feed is off: the endpoint is never mapped and /mycalendar says so. A local
// run and a deployment with no domain yet both behave exactly as they did before it existed.
var publicUrl = builder.Configuration["QUIZR_PUBLIC_URL"];

var alertChatIdRaw = builder.Configuration["QUIZR_ALERT_CHAT_ID"];
TelegramChatId? alertChatId = alertChatIdRaw is null
    ? null
    : new TelegramChatId(long.Parse(alertChatIdRaw, CultureInfo.InvariantCulture));

// Singleton because it holds only the clock and reads everything else off the context it is
// handed. Registered on the context rather than called by each service that saves — see the
// file's own header, and docs/CALENDAR.md.
builder.Services.AddSingleton<CalendarVersionInterceptor>();

builder.Services.AddDbContext<QuizrDb>(
    (sp, options) =>
        options
            .UseNpgsql(connectionString)
            .AddInterceptors(sp.GetRequiredService<CalendarVersionInterceptor>())
            // NotificationRecorder's dedup insert (CLAUDE.md's Conventions) deliberately relies on
            // a unique-constraint rejection on the expected duplicate path — EF logs the failed
            // command and the failed SaveChanges at Error *inside* SaveChangesAsync, before the
            // catch that handles it ever runs, so left alone every rejected duplicate reads as a
            // crash. Only these two events, not the whole Database.Command/Update categories: a
            // genuinely unexpected failure elsewhere still logs at its own severity.
            .ConfigureWarnings(warnings =>
                warnings.Log(
                    (RelationalEventId.CommandError, LogLevel.Warning),
                    (CoreEventId.SaveChangesFailed, LogLevel.Warning)
                )
            )
);

// Retries honouring Telegram's `retry_after` come from the standard handler's default
// retry strategy, which already respects the Retry-After response header. See STACK.md.
builder.Services.AddHttpClient("telegram-bot").AddStandardResilienceHandler();
builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("telegram-bot");
    return new TelegramBotClient(botToken, httpClient);
});

// Logs and metrics both leave over OTLP, which the process pushes: no scrape target, and
// nothing for Coolify to mistake for a health check it could hang a rolling update on. There is
// a port now, for the calendar feed — which makes that second point more important rather than
// less. DEPLOY.md explains why two containers on one bot token is the failure worth this care.
//
// Still deliberately no tracing, and the calendar feed adds a second reason: an HttpClient span
// records the request URI in url.full, every Telegram call carries the bot token in its path,
// and an inbound span would record the feed's own URL — query string included. The metrics and logs the same
// instrumentation emits are labelled with server.address, method and status code only, so they
// carry no secret — the same leak Program.cs already filters out of the HTTP logs below.
builder.Services.AddMetrics();
builder.Services.AddSingleton<QuizrMetrics>();

// The standard OTEL_* variables configure the exporter itself — endpoint, protocol, headers —
// so there is nothing to parse here. This only decides whether to turn it on, which keeps a
// local run with no collector from retrying an export it can never make: developing against
// the console alone needs no OTEL_* set at all, and no Seq running.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder
        .Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("quizr"))
        // IncludeScopes is what carries the per-update scope (update id, chat id, user id —
        // CLAUDE.md's Conventions) through as queryable properties rather than a prefix baked
        // into the rendered text. The exporter also ships each call site's original message
        // template, which is what makes the "never an interpolated string" rule pay off at the
        // other end: Seq indexes {UserId} and {GameId} as fields, not as substrings.
        .WithLogging(logging => logging.AddOtlpExporter(), options => options.IncludeScopes = true)
        .WithMetrics(metrics =>
            metrics
                .AddMeter(QuizrMetrics.MeterName)
                .AddRuntimeInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter()
        );
}

builder.Services.AddSingleton(new CalendarUrls(publicUrl));
builder.Services.AddScoped<CalendarFeedService>();
builder.Services.AddScoped<ICalendarSubscriptionService, CalendarSubscriptionService>();
builder.Services.AddScoped<CalendarEndpoint>();

// Keyed by (player, version, format version, UTC date), so a bump changes the key and nothing
// needs evicting for correctness. The size limit is for the date component alone: yesterday's
// keys are unreachable but would otherwise sit here for the life of the process.
builder.Services.AddMemoryCache(options => options.SizeLimit = 64 * 1024 * 1024);

// The numbers, and the reasoning behind them, live with the feature rather than here.
builder.Services.AddRateLimiter(CalendarRateLimits.Configure);

// Caps how long a feed request may take. Middleware rather than a linked CancellationToken
// inside the handler: the timeout is then declared next to the route it applies to, and a
// request that runs over answers 504 rather than the 500 a hand-rolled one produced.
builder.Services.AddRequestTimeouts();

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
builder.Services.AddScoped<ITeamService, TeamService>();
builder.Services.AddScoped<IDialogService, DialogService>();
builder.Services.AddScoped<ISignupService, SignupService>();
builder.Services.AddScoped<IFranchiseService, FranchiseService>();
builder.Services.AddScoped<IGameService, GameService>();
builder.Services.AddScoped<IParticipationService, ParticipationService>();
builder.Services.AddScoped<AnnouncementService>();
builder.Services.AddScoped<BoardService>();
builder.Services.AddScoped<MyScheduleService>();
builder.Services.AddScoped<SchedulerService>();
builder.Services.AddScoped<UpdateRouter>();
builder.Services.AddSingleton<UpdateDispatcher>();
builder.Services.AddHostedService<BotHostedService>();
builder.Services.AddHostedService<SchedulerHostedService>();

var app = builder.Build();

// Migrations applied at startup — STACK.md.
using (var migrationScope = app.Services.CreateScope())
{
    await migrationScope.ServiceProvider.GetRequiredService<QuizrDb>().Database.MigrateAsync();
}

// Coolify's proxy terminates TLS and forwards, so without this every request appears to come
// from the proxy and the per-IP rate limit degenerates into one global bucket. The known-proxy
// lists are cleared because that proxy's address on the Docker network isn't knowable from
// here and isn't stable; the container is never exposed directly, so the only thing that can
// set these headers is the proxy in front of it.
var forwardedHeaders = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor };
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

app.UseRateLimiter();

// After UseRouting, which WebApplication inserts ahead of this, so the middleware can see the
// per-endpoint policy WithRequestTimeout attaches below.
app.UseRequestTimeouts();

if (publicUrl is not null)
{
    app.MapMethods(
            CalendarUrls.Route,
            // HEAD explicitly: several calendar clients probe with it before subscribing, and
            // MapGet alone answers those 405.
            ["GET", "HEAD"],
            // The token binds from the query string, never the path — see CalendarUrls.Route.
            (HttpContext http, CalendarEndpoint endpoint, string? t, CancellationToken ct) =>
                endpoint.HandleAsync(http, t, ct)
        )
        .WithRequestTimeout(CalendarEndpoint.Timeout);
}

await app.RunAsync();
