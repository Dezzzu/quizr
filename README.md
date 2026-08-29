# Quizr

Telegram bot for pub quiz teams: game announcements with a real roster, a reserve queue,
guests, and reminders — instead of counting message reactions.

**Status:** pre-implementation. The product description is settled; no code yet.

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
- **An archive** of everything the team has ever played, with attendance assumed rather than
  collected.

## Documentation

- **[VISION.md](VISION.md)** — the idea, how it works, the roadmap, and the decision log.
- **[CLAUDE.md](CLAUDE.md)** — working context for agent-assisted development: vocabulary,
  invariants, and the Telegram constraints worth designing around.

## Stack

.NET 8 · [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot) (long polling) ·
EF Core + PostgreSQL.

Because the bot long-polls, nothing ever connects to it — no domain, no TLS, no open ports.
It dials out to Telegram and talks to its database.

## Setup

Not yet. When there is something to run, it will want a bot token from
[@BotFather](https://t.me/BotFather):

```bash
export QUIZR_BOT_TOKEN="..."
export QUIZR_DB="Host=localhost;Database=quizr;Username=quizr;Password=..."
```

Never commit the token — leaked bot tokens are scraped off GitHub within minutes.

## License

[MIT](LICENSE).
