# Implementation plan

The repository has docs, a solution skeleton and no domain code. This file is the bridge:
the data model to build against, and the order to build it in.

## How to use this

Read in this order before writing anything:

1. **`CLAUDE.md`** — vocabulary and the twelve invariants. These are bugs when broken.
2. **`STYLE.md`** — error handling, interfaces, async, comments, tests.
3. **`STACK.md`** — what's chosen, what's built here, and what must not be added.
4. This file — the model and the milestones.

`VISION.md` explains *why* and is worth reading once, but nothing in it should be
re-litigated while implementing.

Two standing rules while working through this:

- **Don't invent decisions.** Anything genuinely undecided is listed at the bottom. If
  something else is missing, ask rather than choosing.
- **One milestone per session, and leave it green.** Build clean with warnings as errors,
  csharpier clean, tests passing. `.github/workflows/build.yml` enforces exactly this, so a
  red pipeline means the milestone isn't done.

## Data model

Field-level specification for milestones 1 and 2. **Once the EF entities exist they are the
source of truth and this section is history** — don't maintain it in parallel.

Every id is a `readonly record struct` over `long`: `TeamId`, `PlayerId`, `FranchiseId`,
`GameId`, `SignupId`, plus `TelegramUserId`, `TelegramChatId`, `TelegramMessageId`. See
`STYLE.md` for why.

### Team

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | `TeamId` | |
| `ChatId` | `TelegramChatId` | unique — one chat is one team |
| `Name` | `string` | |
| `TimeZoneId` | `string` | IANA, never an offset |
| `Locale` | `string` | language for **group** messages |
| `EveningBeforeAt` | `TimeOnly` | reminder slot, local time |
| `MorningOfAt` | `TimeOnly` | reminder slot, local time |
| `BeforeStartLead` | `TimeSpan` | reminder slot, before kickoff |
| `BoardMessageId` | `TelegramMessageId?` | the one pinned message |
| `CreatedAt` | `DateTimeOffset` | |

### Player

Global — one row per Telegram user, shared across teams.

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | `PlayerId` | |
| `TelegramUserId` | `TelegramUserId` | unique |
| `DisplayName` | `string` | |
| `Username` | `string?` | |
| `Locale` | `string?` | null falls back to the team's |
| `DmEnabled` | `bool` | true once they've started the bot |
| `CreatedAt` | `DateTimeOffset` | |

### Membership

Per team, because a person in two teams wants different settings and the team owns the
timezone the reminder slots resolve in.

| Field | Type | Notes |
| --- | --- | --- |
| `TeamId`, `PlayerId` | | unique together |
| `IsCaptain` | `bool` | explicit grant; chat admins also count, checked at runtime |
| `EveningBefore` | `ReminderChannel` | `Off` \| `Group` \| `Dm` — default `Off` |
| `MorningOf` | `ReminderChannel` | default `Off` |
| `BeforeStart` | `ReminderChannel` | default `Off` |
| `RemindWhenReserve` | `bool` | default false |
| `JoinedAt` | `DateTimeOffset` | |

### Franchise

Team-scoped. Captains create and edit them; there is no global catalogue.

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | `FranchiseId` | |
| `TeamId` | `TeamId` | unique with `Name` |
| `Name` | `string` | |
| `DefaultVenue` | `string` | |
| `DefaultCapacity` | `int` | |
| `DefaultPrice` | `decimal?` | |
| `Schedule` | `Dictionary<DayOfWeek, TimeOnly>` | `jsonb`; an absent day is one it doesn't run |
| `ArchivedAt` | `DateTimeOffset?` | |
| `CreatedAt` | `DateTimeOffset` | |

### Game

`FranchiseId` is nullable so one-off games work. **A franchise is a template, not a live
reference** — venue, capacity, price and time are copied onto the game at creation and stay
editable there, so editing a franchise never rewrites past games.

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | `GameId` | |
| `TeamId` | `TeamId` | |
| `FranchiseId` | `FranchiseId?` | null for a one-off |
| `Title` | `string` | e.g. `Квиз, плиз! #142` |
| `Venue` | `string` | copied from the franchise, then editable |
| `StartsAt` | `DateTimeOffset` | computed from picked date + schedule time + team zone |
| `Capacity` | `int` | copied |
| `Price` | `decimal?` | copied; display only, never tracked |
| `Notes` | `string?` | |
| `AnnouncementMessageId` | `TelegramMessageId?` | |
| `FinishedAt` | `DateTimeOffset?` | |
| `DeclinedAt` | `DateTimeOffset?` | |
| `LastNudgedAt` | `DateTimeOffset?` | cooldown, not deduplication |
| `CreatedAt`, `CreatedByPlayerId` | | |

**Open and in-progress are not stored.** They are derived from the clock against `StartsAt`
and `FinishedAt`, the same way playing-versus-reserve is derived from the queue.

### Signup

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | `SignupId` | |
| `GameId` | `GameId` | index with `CreatedAt` |
| `PlayerId` | `PlayerId?` | **null means guest** |
| `GuestName` | `string?` | optional for guests, required for team guests |
| `InvitedByPlayerId` | `PlayerId?` | **null on a guest means team guest** |
| `CreatedAt` | `DateTimeOffset` | **queue order — never rewritten** |
| `CancelledAt` | `DateTimeOffset?` | cancellation, never a delete |
| `CancelledByPlayerId` | `PlayerId?` | |

The live roster is `GameId = x AND CancelledAt IS NULL ORDER BY CreatedAt`. Playing versus
reserve is `Take(capacity)` / `Skip(capacity)` **in C#** — see invariant 2.

### Participation

Written when a game finishes. Statistics read these, never signups.

| Field | Type | Notes |
| --- | --- | --- |
| `Id`, `GameId` | | |
| `PlayerId` | `PlayerId?` | null for guests and venue-assigned |
| `Name` | `string?` | for rows with no player |
| `Kind` | `ParticipationKind` | `Member` \| `Guest` \| `TeamGuest` \| `VenueAssigned` |
| `Played` | `bool` | false for reserves who didn't get in |
| `CreatedAt` | `DateTimeOffset` | |

`VenueAssigned` rows are excluded from member statistics.

### Notification

The dedup record. Written in the same transaction as the change that caused it.

| Field | Type | Notes |
| --- | --- | --- |
| `Id`, `SignupId` | | **unique with `Kind`** |
| `Kind` | `NotificationKind` | `ReservePromotion` \| `ReminderEveningBefore` \| `ReminderMorningOf` \| `ReminderBeforeStart` |
| `SentAt` | `DateTimeOffset` | |

Nudges are not here — they're rate-limited by `Game.LastNudgedAt`, not deduplicated.

### AuditEntry

`Id`, `TeamId`, `GameId?`, `ActorPlayerId?` (null = system), `Action` (string),
`Payload` (`jsonb`), `CreatedAt`. This is what makes queue disputes answerable.

### DialogState

`Id`, `TeamId`, `PlayerId`, `ChatId`, `Kind`, `Step`, `Data` (`jsonb`),
`MessageId?`, `CreatedAt`, `UpdatedAt`. Unique on `(ChatId, PlayerId)` — one active dialog
per person per chat. In Postgres so game creation survives a restart.

## Milestones

Each is one session's work and must end green.

### M1 — Domain

`Quizr.Domain` only. **References nothing** and this must stay true.

Build: the id structs; entities as plain classes; `ReminderChannel`, `ParticipationKind`,
`NotificationKind`; `Result<T>` and the `BusinessError` hierarchy; the roster function that
splits an ordered signup list into playing and reserve; the promotion diff that says who
moved up.

Done when: `Quizr.Domain.Tests` covers the roster function hard — capacity boundaries,
guests occupying seats, cancellations, a drop causing exactly one promotion, ties on
timestamp. No mocking library, no database.

Governed by: invariants 1–6, `STYLE.md`.

### M2 — Persistence

Build: `QuizrDb`, configuration for every entity, value converters for the id structs,
`jsonb` mapping for `Schedule`, `Data` and `Payload`, indexes as specified, the first
migration. A `docker-compose.yml` with a dev Postgres.

Done when: a Testcontainers test writes a team, franchise, game and twenty signups, reads
the roster back in order, and the unique constraint on `(SignupId, Kind)` rejects a duplicate.

Governed by: `STYLE.md` (EF section), `CLAUDE.md` conventions.

### M3 — Telegram plumbing and the message layer

No features yet — the machinery everything else sits on.

Build: generic host and configuration from environment; the update dispatcher with a DI scope
per update; the callback-data scheme (compact, ≤64 bytes, documented where it's defined); the
send/edit wrapper with the debouncer; the error boundary and the private-channel alert;
`IStrings` / `IStringsFor` with the JSON loader and an **English** file.

**Set `allowed_updates` explicitly** to `message`, `callback_query`, `my_chat_member` and
`chat_member`. The last one is excluded by default and fails silently — see `CLAUDE.md`.

**Team bootstrap**, which nothing else can happen without:

- Bot added to a group (`my_chat_member`) → create the `Team` from the chat id and title.
  Locale defaults to the language of whoever added it; **the timezone has to be asked**, since
  every `StartsAt` is computed from it.
- Post a setup message prompting for timezone and confirming the language. **Refuse to create
  games until the timezone is set** rather than silently defaulting to UTC.
- Warn if the bot is not an administrator — pinning and `chat_member` both depend on it.
- Bot removed → mark the team inactive. Nothing is deleted, per invariant 7.
- Players and memberships are **created lazily** on first interaction, so the bot works for
  someone who has never spoken before. `chat_member` is used to mark departures, not to
  populate the roster — that way a missed update degrades rather than breaks.

**Localization infrastructure lands here, not at the end.** Locale is a parameter from the
first rendered message — retrofitting it is exactly what `CLAUDE.md` forbids.

Done when: the bot can be added to a fresh group, creates a team, asks for a timezone, and
refuses to create a game until it has one; it responds to `/start`, registers the player
lazily, and a deliberate exception in a handler produces an alert without killing the process.

### M4 — The signup loop

The first genuinely useful thing.

Build: announcement rendering from a roster; **I'm in**, **Bring a friend**, **Can't make it**
with the confirmation step; guest naming; the team-guest choice when an inviter drops;
automatic reserve promotion with its notification record.

Done when: a real game in a real chat can be signed up for, dropped from, and promotes
correctly, with the message rewritten each time and positions visible.

Governed by: invariants 1–7, 11.

### M5 — Board and pinning

Build: the Board message, date-ordered, links to each announcement; pin maintenance and
silent re-pinning; repost from the database if the message is gone.

Done when: unpinning it by hand results in it being pinned again without anyone acting.

Governed by: invariant 12.

### M6 — Scheduler

Build: the `BackgroundService` tick; the three reminder slots resolved against team settings
and each membership's preferences; group batching (one message tagging many, never one each);
DM delivery where `DmEnabled`; auto-finish with participation materialisation; catch-up on
start; pin verification.

Done when: reminders fire once and only once for the right people, a restart mid-window sends
what was missed and skips what is no longer relevant, and a game left alone finishes itself.

Governed by: invariants 8–10, `STACK.md`'s scheduler section.

### M7 — Captain flows

Build: franchise create and edit including the schedule; the game creation dialog
(pick franchise → pick date → confirm) with the one-off path; game editing; the post-game
buttons — **Mark absent · Add player · Not played**; the nudge with its cooldown.

Done when: a captain can run a full game start to finish without a developer.

### M8 — Russian and German

Build: the two locale files; the language setting for teams and for people; the resolution
chain.

Done when: key parity holds across all three files and every plural template has a snapshot
test at 1, 2, 5, 21 and 111.

Governed by: `CLAUDE.md`'s localization section.

### M9 — Closing the v1 feature gap

M1–M8 built the core loop but left several things VISION.md's Bot v1 list — and CLAUDE.md's
own invariant 8 — already promised: reminder opt-in, act-on-behalf-of, decline, an explicit
finish button, captain grant/revoke. `AuditEntry` was scaffolded in M1's data model and never
written to. Tags, listed under VISION.md's "Later," move up because a tag rendered as a real
Telegram hashtag is the interim, no-bot-command way to find past games in chat history until
the mini-app's archive lands.

Build: `/myreminders` (per-slot channel cycling, reserve toggle, self-service); "Manage
players" on a live announcement (captain registers or drops a member on their behalf,
reusing `JoinAsync`/`DropAsync` unchanged); `/managecaptains` (grant/revoke, team-wide);
Decline with a confirm step; an explicit Finish button sharing `GameService.FinishAsync` with
the scheduler's auto-finish; `Game.Tags`, settable alongside the other overridable fields and
rendered as hashtags; an `AuditRecorder` writing `AuditEntry` rows for the actions invariant 13
names, in the same transaction as the change that caused them — including
`ParticipationService.TogglePlayedAsync` (editing a finished game's roster is the exact
dispute this table exists for) and `AddVenueAssignedAsync`, added once the first pass at audit
logging turned out to have missed both.

Done when: a captain can decline, finish early, manage another player's signup, manage
captaincy, and edit a finished game's roster, all without a developer; every reminder slot is
actually reachable by a player; and each of those captain actions leaves an `AuditEntry` row
naming who did it.

Governed by: invariant 13, `CLAUDE.md` Conventions (audit logging), `VISION.md`'s Bot v1
list.

## Deliberately not in scope

A browsable archive UI — M9 built tags rendered as real Telegram hashtags instead, the
interim way to find past games via Telegram's own in-chat search until the mini app's
archive lands. Results and scores, statistics, captain-authored templates, badges, export —
all `VISION.md` **Later**. The mini app is phase 2; see `STACK.md`. Don't build toward any of
them speculatively.

## Still undecided — ask, don't invent

- **Default reminder slot times.** Proposed: 20:00 the evening before, 09:00 the morning of,
  2 hours before kickoff. They're team settings, so defaults are cheap to change.
- **Nudge cooldown.** Proposed: 10 minutes per game.
- **Exact wording of every user-facing string.** English first; the Russian and German
  translations may be machine-generated but must pass the plural snapshot tests.
