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
| Tests | `xunit.v3` **4.0.0**, `AwesomeAssertions` **9.6.0**, `NSubstitute` **6.2.0**, `Testcontainers.PostgreSql` **4.14.0**, `Microsoft.Extensions.TimeProvider.Testing` **10.9.0** | |
| Formatter | `csharpier` **1.3.0** | local tool; print width 120 |
| Migrations CLI | `dotnet-ef` **10.0.5** | local tool |
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
dotnet run --project tests/Quizr.Domain.Tests    # fast
dotnet run --project tests/Quizr.App.Tests       # needs Docker
dotnet csharpier format .                        # or: check .
```

### `dotnet test` does not work here

It reports **"Zero tests ran"** with xUnit v3 on SDK 10.0.111. This is not a problem with
this repository — an untouched `dotnet new xunit3` template fails identically, while xUnit v2
works fine. The MTP server-mode handshake between `dotnet test` and the test host dies during
host construction; the test assemblies discover and run their tests correctly when executed
directly.

Run tests with `dotnet run --project` until this is fixed upstream.

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
| Serilog | Built-in `ILogger` with a JSON console is enough. Add OpenTelemetry if aggregation is ever wanted. |
| Telegram bot frameworks (Deployf.Botf and similar) | Small, often stale, and they sit structurally in the middle of the app. |
| MarkdownV2 | Escaping hazard. Use HTML parse mode. |

## When to revisit

Nothing above is permanent. These are the specific triggers that should reopen a choice.

| Trigger | What changes |
| --- | --- |
| A new SDK feature band lands | Re-test `dotnet test` with xUnit v3. If it works, drop the `dotnet run` workaround from the docs. |
| The mini app arrives | Generic host becomes `WebApplication`. Hosted services carry over unchanged. |
| You want log aggregation | Add OpenTelemetry at the composition root and point OTLP wherever you like. Call sites are already structured, so nothing else moves. |
| Scheduling grows teeth — backoff, one-off jobs, an operational dashboard | TickerQ becomes the right library to reach for: EF Core-backed, so no separate storage or migration story. |
| A second process appears | Two assumptions break — migrations applied at startup, and the in-process edit debouncer. Both need rethinking *before* a second instance exists, not after. |
| A fourth language with unfamiliar plural rules | Revisit SmartFormat against ICU MessageFormat. SmartFormat's plural forms are positional, so a wrong form order can't be checked automatically; ICU's named CLDR categories can. Until then the snapshot tests in `CLAUDE.md` cover it. |
