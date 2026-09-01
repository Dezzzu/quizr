# Stack

What Quizr is built with, how the repository is laid out, what is built here rather than
taken from a library, and what was considered and rejected.

`CLAUDE.md` holds the rules for writing code. This file holds the choices those rules assume.

## The set

Versions are pinned centrally in `Directory.Packages.props`; the numbers below are what is
in the repo today.

| | | |
| --- | --- | --- |
| Runtime | **.NET 10 LTS**, C# 14 | SDK pinned in `global.json`; supported to Nov 2028 |
| Database | **PostgreSQL 18** | |
| Telegram | `Telegram.Bot` **22.10.3** | long polling |
| Data access | `Npgsql.EntityFrameworkCore.PostgreSQL` **10.0.3**, `Microsoft.EntityFrameworkCore.Design` **10.0.11** | migrations applied at startup |
| Hosting | `Microsoft.Extensions.Hosting` **10.0.11** | generic host; no web server in phase 1 |
| Resilience | `Microsoft.Extensions.Http.Resilience` **10.9.0** | Polly 8; retries honouring `retry_after` |
| Localization | `SmartFormat.NET` **3.6.1** | JSON string files |
| Tests | `TUnit` **1.65.68**, `AwesomeAssertions` **9.6.0**, `NSubstitute` **6.2.0**, `Testcontainers.PostgreSql` **4.14.0**, `Microsoft.Extensions.TimeProvider.Testing` **10.9.0** | source-generated, native to Microsoft.Testing.Platform — see below |
| Formatter | `csharpier` **1.3.0** | local tool; print width 120 |
| Migrations CLI | `dotnet-ef` **10.0.11** | local tool |
| Packaging | Docker | `tzdata` must be present in the image |

### Why .NET 10 specifically

.NET 8 leaves support in November 2026, and .NET 9 was a short-term release that is already
out of support. .NET 10 is LTS through November 2028. It also settles EF Core 10 and Npgsql
10, since those track the runtime's major version.

### No inbound connectivity

Long polling means **nothing ever connects to the bot** — no domain, no TLS, no open ports.
It dials out to Telegram and talks to its database. Don't add a dependency that breaks that
without flagging it.

Phase 2 (the mini app) will need ASP.NET Core. Switching from the generic host to
`WebApplication` is a few lines and the hosted services carry over unchanged, so build for
the generic host now.

## Repository layout

```
Quizr.slnx                    XML solution format
├── src/
│   ├── Quizr.Domain          entities and roster logic — references nothing
│   └── Quizr.App             EF Core, Telegram, hosting, rendering, composition root
└── tests/
    ├── Quizr.Domain.Tests    pure and fast; no Docker, no mocks
    └── Quizr.App.Tests       Testcontainers; needs Docker
```

**`Quizr.Domain` references nothing on purpose** — no EF Core, no Telegram.Bot. Invariant 2
says roster logic is pure functions over an ordered list, testable with no database. An empty
reference list makes that structurally impossible to violate: you cannot reach for the
`DbContext` in roster logic if the reference does not exist.

**`Quizr.Domain.Tests` deliberately has no NSubstitute.** If a mock ever seems necessary
there, something has leaked into the domain that shouldn't be there.

The test projects are split by **speed**, not by layer. Domain tests run in milliseconds and
can be run on every save; app tests start a real Postgres container and take seconds.

`Infrastructure` is deliberately *not* a separate project. With one host and one consumer it
would buy ceremony and no isolation.

## Phase 2: what the mini app changes

Recorded so it doesn't have to be re-derived. Nothing here needs doing until the app is real.

**The structure barely moves.** `Quizr.App` swaps `Host.CreateApplicationBuilder` for
`WebApplication.CreateBuilder` and gains endpoints; the bot stays a hosted service and carries
over unchanged. Four new pieces live inside it, none needing a project of their own:

- **initData validation** — HMAC-SHA256 over the payload using the bot token. No dependency,
  and it is the entire auth story: a mini app has no login.
- **A JSON API** for games, rosters and franchises.
- **The iCal feed** — a per-user secret URL, rotatable. `Ical.Net`, or hand-rolled VEVENT.
- **Static file serving** for the built frontend.

`Quizr.App.Tests` gains `WebApplicationFactory` integration tests. Same project — it is
already the slow, container-using one.

**The frontend needs a home, and it is not a `.csproj`:**

```
web/          Vite + TypeScript, its own package.json
  └── dist/ → copied into Quizr.App/wwwroot at publish
```

Two decisions come with it: whether an MSBuild target runs the frontend build during publish
or CI builds them separately, and whether to generate TypeScript types from the API. .NET 10
emits OpenAPI documents natively, so `openapi-typescript` or NSwag gives a typed client.

**Don't split into `Quizr.Bot` / `Quizr.Web` / `Quizr.Infrastructure`.** It is the instinct,
and it buys compile-time dependency enforcement — but the only boundary worth enforcing is
`Quizr.Domain`, which already exists. Folders inside `Quizr.App` (`Telegram/`, `Web/`,
`Data/`) do the organising without four more project files and a host-ownership question.

**Both front doors call the same application services.** "Sign up for game 142" is one method
that the Telegram handler and the HTTP endpoint both invoke, never reimplemented per surface.
Get this wrong and the invariants in `CLAUDE.md` hold on one path but not the other — a class
of bug that stays invisible until someone's queue position is wrong.

### What actually gets harder

**The bot stops being outbound-only.** Today "nothing ever connects to it" buys no domain, no
TLS, no open ports and effectively no attack surface. The mini app requires all of them —
a domain, certificates, a reverse proxy, and a public endpoint that has to be correct about
auth. That is a larger cost than any project reshuffle, and it should be weighed before
starting rather than discovered during.

**Stay single-process.** Bot and web in one host keeps startup migrations and the in-process
edit debouncer valid. Two processes breaks both — see the revisit table — and "just deploy the
API separately" is an easy way to drift into it without noticing.

## Repository tooling

| File | Purpose |
| --- | --- |
| `global.json` | Pins the SDK so this machine and CI can't drift |
| `Directory.Build.props` | Shared `TargetFramework`, nullable, implicit usings, warnings-as-errors, `EnforceCodeStyleInBuild`, and `InvariantGlobalization=false` (three languages and IANA zones both need real ICU data) |
| `Directory.Packages.props` | Central package management, with transitive pinning |
| `.config/dotnet-tools.json` | csharpier and dotnet-ef pinned per repo — `dotnet tool restore` on first checkout |
| `.csharpierrc.json` | Print width 120, matching `max_line_length` in `.editorconfig` |
| `.editorconfig` | Naming rules and analyzer severities. csharpier owns layout, so its C# *formatting* entries are IDE hints only |

## Running things

```bash
dotnet tool restore                              # first checkout
dotnet build
dotnet test                                      # both projects
dotnet run --project tests/Quizr.Domain.Tests    # or just one — fast, no Docker
dotnet run --project tests/Quizr.App.Tests       # or just one — needs Docker
dotnet csharpier format .                        # or: check .
```

### Testing on TUnit, not xUnit

`global.json` pins `"test": { "runner": "Microsoft.Testing.Platform" }` — .NET 10's own test
runner, not the legacy VSTest bridge. xUnit v3 sat on top of that runner through a compat
shim, which is what produced the **"Zero tests ran"** failure this repo used to have to work
around (an untouched `dotnet new xunit3` template failed identically on SDK 10.0.111). TUnit
is source-generated and native to Microsoft.Testing.Platform — no shim, no discovery step at
runtime — so both `dotnet test` and `dotnet run --project` work here without a workaround.

**TUnit runs tests within a class in parallel by default** — xUnit ran them sequentially.
Most classes are unaffected, since each test already seeds rows under its own chat/game id.
Two hazards this surfaced during the migration, worth knowing before adding a test:

- A test class whose tests touch data **not scoped to their own seed** needs
  `[NotInParallel]`. `SchedulerServiceTests` is the one case today — `RunTickAsync` processes
  every team in the database, not just the one a test seeded, so two tests' tick calls could
  otherwise race over each other's teams.
- A row's identifying value (a `TelegramUserId`, say) must be **actually unique**, not just
  "unique enough under serial execution" — a scheme derived from `DateTimeOffset.UtcNow` looks
  fine sequentially but can collide when two calls a few instructions apart land in the same
  clock tick under parallel load. Use a counter or a distinct literal per test instead.

A missing `.ThenBy(id)` tiebreaker on an `OrderBy(createdAt)` query is the same hazard from
the other direction: two rows with count identical timestamps (a `FakeTimeProvider` that
never advances, or a batch insert stamped with one `now`) sort in a database-decided,
un-guaranteed order — invariant 1 already calls for the tiebreaker in production code, and
the domain model's own `Roster.Split` does it in memory. `LoadLiveGuestsAsync` in
`SignupService` was missing it in the database-side query; parallel execution's extra
concurrent writes are what turned that gap into an actually-visible flake.

## Built here on purpose

Each of these is small, specific to this app, and avoids a dependency sitting somewhere
structural. Don't replace one with a library without a real reason.

- **Update dispatch** — switch on update type, match callback-data prefixes to DI-resolved
  handlers. ~200 lines.
- **Dialog state** for the game-creation flow, persisted in Postgres so it survives restarts.
- **The scheduler** — see below.
- **The edit debouncer** — coalesce a burst of signups into one message edit, respecting the
  per-group rate limit.
- **Message rendering** — interpolated strings and a function per message type. No templating
  engine.
- **The alert path** — unhandled exception to a private channel.
- **`Result<T>` and the `BusinessError` hierarchy** — about twenty lines. See below for why no
  library fits.
- **Strongly-typed IDs** — `readonly record struct` per id, with EF Core value converters.

### The scheduler, specifically

Reminders, auto-finish and pin verification are a `BackgroundService` ticking every 30–60
seconds and **asking what is due now**:

```sql
WHERE reminder_due_at <= now() AND reminder_sent_at IS NULL
```

This is deliberately not a job queue. A query is idempotent, gives restart catch-up for
free, and needs no reconciliation when a captain moves a game to a different evening —
the next tick simply asks again. A queue would require finding and cancelling scheduled
jobs on every edit, which is pure bug surface.

**On start, catch up**: send reminders that came due while the process was down and are still
relevant, and finish games whose 4-hour window elapsed. Uptime is then not a correctness
requirement.

## Don't reach for these

Considered and rejected. If one seems necessary, raise it rather than adding it.

| Not this | Because |
| --- | --- |
| MediatR, or any mediator/pipeline | Hard to navigate, and manual dispatch is cheap when no middleware behaviour is wanted. Also commercially licensed now. |
| Hangfire, Quartz, TickerQ | They model "run this job at this time". Scheduling here is a query, not a queue. |
| StyleCop.Analyzers | ~200 rules, many pedantic, and its layout rules fight csharpier. Built-in analyzers with `EnforceCodeStyleInBuild` cover what actually catches bugs. |
| `IStringLocalizer` / `.resx` | Resolves language from ambient `CurrentUICulture`, which is the wrong model for a bot that renders for other people. No plural support. |
| FluentAssertions | Commercially licensed since v8. Use AwesomeAssertions. |
| Moq | Use NSubstitute. |
| Serilog | The built-in `ILogger` plus the OpenTelemetry exporter already delivers to Seq the thing Serilog is usually reached for: each call site's message template with its named properties indexed, not a rendered sentence. Serilog would sit behind `ILogger<T>` regardless, so no call site changes and the whole gain is sink features — bought with a second logging pipeline running beside the one already exporting metrics. See "When to revisit" for the one feature that would justify it. |
| Telegram bot frameworks (Deployf.Botf and similar) | Small, often stale, and they sit structurally in the middle of the app. |
| MarkdownV2 | Escaping hazard. Use HTML parse mode. |
| OneOf | Per-operation unions by construction, so cross-cutting failures like "not a captain" get repeated in every signature. |
| ErrorOr, FluentResults | Stringly-typed errors (code plus description), which kills exhaustive switching and turns the message-key mapping into a string lookup. |
| .NET Aspire | Its value is orchestrating several services; this is one process and one database, deliberately. Service discovery and health checks buy nothing here, `ServiceDefaults` would own a startup we want to understand, and the AppHost is dev-time only — so the whole benefit is local, where one Postgres container is needed. Two more projects and a fast-moving dependency to replace fifteen lines of `docker-compose.yml`. The dashboard is the one part genuinely worth missing. |

## When to revisit

Nothing above is permanent. These are the specific triggers that should reopen a choice.

| Trigger | What changes |
| --- | --- |
| The mini app arrives | Generic host becomes `WebApplication`. Hosted services carry over unchanged. |
| Phase 2 turns into genuinely separate services rather than one host | Reconsider .NET Aspire. Its orchestration and dashboard start earning their keep once there is more than one thing to run, and the objection above is entirely about there being one. |
| You want to change log level without a redeploy | Reconsider Serilog. `Serilog.Sinks.Seq`'s `controlLevelSwitch` is the one capability OTLP has no answer for: Seq pushes a level change down to the running process, so Debug can be turned on from the UI, an update watched, and it turned back off. Durable disk buffering while Seq is unreachable comes along with it. Both cost a second logging pipeline, so wait until an incident has actually made you want them. |
| Scheduling grows teeth — backoff, one-off jobs, an operational dashboard | TickerQ becomes the right library to reach for: EF Core-backed, so no separate storage or migration story. |
| A second process appears | Two assumptions break — migrations applied at startup, and the in-process edit debouncer. Both need rethinking *before* a second instance exists, not after. |
| A fourth language with unfamiliar plural rules | Revisit SmartFormat against ICU MessageFormat. SmartFormat's plural forms are positional, so a wrong form order can't be checked automatically; ICU's named CLDR categories can. Until then the snapshot tests in `CLAUDE.md` cover it. |
