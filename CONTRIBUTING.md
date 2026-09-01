# Contributing

Thanks for looking. Quizr is a small project with a real user base — one Telegram bot,
several quiz teams, and no staging environment.

**Read this first, because it changes how a merge feels:**

> Merging a pull request to `main` deploys it to the live bot within minutes. There is no
> approval gate after the merge, no staging soak, and no "we'll ship it Tuesday".
> `.github/workflows/build.yml` builds the image, pushes it to GHCR, and pokes Coolify, which
> restarts the container. The full pipeline is in **[DEPLOY.md](DEPLOY.md)**.

Everything below follows from that.

## Reporting a bug or asking for a feature

Open an issue: **<https://github.com/Dezzzu/quizr/issues>**. The bot's `/help` links here too.

You do not need to know C# to file a good issue. What makes one useful is the same thing that
makes it reproducible.

### For a bug

Include, as far as you can:

- **What you tapped or typed**, in order — "posted /newgame, picked Квиз, плиз!, picked the
  12th, tapped Create".
- **What the bot did**, and what you expected instead. A screenshot of the message is worth
  more than a description of it, because the bot's own text is the evidence.
- **Roughly when**, with a timezone. The bot logs to stdout on the server, and a timestamp is
  what makes those logs findable.
- **Group or DM**, and the team's language. A surprising number of bugs are one locale's
  template, one word order, or one plural form — and never show up in English.
- **Whether it survived**. Because the database is the source of truth and messages are
  generated views (see [CLAUDE.md](CLAUDE.md)), a wrong-looking message often corrects itself
  on the next change. "It fixed itself when someone else joined" is a real and useful clue.

Please don't paste a bot token, a database connection string, or a `t.me/c/...` link to a
private chat you'd rather not share.

### For a feature

Say what you were trying to do and what got in the way, not just the button you'd like added.
The design decisions already taken are recorded in **[VISION.md](VISION.md)** — the decision
log at the bottom, plus the questions that were settled and how — so an idea that was
considered and rejected usually has its reasoning written down.

Some things are **deliberately out of scope**: money and debt tracking, lineup building and
player strengths, photos, and DMs as the primary notification channel. The full list, with
reasons, is at the bottom of [CLAUDE.md](CLAUDE.md). An issue arguing that one of those was
decided wrongly is welcome; a pull request quietly implementing one is not.

## Getting set up

You need the .NET SDK pinned in `global.json` and Docker (for Postgres, and for the
Testcontainers the tests use).

```bash
dotnet tool restore          # first checkout only
docker compose up -d         # local Postgres 18
dotnet build
dotnet test                  # both test projects
dotnet csharpier format .    # or: dotnet csharpier check .
```

The bot needs a token from [@BotFather](https://t.me/BotFather) and a connection string, both
from environment variables — never a config file, never the repo. `README.md` has the full
list. Locally, prefer `dotnet user-secrets` over exporting the token in a shell: leaked bot
tokens are scraped off GitHub within minutes.

Migrations run automatically at startup against whatever `QUIZR_DB` points to. There is no
separate migrate step, locally or in production.

## Before you write code

Four files carry the decisions, and reading them saves re-deriving what was already settled:

| File | What it holds |
| --- | --- |
| **[CLAUDE.md](CLAUDE.md)** | Vocabulary, the invariants, and the Telegram constraints. The shortest path to understanding why the code is shaped this way. |
| **[PLAN.md](PLAN.md)** | The data model and the milestones. |
| **[STYLE.md](STYLE.md)** | Error handling, interfaces, async, comments, tests — the conventions to match. |
| **[STACK.md](STACK.md)** | The tools and versions, what is hand-rolled here, and what was rejected. |

Two rules are worth repeating here because breaking either is a bug rather than a preference:

- **The database is the source of truth; chat messages are generated views.** Never store
  state in a Telegram message and never read it back out of one.
- **Playing vs reserve is derived, never stored.** Queue order is `created_at` ascending, and
  nothing renumbers it.

The numbered invariants in CLAUDE.md are the rest. If a change seems to need one relaxed,
that's an issue to open, not a line to edit.

## Opening a pull request

`main` is protected. Nothing is pushed to it directly, by anyone — the repository owner
included.

1. Branch off `main`.
2. Make the change, with tests. `dotnet test` and `dotnet csharpier check .` both have to pass,
   and `Directory.Build.props` sets `TreatWarningsAsErrors`, so an analyzer complaint fails the
   build like an error.
3. Open the pull request. The `build` check runs on every PR and must be green before it can
   merge.
4. **Stop there.** Merging is the owner's call, because the merge is the deploy — it's the last
   point at which a human sees the change before players do.

In the description, say what a user will notice. "Adds a Title button to the confirm screen" is
more useful to whoever is deciding whether to ship it than "adds `OverrideTitle = 5`".

## Things that bite, specific to this project

**A deploy restarts the bot mid-conversation.** Someone is always halfway through a wizard.
Dialog state lives in the database (`DialogState`) and survives the restart, which is what makes
that safe — but only as long as the state it holds still means the same thing to the new code.
The field indices in `NewGameDialogData` and `EditGameDialogData` are persisted as integers
inside that JSON, so **append a new one, never renumber**: a reordering silently repoints every
dialog left open across the deploy that did it.

**A migration is harder to undo than a deploy.** Rolling back means pointing Coolify at an older
image tag (see DEPLOY.md), and that does not roll back the schema. Prefer additive migrations
that the previous image could also have lived with.

**Three locales, always.** English, Russian and German are first-class. Every key must exist in
all three files under `src/Quizr.App/Localization/Strings/` — `StringsTests` compares the loaded
key sets and fails the build otherwise. Machine translation is fine; AI-generated translations
are explicitly allowed. Two habits keep it honest:

- **Never concatenate user-visible text.** One template per sentence, with placeholders, or
  Russian word order will break in a way English never reveals.
- **Plural templates are snapshot-tested at 1, 2, 5, 21 and 111.** SmartFormat's plural forms
  are positional, so a wrong form order is otherwise undetectable — this is the check that
  catches a bad translation.

**Callback data is capped at 64 bytes.** Buttons carry a compact `verb:id` pair
(`CallbackData.cs`), never serialized JSON. A new verb is one character, and the file lists
which are taken.

**Tests run in parallel within a class.** A test class touching shared state — anything whose
subject sweeps every team in the database, like `SchedulerServiceTests` — needs
`[NotInParallel]`, and every seeded row still needs a chat id and game id unique to its own
test.

**A red CI blocks everyone, including the owner.** That's the point of the branch protection, and
it's also the thing to remember on the day CI itself is what's broken.

## License

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE),
same as the rest of the project.
