# Quizr

Telegram bot for pub quiz teams: game announcements with a real roster, a reserve queue,
guests, and reminders — instead of counting message reactions.

**Status:** the bot is built (M1–M9) and passing tests. The mini app is phase 2, not started.

## What it does

A team lives in one Telegram chat. A captain posts a game; everyone who wants to play taps
a button. The bot keeps the roster — including who registered first, who brought a guest,
and who has to wait for a seat — and rewrites its own messages whenever any of it changes.

- **Sign-up buttons instead of reactions.** Idempotent, timestamped, and impossible to lose
  by mis-tapping.
- **Seats and a reserve queue.** First come, first served. A freed seat is promoted to the
  next person automatically and held for them.
- **Guests.** Bring a friend in one tap, name them if you like, and they keep their own place
  in the queue.
- **A pinned Board** listing every upcoming game in date order, kept pinned and kept current
  by the bot.
- **Reminders** that fire themselves.
- **Tags as hashtags**, the interim way to find past games via Telegram's own in-chat search,
  until a browsable archive lands with the mini app.
- **English, Russian and German**, with group posts in the team's language and private
  messages in each person's own.

## Documentation

- **[PLAN.md](PLAN.md)** — the data model and the ordered implementation milestones.
- **[VISION.md](VISION.md)** — the idea, how it works, the roadmap, and the decision log.
- **[STACK.md](STACK.md)** — the tools and versions, what is built here rather than taken
  from a library, and what was considered and rejected.
- **[STYLE.md](STYLE.md)** — how code here is written: error handling, interfaces, async,
  comments and tests.
- **[CLAUDE.md](CLAUDE.md)** — working context for agent-assisted development: vocabulary,
  invariants, and the Telegram constraints worth designing around.
- **[DEPLOY.md](DEPLOY.md)** — how the bot ships: the GitHub Actions pipeline and the Coolify
  configuration it hands off to.
- **[CONTRIBUTING.md](CONTRIBUTING.md)** — how to report a bug, ask for a feature, and open a
  pull request.

## Contributing

Bugs, ideas and pull requests are welcome — [open an
issue](https://github.com/Dezzzu/quizr/issues) or read
**[CONTRIBUTING.md](CONTRIBUTING.md)** first.

Note that `main` autodeploys: merging a pull request ships it to the live bot within minutes,
so `main` is protected, work reaches it through a pull request whose `build` check has passed,
and merging is the owner's call rather than the contributor's.

## Stack

.NET 10 · [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot) (long polling) ·
EF Core 10 + PostgreSQL 18 · SmartFormat.NET.

Tested with TUnit, AwesomeAssertions, NSubstitute and Testcontainers.
Full set, and what was rejected, in **[STACK.md](STACK.md)**.

Because the bot long-polls, nothing ever connects to it — no domain, no TLS, no open ports.
It dials out to Telegram and talks to its database.

## Setup

First checkout:

```bash
dotnet tool restore
```

The bot needs a token from [@BotFather](https://t.me/BotFather), a Postgres connection
string, and optionally a chat id to receive unhandled-exception alerts:

```bash
export QUIZR_BOT_TOKEN="..."
export QUIZR_DB="Host=localhost;Database=quizr;Username=quizr;Password=..."
export QUIZR_ALERT_CHAT_ID="..."   # optional
```

Never commit the token — leaked bot tokens are scraped off GitHub within minutes. Locally,
prefer `dotnet user-secrets` over exporting it in a shell.

Migrations run automatically at startup, against whatever `QUIZR_DB` points to — there's no
separate migrate step. `docker-compose.yml` starts a local Postgres 18 for development:

```bash
docker compose up -d
dotnet run --project src/Quizr.App
```

### Running in a container

```bash
docker build -t quizr .
docker run --rm \
  -e QUIZR_BOT_TOKEN="..." \
  -e QUIZR_DB="Host=...;Database=quizr;Username=...;Password=..." \
  quizr
```

The image rebuilds `tzdata` on every build — CLAUDE.md's own warning about `TimeZoneInfo`
drifting silently wrong after a stale image outlives a country's next DST rule change, so
rebuild the image periodically even without a code change.

## License

[MIT](LICENSE).
