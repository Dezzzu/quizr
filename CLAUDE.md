# Quizr

A Telegram bot that runs game sign-ups for a pub quiz team, replacing message reactions
with a roster the bot owns.

**This file is the working context**: vocabulary, the rules that must hold, and conventions.
Read it before designing anything — most of what follows was decided deliberately and is not
worth re-deriving.

- **`STACK.md`** — the chosen tools and versions, what is built here rather than taken from a
  library, and what was considered and rejected.
- **`VISION.md`** — the product description, roadmap and decision log.

## Status

Project layout and tooling are in place; no domain code yet. The product description and the
stack are settled.

## Commands

```bash
dotnet tool restore                              # first checkout
dotnet build
dotnet run --project tests/Quizr.Domain.Tests    # fast: pure domain, no Docker
dotnet run --project tests/Quizr.App.Tests       # slower: Testcontainers needs Docker
dotnet csharpier format .                        # or: dotnet csharpier check .
```

**Don't use `dotnet test`.** It reports "Zero tests ran" with xUnit v3 on SDK 10.0.111 — an
untouched `dotnet new xunit3` template fails identically, so it's an SDK issue rather than
anything in this repository. The assemblies discover and run their tests correctly when
executed directly, which is what `dotnet run --project` does. See `STACK.md`.

## The rule everything else follows

**The database is the source of truth. Chat messages are generated views.**

Never store state in a Telegram message and never read state back out of one. Every button
press writes to the database; the bot then rewrites whatever messages that change affects.
This single arrangement is why a lost pin, a deleted post or a mis-tap costs nothing.

## Vocabulary

Use these words in code, comments and user-facing text. They come from how the team
actually talks about quiz nights.

| Term | Meaning |
| --- | --- |
| **Team** | One Telegram chat is one team. Owns its games, franchises, captains, timezone and language. A person may belong to several teams; calendars stay separate. |
| **Captain** | Chat admins by default, plus explicit grant/revoke. Can do anything a player can do, **on that player's behalf**. |
| **Franchise** | A recurring quiz brand (Квиз, плиз!, Мозгобойня). Carries default venue, capacity, price, and a **schedule**. |
| **Schedule** | A franchise's map from day of week to start time, e.g. `{ Mon–Fri: 19:00, Sat: 16:00, Sun: 16:00 }`. An absent day is one the franchise doesn't run. |
| **Game** | One quiz night: franchise, number or title, venue, start time, capacity, price, tags, notes. |
| **Announcement** | The bot's message for a single game, carrying the sign-up buttons. Rewritten on every change. |
| **Board** | The one pinned message per chat, listing upcoming games sorted by date. The *only* pinned message. |
| **Signup** | Someone holding a place in a game. Its creation timestamp determines queue order, permanently. |
| **Playing / Reserve** | The first `capacity` signups are playing; everyone after them is the reserve. Derived, never stored. |
| **Participation** | Written when a game finishes: one row per person, recording whether they played and whether they attended. Statistics read these; captains edit these. |
| **Member** | A person in the chat. |
| **Guest** | Brought by a member. Anonymous by default, optionally named. Occupies a seat and holds its own queue position. |
| **Team guest** | A guest who stays after their inviter drops out. Has no owner. Must be named. |
| **Venue-assigned** | A stranger the organisers add to the team on the night. Recorded after the fact. |

## Invariants

Breaking one of these is a bug, not a preference.

1. **Queue order is `created_at`, ascending.** Never renumber, never reorder.
2. **Playing vs reserve is derived, never stored — and derived in C#, not SQL.** A signup
   records who, which game and when. Rendering any message loads the whole ordered roster
   anyway, so the split is `Take(capacity)` / `Skip(capacity)` over that list. This is what
   makes the last seat safe: two people tapping at the same moment simply get two
   timestamps, and the ordering settles it. Don't add a status column that duplicates the
   split, and don't push it into a window function — in memory it stays unit-testable with
   no database at all.
3. **Dropping out cancels the signup entirely.** Re-registering creates a new signup with a
   new timestamp, at the back of the queue — often the reserve. This is intended.
4. **Guests occupy seats** and hold their own queue position. There is no limit on how many
   a member may bring; the team is trusted to be fair.
5. **A guest whose inviter drops may stay only if named.** Otherwise they are cancelled with
   the inviter. An ownerless anonymous guest is a person nobody can identify at the door.
6. **Reserve promotion is automatic**, notifies the promoted person, and holds the seat
   indefinitely. No timers. If they go quiet, a captain removes them by hand.
7. **Nothing is ever deleted.** Cancellation is a state change. The audit trail is what makes
   queue disputes answerable.
8. **A game auto-finishes 4 hours after its start time.** Captains can finish it early with an
   explicit button. Until then it is live and players can still self-serve.
9. **A finished game counts as played unless declined**, and every participant counts as
   attended unless a captain says otherwise. The ordinary case requires zero input.
10. **Finishing a game materialises participation.** Until then the roster is derived from
    signup order. At finish, write one participation row per person — played or didn't,
    attended defaulting to true. Statistics read those rows, never the signups.
11. **Captains can edit a roster forever**, including long after the game. Before a game
    finishes that means the signups; afterwards it means the participation rows, and the
    signups become immutable history. That split is what lets a captain record "Лена played
    instead of Костя" without falsifying a timestamp, and it stops a later capacity change
    silently rewriting what already happened.
12. **Only the Board is pinned.** The bot verifies the pin and restores it silently, reposting
    from the database if the message is gone.

## Telegram constraints to design around

- **A bot cannot message anyone who has not started it.** The group chat, with mentions, is
  the default notification channel. Do not design a flow that assumes DMs work.
- The bot **must be a chat admin** to pin and to see messages that aren't commands.
- **~20 messages per minute per group.** Debounce message edits; batch reminders into one
  message rather than one per person.
- **Callback data is capped at 64 bytes.** Use a compact scheme (`j:142`, `g:142`, `d:142`),
  never serialized JSON.
- **Send with HTML parse mode, not MarkdownV2.** MarkdownV2 requires escaping
  ``_*[]()~`>#+-=|{}.!`` and it will eventually break on somebody's name. HTML needs three
  escapes.
- `web_app` inline buttons are **private-chat only**. In groups, use a direct app link
  (`t.me/quizr_team_bot/<app>?startapp=<id>`) as a normal URL button. Phase 2 concern.
- Deep-link payloads are ≤64 chars from `A-Za-z0-9_-` and are **user-editable**. They carry an
  id, never data, and permission is always checked server-side.

## Time

Native BCL types throughout.

| Concept | Type | Postgres |
| --- | --- | --- |
| A game's actual start | `DateTimeOffset` | `timestamptz` |
| Franchise schedule times | `TimeOnly` | `time` |
| The date a captain picks | `DateOnly` | `date` |
| Team timezone | IANA id string + `TimeZoneInfo` | `text` |
| Clock | `TimeProvider` / `FakeTimeProvider` in tests | — |

- **All conversions live behind a `TeamTime` service.** `ConvertTime`, `GetUtcOffset` and
  local-date arithmetic appear in exactly one file, with tests. Nowhere else.
- **A game's start is computed fresh** from picked date + schedule time + team zone, never
  derived from another game's instant. There is no date arithmetic in this domain, which is
  what makes native types safe here.
- **Store the computed instant**, not the local date and time. The scheduler needs an instant.
- **Store the team's IANA id, never an offset.** An offset isn't a timezone, and Postgres
  discards it anyway.
- `tzdata` must be present in the container image, and the image rebuilt periodically.
  `TimeZoneInfo` reads the operating system's timezone database, so a stale image produces
  wrong offsets after a country changes its DST rules — silently, with no error.

## Localization

**First-class from v1: English, Russian, German.** Translations may be AI-generated.

- **Locale is a parameter, never ambient.** Resolve it at the boundary, then bind it:
  `IStrings.For(locale)` returns an `IStringsFor`, and render functions take `IStringsFor`.
  A single operation routinely renders in two locales — rewriting a group post in the team's
  language while DMing a promoted player in theirs — so an ambient "current culture" is the
  wrong model and would produce silent, hard-to-see bugs.
- **Group messages use the team's language; DMs and the app use the person's own.**
- **Resolution order:** explicit user choice → Telegram `language_code` → team default →
  English.
- **Never concatenate user-visible text.** One template per sentence, with placeholders, or
  Russian word order will break in ways English never reveals.
- **Test key parity** — every key present in every locale file.
- **Snapshot-test plural templates** at 1, 2, 5, 21 and 111. SmartFormat's plural forms are
  positional, so a wrong form order is otherwise undetectable — this is the check that
  catches a bad machine translation.

## Conventions

- **All configuration comes from environment variables** — bot token, connection string,
  log level. No hardcoded paths, no per-environment `appsettings` files to edit.
- **The bot token never enters the repo.** Use .NET user secrets locally, `QUIZR_BOT_TOKEN`
  and `QUIZR_DB` in deployment.
- **Log to stdout**, and **always with structured message templates**:
  `LogInformation("Promoted {UserId} to game {GameId}", userId, gameId)` — never an
  interpolated string. Adding an aggregator later is then a composition-root change instead
  of a rewrite of every call site.
- **Open a logging scope per update** carrying update id, chat id and user id.
- **On an unhandled exception, message a private channel.** Logs say what happened; this
  says it now.
- **Store enums as `int` or `text` through an EF conversion**, not as native Postgres enums.
  Native enums need `ALTER TYPE` to gain a value and EF's migration support for them is
  awkward. Game states will gain members.
- **Record every notification you send; don't reach for a lock.** A promotion ping is worked
  out by diffing the roster before and after a change, so two near-simultaneous changes can
  both conclude the same person moved up, and a crash mid-send can repeat the message on
  restart. One mechanism solves both: a notifications table keyed `(signup_id, kind)` with a
  unique constraint, written in the same transaction as the change that caused it. A
  duplicate becomes a rejected insert rather than a second message. **There are no locks in
  this system** — if you find yourself wanting one, the derived-state rule is being broken
  somewhere.
- Bot handle: **@quizr_team_bot**. Display name: **Quizr**.

## Out of scope — do not build

Parked deliberately. If a change seems to need one of these, raise it rather than adding it.

- Money or debt tracking. Price is a display field; money is settled at the venue.
- Lineup building, player strengths, team balancing. Capacity is a venue limit and the rule
  is first come, first served.
- Photos, a memory wall.
- DMs as the primary notification channel.
- Badges and full data export — parked rather than rejected; see `VISION.md`.
