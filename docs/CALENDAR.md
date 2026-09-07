# Calendar feeds

Each player gets one personal `.ics` subscription URL listing the games they are rostered for.
They paste it into Google or Apple Calendar once and it stays current.

This file is the design and the live implementation plan. **It describes the system, not the
intention** — when implementation forces a different decision, this file changes in the same
commit.

`CLAUDE.md` holds the invariants, `STACK.md` the tool choices, `STYLE.md` the conventions.
Nothing here overrides those; where this feature *changes* one of them, it says so.

## What this changes about the project

Three standing claims stop being true, and each is load-bearing somewhere:

| Claim | Where | What replaces it |
| --- | --- | --- |
| "Nothing ever connects to the bot — no domain, no TLS, no open ports" | `STACK.md`, `README.md`, `VISION.md` phase 1 | One public `GET` endpoint, behind Coolify's proxy, on a domain. The bot still long-polls; the endpoint is read-only and touches no Telegram API. |
| Generic host, no web server in phase 1 | `STACK.md` | `WebApplication`, which `STACK.md` already predicted costs "a few lines" with hosted services carrying over unchanged. That prediction held. |
| The phone-calendar feed is a phase 2 / **Later** item | `VISION.md` | Pulled forward. It needs none of the mini app — no initData validation, no JSON API, no frontend. |

**The health check is the trap, and this feature does not touch it.** `DEPLOY.md` explains
that Coolify rolling updates require a configured, passing health check, and that the bot
deliberately has none — which is the only thing stopping two containers from running at once.
Now that an HTTP port exists, adding one becomes the obvious tidy-up. **Do not.** Exposing the
port is safe; configuring a health check is not.

What an overlap actually costs, checked against the code rather than `DEPLOY.md`'s wording:
reminders and reserve promotions are already safe, because `Notification`'s unique constraint on
`(SignupId, Kind)` rejects the duplicate; **auto-finish is not**, since `GameService.FinishAsync`
has no such guard and would materialise `Participation` rows twice; and Telegram answers one
poller `409` until the other exits, which clears on its own rather than lasting forever.

## Not in this feature

Named so they are not re-derived, and not re-litigated:

| Kept out | Why |
| --- | --- |
| **Health-check endpoints** | Considered, and deliberately not here. They are worth having — `/health/ready` asserting that the scheduler has ticked recently is `quizr.scheduler.ticks` expressed as a probe, and pointed at an *external* monitor it closes the gap `DEPLOY.md` names as open, since Seq runs on the same VPS as the bot. But they are a deployment concern, not a calendar one, and they arrive tangled with the rule above. Their own PR. |
| **A singleton guard** (a Postgres advisory lock, so a second container waits instead of polling) | The thing that would make Coolify's health check safe to turn on, and therefore the thing that has to land *before* `DEPLOY.md`'s central rule can flip. A deployment-policy change deserving its own review, not a paragraph inside a feature. |
| **Team-level caching, background pre-rendering** | See §5. Revisit if a roster ever runs to hundreds. |
| **Two-way sync, CalDAV write access, editing a game from a calendar client** | The feed is a projection. The bot's database stays the source of truth, and a calendar client is not a second front door onto it. |
| **Email invitations — `METHOD:REQUEST`, iTIP, iMIP, RSVP over email** | Sign-up lives on the announcement's buttons. A second RSVP channel would need reconciling with the queue, and invariant 1 says queue order is `created_at` and nothing else. |
| **A team-wide shared calendar** | Per-player only. A team calendar is the Board's job, and it already exists. |
| **Any admin UI** | Captains manage rosters in Telegram. |

## 1. Data model

Two entities gain columns. Nothing new is created.

### `Player`

| Column | Type | Postgres | Notes |
| --- | --- | --- | --- |
| `CalendarToken` | `string?` | `text` | The credential. Null until first issued, null again after revocation. |
| `CalendarTokenIssuedAt` | `DateTimeOffset?` | `timestamptz` | So `/mycalendar` can say when the link was last rotated. |
| `CalendarVersion` | `long` | `bigint`, default `0` | Bumped whenever anything that changes *this player's rendered feed* changes. Feeds the ETag. |

### `Game`

| Column | Type | Postgres | Notes |
| --- | --- | --- | --- |
| `Revision` | `int` | `integer`, default `0` | The event's `SEQUENCE`. Bumped when a feed-visible *detail of the game itself* changes. |
| `RevisedAt` | `DateTimeOffset` | `timestamptz` | The event's `DTSTAMP`. Set to `CreatedAt` on insert, to the clock on each revision. |

### Indexes

| Index | Why |
| --- | --- |
| `IX_Players_CalendarToken` — unique, partial (`WHERE "CalendarToken" IS NOT NULL`) | The only index the hot path uses: one probe turns a URL into a player. Unique because a collision would hand one person another's feed. Partial because most players never issue a token, and the repo already filters indexes this way (`TeamConfiguration`). |

No new index on `Signup` or `Game` — the feed query filters on `Signup.GameId` and
`Game.TeamId`, both already indexed, and the result set is bounded by one person's live
signups.

### Migration

One migration, `AddCalendarFeed`, additive only:

- three nullable/defaulted columns on `Players`, two on `Games`
- `Games.RevisedAt` backfilled from `CreatedAt` (`UPDATE "Games" SET "RevisedAt" = "CreatedAt"`)
- the partial unique index

Applied at startup like every other (`DEPLOY.md`). Safe to deploy before anything reads it.

### Judgment calls

- **Token on `Player`, not a `CalendarSubscription` table.** Rejected the table because the
  only thing it buys is several live tokens per person — a rotation grace period, or a
  per-device link. Neither is wanted: rotation is meant to be an instant, total revocation.
- **Rotation overwrites the token.** This looks like a violation of invariant 7 ("nothing is
  ever deleted"), and is not: invariant 7 protects the audit trail of who did what. A
  credential is not history, and destroying the old one *is* the operation.
- **One token per player, covering every team they play for.** `CLAUDE.md` says team calendars
  "stay separate", which was written about the Board — the one pinned, team-owned view. A
  person's own view already crosses teams (`/myschedule` in a DM), on the reasoning that
  someone in two teams still only has one Friday evening. Rejected per-`(player, team)` tokens:
  N URLs to paste and N to rotate, for a separation nobody asked for on their own phone.

## 2. Endpoint contract

```
GET  /cal/{token}.ics
HEAD /cal/{token}.ics
```

Mapped only when `QUIZR_PUBLIC_URL` is set. `HEAD` is mapped explicitly — several clients probe
with it before subscribing, and `MapGet` alone would answer 405.

### Responses

| Status | When | Headers |
| --- | --- | --- |
| `200` | Known token | `Content-Type: text/calendar; charset=utf-8`, `ETag`, `Cache-Control: private, max-age=3600`, `Content-Disposition: inline; filename="quizr.ics"` |
| `304` | `If-None-Match` matches the current ETag | `ETag`, `Cache-Control` |
| `404` | Token malformed, unknown, or revoked | none |
| `429` | Rate limit | `Retry-After` |
| `500` | Anything unhandled | none |

### Error cases

- **Malformed token** — length and charset are checked by a route constraint before any
  database work, so a scan costs no query.
- **Unknown or revoked token** — the *same* bare `404` as malformed. No body, no `WWW-Authenticate`,
  no distinction between "never existed" and "was revoked". There is nothing an unauthenticated
  caller should be able to learn.
- **No `401`/`403` anywhere.** Calendar clients cannot do interactive auth; a challenge would
  only teach a scanner that the path is real.
- **Database unreachable** — `500`, logged with the player id where one was resolved, never
  with the path. See failure modes.

### The token never enters a log

`Program.cs` already filters `System.Net.Http.HttpClient` to Warning because every Telegram
call carries the bot token in its URI. The same leak now exists inbound, and it needs two
things:

1. `Microsoft.AspNetCore.Hosting.Diagnostics` filtered to Warning, which is what emits
   `Request starting HTTP/1.1 GET /cal/<token>.ics` at Information.
2. **The handler catches its own exceptions.** `STYLE.md` names exactly two places that catch
   broadly — the update dispatch boundary and the scheduler tick — and says a third is almost
   always someone hiding a fault. This is a genuine third boundary, for the same reason as the
   first: one failing request must not take anything else down, and here additionally because
   an exception escaping to the diagnostics middleware puts `RequestPath` into a logging scope,
   and `IncludeScopes` ships scopes to Seq. `STYLE.md` gains this as a named third boundary
   rather than an exception to itself.

Constant-time comparison is not used and is not needed: the lookup is an indexed equality
query, not a byte comparison, and the token is 256 bits of CSPRNG output.

## 3. Feed content

### The window

`[today − 30 days, today + 12 months)`, in whole UTC days. Never the whole history.

Whole days matter: the boundary moving is what makes the date part of the cache key correct
(§5).

### Which games appear

A live game is one this person holds a live signup for; a **finished** one is read off its
participation rows instead, never its signups. That is invariant 10 restated — once a game is
finished, what happened is what the participation says happened, including a captain's later
correction. It has one visible consequence: a finished game cannot report how many guests
somebody brought, because a participation row records who was there and not who invited them.
The guest line is omitted there rather than guessed at from history.


| Game | In the feed? |
| --- | --- |
| Live signup, inside capacity → **playing** | Yes |
| Live signup, beyond capacity → **reserve** | Yes, marked, and `TRANSP:TRANSPARENT` so it does not book the evening |
| Cancelled signup (they dropped) | No — invariant 3, the signup is gone |
| Finished, within the window | Yes, rendered from `Participation` (invariant 10), marked when `Played` is false |
| Declined | No |
| Games of a team they are in but not signed up to | No |
| Games of a team the bot was removed from | No — `TeamConfiguration`'s global query filter already hides them |

Removing an event means **omitting it from the next response**. No `STATUS:CANCELLED`
tombstones — a subscription feed is a full replacement on every fetch.

### VCALENDAR

| Property | Value |
| --- | --- |
| `PRODID` | `-//Quizr//Quizr Bot//EN` |
| `VERSION` | `2.0` |
| `CALSCALE` | `GREGORIAN` |
| `METHOD` | absent — this is a subscription, not iTIP |
| `X-WR-CALNAME` | The team's name when the feed covers exactly one team, otherwise a localized "Quizr" |
| `REFRESH-INTERVAL;VALUE=DURATION` | `PT1H` |
| `X-PUBLISHED-TTL` | `PT1H` |

Apple honours the refresh hints. Google ignores them and refreshes on its own schedule
(8–24 hours). Both are emitted anyway; they cost two lines.

### VEVENT

| Property | Value |
| --- | --- |
| `UID` | `game-{gameId}@quizr.bot` |
| `DTSTAMP` | `Game.RevisedAt`, as UTC |
| `SEQUENCE` | `Game.Revision` |
| `DTSTART` | `Game.StartsAt` as UTC, with a trailing `Z` |
| `DTEND` | `game.EndsAt`, likewise — the same 3 hours the scheduler auto-finishes on |
| `SUMMARY` | Localized. Franchise + title (the `GameLabel` rule, in plain text), prefixed with the team name when the feed spans teams, suffixed with the reserve position or the did-not-play marker |
| `LOCATION` | `Game.Venue` |
| `DESCRIPTION` | Localized lines, venue time first: the start in the team's own zone and offset, then status, guests brought, price, notes, tags as hashtags, and a link back to the announcement |
| `URL` | The `t.me/c/...` announcement link where the chat has one — supergroups only, same rule as `AnnouncementLink` |
| `STATUS` | `CONFIRMED` playing, `TENTATIVE` reserve |
| `TRANSP` | `OPAQUE` playing, `TRANSPARENT` reserve |
| `CATEGORIES` | `Game.Tags` |
| `VALARM` | **None.** Google will not fire alarms on a subscribed calendar, so an alarm here would be a promise kept on some phones and not others. The bot's own Telegram reminders are the real notification path, and they are already opt-in per slot per channel. |

`UID` is derived from our own `GameId` and a **fixed constant domain**, deliberately not from
`QUIZR_PUBLIC_URL`: moving the bot to another hostname must not orphan every event already in
everyone's calendar. It is stable for the lifetime of the event and is never a fresh GUID.

### A game's duration is one number, shared

`DTEND` is `game.EndsAt` — a derived fact in `Quizr.Domain/Extensions/GameExtensions.cs` over a
single private `Duration` constant, which is **also** what the scheduler auto-finishes on.

This was two numbers until this feature needed one. Invariant 8's window was four hours and
nothing stored a duration at all, so the feed would have carried a second, independently
guessed length — the classic way two constants drift until they contradict each other in
public. Both are now three hours, and both call sites read `game.EndsAt` rather than doing the
arithmetic:

```csharp
if (now >= game.EndsAt)      // SchedulerService, was: now >= game.StartsAt + AutoFinishAfter
```

The constant stays private: nothing outside the domain needs the span itself, only the instant
it produces. Consequence worth naming — a 19:30 game now auto-finishes at 22:30 rather than
23:30, so a captain has an hour less before participation materialises. Invariant 11 keeps a
finished roster editable by captains forever, so this changes when the rows appear, not whether
they can be corrected.

**`SEQUENCE` is per game, not per subscriber.** A player promoted from the reserve sees a
changed body with an unchanged `SEQUENCE`. That is correct for a subscription feed (the client
replaces the whole event by `UID`) and would only matter under iTIP, which is a non-goal.

### Timezones — UTC, plus venue time as text

`DTSTART` and `DTEND` are emitted as UTC instants with a trailing `Z`. No `TZID`, no
`VTIMEZONE`, anywhere in the output.

**`TZID` does not do what its name suggests.** It tells a client how to *interpret* a
wall-clock value in order to compute the instant; it does not ask the client to display that
zone. Once a client has the instant it renders in the **viewer's** calendar zone, identically
from `DTSTART;TZID=Europe/Berlin:20260904T193000` and from `DTSTART:20260904T173000Z`. Some
clients surface the originating zone in an expanded event detail, inconsistently, and least
reliably on a subscribed calendar. There is no iCalendar property that puts two clocks on one
event.

So the viewer's local time comes free from the client's own rendering, and **venue time is
text we write**, as the first line of `DESCRIPTION`:

```
    grid:  18:30  Квиз, плиз! #142

  opened:  Starts 19:30 · Europe/Berlin (UTC+02:00)
           You're playing
           Пивная станция · 500 ₽
           #music
           Open in Telegram: https://t.me/c/…
```

A localized template with `{When}`, `{Offset}` and `{Zone}` placeholders, so Russian and German
can reorder it — never concatenated (`CLAUDE.md`). The offset comes from `TeamTime.GetUtcOffset`,
which already exists and is already tested. The full venue-local **date and** time, not just the
clock, so a player far enough east or west doesn't see a date mismatch they can't explain. For
everyone in the same city as the venue — every player today — that line duplicates what the
client already shows; one redundant line is what makes the feed unambiguous for the person who
travels, and it matches what `/myschedule` already renders in Telegram.

**Why not `TZID` + `VTIMEZONE`:** it would buy nothing for the two-times requirement, which the
description line delivers under either encoding, and it would put **two timezone databases in
one process**. `TeamTime` uses `TimeZoneInfo`, which reads the operating system's tzdata;
Ical.Net generates `VTIMEZONE` from NodaTime's bundled tzdb. Disagreement between them after a
country changes its DST rules would make the file internally inconsistent — the wall-clock value
computed from one database, the rules for interpreting it from the other — and the failure is an
event landing at the wrong time in somebody's calendar, silently, with no test in our process
able to catch it. Emitting the instant removes that by construction rather than by making two
databases agree and asserting that they still do. It is also the most faithful projection
available: `CLAUDE.md` stores the computed instant and nothing else, and this renders the
instant as an instant.

**One thing Ical.Net gets wrong.** It escapes `,` and `;` in a TEXT value but leaves a
backslash alone, which RFC 5545 §3.3.11 does not allow — a lone `\` opens an escape sequence,
so a venue like `C:\bar` reaches a client as the undefined `\b` and a strict parser may reject
the whole calendar. `CalendarRenderer.Text` pre-escapes ours so Ical.Net passes both through.
Two tests pin it, in both directions; if a future version fixes the library, they fail on a
doubled backslash rather than going quietly wrong.

**NodaTime is therefore not adopted.** It arrives as a hard dependency of Ical.Net and is never
consulted — no zone is ever resolved through it. `docs/STACK.md`'s "When to revisit" records
what would earn it: `RRULE` recurrence, per-person timezones, or arithmetic on local dates
across a transition. None exist today, and the ambiguity NodaTime makes you resolve explicitly
cannot arise in a domain whose only local-time inputs are evening kick-offs and two reminder
slots.

### Localization

The feed renders in the **player's own** locale (`Player.Locale`, falling back to English) —
never the team's. `CLAUDE.md`'s rule is that group messages use the team's language and
anything personal uses the person's own; a private calendar is as personal as a DM. A feed
spanning two teams in two languages still renders as one document, in one language, which is
the person's.

## 4. Token lifecycle

| Step | Behaviour |
| --- | --- |
| **Generation** | 32 bytes from `RandomNumberGenerator`, base64url-encoded to 43 characters of `[A-Za-z0-9_-]`. Issued lazily on the first `/mycalendar`, the same way players and memberships are created lazily. |
| **Storage** | Plaintext in `Player.CalendarToken`. |
| **Delivery** | By DM. In a group, the reply is ephemeral (visible to that member only) — see §6. |
| **Rotation** | A button under the link. Overwrites the token; the old URL 404s on the next poll, on every device, with no grace period. That is the point. |
| **Revocation** | A button that nulls the token. The feed stops existing; `/mycalendar` will happily issue a new one later. |
| **Logging** | Never, anywhere, in any form. Not at Debug, not in an error. |

**Plaintext, not hashed.** A hashed token would mean the database could not reproduce the URL,
so `/mycalendar` could only ever show it once — and the whole point of the command is that
someone can come back and re-read the link when they set up a second device. The threat a hash
defends against is a database dump, which in this system also contains the connection string's
worth of everything else; the token grants read-only access to one person's own quiz schedule.
Rejected as the wrong trade for this data.

## 5. Caching

Two layers, one key.

```
key = (PlayerId, Player.CalendarVersion, CalendarFormat.Version, utcDate)
ETag = "sha256(key)"  (strong, quoted)
```

| Part | Why it is in the key |
| --- | --- |
| `PlayerId` | Feeds are per person. |
| `CalendarVersion` | Any data change affecting this person's feed. |
| `CalendarFormat.Version` | A hand-bumped constant in the serializer. Without it, a deploy that *fixes* the ICS output would keep serving `304` against every client's stale cached body, forever. |
| `utcDate` | The window's boundaries move in whole days (§3). Without it, a game entering the 12-month horizon would never appear. Costs at most one re-render per person per day. |

**Request path:**

1. Route constraint rejects a malformed token → `404`, no query.
2. One primary-key-shaped lookup on the partial unique index: `token → (PlayerId, CalendarVersion, Locale)`. No joins, no `Include`.
3. Compute the ETag. If `If-None-Match` matches → `304`. **The request ends here without
   serializing anything.**
4. `IMemoryCache.TryGetValue(key)` → the rendered bytes, if a previous request built them.
5. Otherwise query, render, cache, return.

The ETag is a hash rather than `"{playerId}-{version}"` so an internal row id and a change
count do not travel through proxies and client logs. Entries carry a 25-hour absolute
expiration and a size limit: version bumps change the key so nothing needs *evicting*, but the
date component means yesterday's keys would otherwise accumulate for the life of the process.

### Where the version gets bumped

A `CalendarVersionInterceptor` — an EF Core `SaveChangesInterceptor` registered on `QuizrDb` —
inspects the change tracker in `SavingChangesAsync`, works out who is affected, and increments
their `CalendarVersion` **in the same `SaveChanges`**, inside the same transaction as the change
that caused it. That mirrors what `CLAUDE.md`'s Conventions already require of the notifications
table and of `AuditEntry`.

| Change tracker sees | Bumped |
| --- | --- |
| A `Signup` added or modified | Every player with a live signup on that game — a seat taken or freed moves everyone below it across the playing/reserve line — plus the signup's own player and inviter, read from the change tracker because a row being inserted is not yet visible to that query |
| A `Game` modified in a feed-visible field (`Title`, `Venue`, `StartsAt`, `Capacity`, `Price`, `Notes`, `Tags`, `FinishedAt`, `DeclinedAt`) | Every player with a live signup on that game, **and** the game's own `Revision` / `RevisedAt`. Deliberately not `LastNudgedAt` or `AnnouncementMessageId`, or every nudge would read as a rescheduling |
| A `Participation` added or modified | Every player with a live signup on that game |
| A `Team` modified in `TimeZoneId` or `Name` | Every member of the team |
| A `Player`'s `Locale` modified | That player |
| A `Game` added | Nobody — a new game has no signups yet. `RevisedAt` is stamped with `CreatedAt`, since a game nobody has revised was last revised when it was made |

**This is a judgment call, and the interesting one.** The alternative — an explicit
`await _calendarVersions.BumpAsync(...)` at each of the ~15 service call sites that save a
signup or a game — is more in keeping with `STYLE.md`'s dislike of magic. It was rejected
because a missed call site produces a feed that is silently stale until some unrelated change
happens to fix it, which is exactly the class of bug the strongly-typed ids and
`Quizr.Domain`'s empty reference list exist to make structurally impossible. One interceptor
that cannot be forgotten beats fifteen calls that can. If it proves too clever to live with,
the explicit form is a mechanical refactor.

**Only subscribers are bumped.** The write is filtered to players whose `CalendarToken` is not
null, so a team where nobody uses the feature pays one query per save and writes nothing. A
version nobody can read is a version not worth writing, and it keeps the interceptor off the
critical path of every button tap in a team that never subscribes.

**Known staleness, accepted, two kinds:**

- Renaming a `Franchise` changes the `SUMMARY` of every game built from it, and is not bumped.
  A rename does not move anyone's evening, and the next roster change on that game corrects
  it. Bumping it would mean a fourth query shape for the rarest edit in the system.
- The bump is a read-modify-write, so two concurrent saves affecting the same person can both
  read version 5 and both write 6. If a feed request lands exactly between those two commits it
  caches the first change's body under version 6 and answers `304` for the second. Closing it
  properly means serializing the read against the write, which is a much larger machine than
  this feature earns. It is bounded rather than open-ended: the next change to any of that
  person's games fixes it, and failing that the `utcDate` component of the cache key clears it
  within a day — which is inside Google's own 8–24 hour refresh window anyway.

**Not built yet, deliberately:** team-level caching (one rendered event reused across a team's
subscribers) and background pre-rendering. At ~20 subscribers refreshing hourly at most, the
whole feature is a few hundred queries a day. Revisit if a team's roster ever exceeds a few
hundred people, or if the per-request render shows up in `quizr.exceptions`' neighbours.

## 6. Bot surface

### `/mycalendar`

Added to `CommandMenu.EveryoneCommands`, which `/help` reads from — so nothing else needs
registering.

| Where | Behaviour |
| --- | --- |
| DM | Issues the token if there isn't one, sends the URL and the onboarding text, with **Rotate** and **Turn off** buttons |
| Group, `DmEnabled` | Sends all of the above by DM, and an ephemeral "sent you a message" in the group |
| Group, no DM possible | Sends the URL as an **ephemeral** message in the group, visible to that member only (`SendEphemeralAsync`, Bot API 10.2), with the same buttons |
| `QUIZR_PUBLIC_URL` unset | A localized "calendar feeds aren't available on this bot" — no token issued |

The ephemeral fallback exists because a bot cannot message anyone who has not started it, and
telling someone to go start the bot before they can have a calendar is a dead end for the one
feature most likely to be tried by a person who has never DM'd it. Ephemeral delivery is
documented as best-effort; if it does not land, running the command again is free.

### Callback verbs

Single characters, per `CallbackData`'s scheme, all with dummy ids:

| Verb | Action |
| --- | --- |
| `y` | Replace link — asks to confirm |
| `Y` | Confirm replace |
| `Z` | Turn off — asks to confirm |
| `Q` | Confirm turn off |
| `L` | Back to the calendar view — the way out of either prompt, and the only one that destroys nothing |

Both destructive actions confirm first, matching Drop and Decline, and both confirm texts say
plainly that every device subscribed to the old link stops updating. The callbacks resolve no
team: a calendar is a person's own, so unlike every other callback handler these work in a DM,
where there is no team to resolve from the chat id.

**"Replace" rather than "rotate" in the user-facing text.** Rotation is what it is called here
and in the code; it is not a word to put in front of somebody looking for the button that fixes
a leaked link.

### Onboarding text

Four things it must say, because each is a support question otherwise:

1. **The Google Calendar mobile app cannot add a calendar by URL.** Only the desktop web UI
   can: Other calendars → **+** → From URL. On the phone it appears afterwards, on its own.
2. Apple: Settings → Calendar → Accounts → Add Account → Other → Add Subscribed Calendar.
3. **Refreshes are slow** — Google 8–24 hours, Apple about an hour. The calendar is the
   schedule of record; **Telegram stays the channel for last-minute changes.**
4. **Anyone with this link can see your games.** Don't post it anywhere. Rotate if you do.

## 7. Failure modes

| Mode | What happens |
| --- | --- |
| **Slow or unreachable database** | The handler's own `CancellationTokenSource` (linked to `RequestAborted`) caps the request; the boundary catch logs with the player id and returns `500`. No connection pile-up, no token in the log. |
| **Aggressive client** | Chained rate limiters: 10 requests/minute per token, 60/minute per client IP. Over → `429` with `Retry-After: 60`. The IP limiter is what stops a scanner; the token limiter is what stops one misconfigured client outweighing all real traffic. Coolify's proxy means `UseForwardedHeaders` is required for the IP partition to see anything but the proxy. |
| **Token enumeration** | 256-bit token, uniform bare `404`, no query on a malformed one, per-IP limit. |
| **Leaked token** | Read-only exposure of one person's own quiz schedule — no write path, no other player's data, nothing about other teams they are not in. **Rotate** invalidates it instantly, everywhere. |
| **Two containers** | Unchanged, and now easier to cause. Duplicate `Participation` rows from a double auto-finish is the lasting damage; the `409` clears itself. See the warning at the top and in `DEPLOY.md`. |
| **A team with no timezone** | Cannot have games (`M3` refuses to create them), so the feed cannot reach one. Rendered with `TimeZoneId!` the same way `MyScheduleRenderer` does. |
| **`QUIZR_PUBLIC_URL` unset** | The endpoint is not mapped and `/mycalendar` says so. A local run and the current production deploy both behave exactly as they do today. |
| **A stale image's tzdata** | Unchanged from `CLAUDE.md`'s existing warning — the feed consults the same one database everything else does. See §3. |

## 8. Test plan

**Unit — no Docker.** These live in `Quizr.App.Tests` alongside `BoardRendererTests` and
`MyScheduleRendererTests`, which are equally pure; the project only starts a container for the
classes that ask for `PostgresFixture`.

- `CalendarRendererTests` — the bug farm, and where the effort goes:
  - every `DTSTART` and `DTEND` ends in `Z`; `TZID` and `VTIMEZONE` appear nowhere in the output
  - the venue-time description line carries the team's zone, offset and local date for a feed whose games sit in two different zones
  - CRLF line endings; folding at 75 octets; a long Russian venue name folds without splitting a UTF-8 sequence
  - `,` `;` `\` and newlines escaped in `LOCATION`, `SUMMARY` and `DESCRIPTION`
  - `UID` deterministic across two renders, distinct per game, unchanged by a roster change
  - `SEQUENCE` equals `Game.Revision`; `DTSTAMP` equals `RevisedAt`
  - `REFRESH-INTERVAL`, `X-PUBLISHED-TTL`, `X-WR-CALNAME` present; no `VALARM` anywhere
  - `STATUS`/`TRANSP` differ for playing vs reserve
  - an empty feed still produces a valid `VCALENDAR`
  - the output round-trips through Ical.Net's own deserializer
- `CalendarTokenTests` — length, charset, uniqueness across a large batch.
- `CalendarCacheKeyTests` — the ETag changes with version, with format version, with the date;
  is stable otherwise; and does not contain the raw player id.
- `CalendarEndpointTests` — the handler is a method returning `IResult`, tested directly: 200
  headers, 304 on a matching `If-None-Match`, 404 for unknown and for malformed, and that no
  code path writes the token anywhere. No host is started; see the gap below.
- `StringsTests` / `PluralTemplatesTests` pick up the new keys automatically.

**Integration — `PostgresFixture`.**

- `CalendarFeedServiceTests` — window boundaries (29 days ago in, 31 out; 11 months out in, 13
  out); declined excluded; cancelled signup excluded; another player's games never appear;
  reserve marked with its position; finished-and-not-played marked; two teams merged in date
  order.
- `CalendarVersionInterceptorTests` — join, drop, bring a guest, capacity change, each edited
  field, finish, decline, participation toggle, team timezone change: each bumps exactly the
  players it should and leaves the others alone.

**Gap, named:** no `WebApplicationFactory` test of the real HTTP pipeline. `Program.cs` starts
long polling unconditionally, so hosting it in a test would need a guard that is a bigger change
than this feature warrants. `docs/STACK.md` already flags `WebApplicationFactory` as arriving
with the mini app; this is where it will earn its keep.

What the unit tests cannot reach was checked by hand instead, once, against a real Postgres and
a real seeded subscriber — worth repeating rather than trusting if any of it changes:

| Checked | Result |
| --- | --- |
| Route constraint on a wrong-length token | `404`, no handler reached |
| `HEAD` | `404`/`200` as the token warrants — **not** `405` |
| `POST` | `405` |
| Eleven rapid `GET`s on one token | seven answered, then `429` with `Retry-After: 60` |
| A seeded subscriber's real feed | `200`, correct `ETag`, `Cache-Control`, `Content-Disposition`, and a valid `VCALENDAR` |
| Conditional `GET`, matching and `W/`-weakened | `304` both; a stale tag gets `200` |
| **The token in any log line** | **absent across ~20 requests including 404s and 429s** |
| The `AddCalendarFeed` migration against an empty database | applied clean |

One honest limitation the same run confirmed: a row written by raw SQL bypasses
`CalendarVersionInterceptor`, so the version does not move and a client keeps its cached body.
That is by construction — the interceptor is an EF-level mechanism and the application only
ever writes through EF — but it is the thing to remember before anyone reaches for
`ExecuteUpdate` on a signup or a game.

## 9. Rollout

Ordered, and each step is independently safe:

1. **Migration ships first**, in slice 1, additive only. Applied at startup. Nothing reads the
   columns yet.
2. **Deploy with `QUIZR_PUBLIC_URL` unset.** Kestrel binds a port inside the container; nothing
   is routed to it, the endpoint is not mapped, `/mycalendar` reports unavailable. Production
   behaviour is unchanged.
3. **Coolify, once:** expose the container port, attach a domain, let the proxy terminate TLS.
   **Leave the health check empty** — see the top of this file. Then set `QUIZR_PUBLIC_URL` and
   redeploy.
4. Confirm on that deploy: `docker ps --filter name=quizr` shows exactly **one** container, and
   `docker logs <container> | grep -i conflict` is empty.

`CommandMenu` re-registers on every startup, so `/mycalendar` appears in the menu with no extra
step.

### Configuration

| Variable | Required | Notes |
| --- | --- | --- |
| `QUIZR_PUBLIC_URL` | for this feature | `https://quizr.example.com` — the origin the feed URL is built from. Unset means the feature is off. |
| `ASPNETCORE_HTTP_PORTS` | no | Defaults to `8080` in the Dockerfile. Coolify needs to know it to route. |

### Packaging

- `Quizr.App.csproj` moves to `Microsoft.NET.Sdk.Web`; the Dockerfile's runtime stage moves from
  `dotnet/runtime:10.0` to `dotnet/aspnet:10.0`. `tzdata` is still installed explicitly.
- New pins in `Directory.Packages.props`: `Ical.Net` 5.2.3, and `NodaTime` 3.2.2 — Ical.Net's own
  dependency, pinned explicitly because `CentralPackageTransitivePinningEnabled` is on, and never
  consulted by this code (§3).
- Rate limiting and `IMemoryCache` come from the shared framework — no package.

## 10. Implementation plan

Kept live. Ticked as completed; amended in the same commit when reality diverges, with a note
saying what changed and why.

### Slice 0 — One duration, shared

Standalone and mergeable on its own; everything after it depends on `game.EndsAt` existing.

- [x] `GameExtensions`: a private `Duration` of 3 hours, and a `game.EndsAt` extension property
- [x] `SchedulerService` reads `game.EndsAt`; `AutoFinishAfter` deleted
- [x] `GameExtensionsTests`, and the two `SchedulerServiceTests` cases renamed off "four hours"
- [x] Docs: `CLAUDE.md` invariant 8, `docs/VISION.md` lifecycle diagram, feature list and
      decision table, `docs/STACK.md`'s scheduler section, this file

### Slice 1 — Migration and model

- [x] `Player.CalendarToken`, `CalendarTokenIssuedAt`, `CalendarVersion`; `Game.Revision`, `RevisedAt`
- [x] `PlayerConfiguration` partial unique index on `CalendarToken`
- [x] `AddCalendarFeed` migration, with the `RevisedAt` backfill
- [x] `CalendarVersionInterceptor` and its registration, on every context including `PostgresFixture`'s
- [x] `CalendarVersionInterceptorTests`
- [x] Docs: this file, `docs/PLAN.md` milestone entry

### Slice 2 — ICS serialization

- [x] `Ical.Net` + `NodaTime` pins; `docs/STACK.md` table entry and its "When to revisit" row for NodaTime
- [x] Feed DTOs and `CalendarFeedService` (window, playing/reserve, finished, multi-team merge)
- [x] `CalendarRenderer` — pure, no clock, no EF (renamed from `CalendarSerializer`, which is the name of a type in Ical.Net)
- [x] `CalendarFormat` — the format version and the UID domain
- [x] Localization keys in `en` / `ru` / `de`, with plural snapshots for the guest count
- [x] `CalendarRendererTests`, `CalendarFeedServiceTests`
- [x] Docs: `docs/STACK.md`, and this file where the serializer forced a change

### Slice 3 — HTTP endpoint

- [x] `Microsoft.NET.Sdk.Web`; `WebApplication` in `Program.cs`, and the two packages the shared framework now supplies dropped
- [x] `GET`/`HEAD /cal/{token}.ics`, route constraint, ETag, 304, caching
- [x] Rate limiters, `UseForwardedHeaders`
- [x] Log filtering and the handler's own error boundary
- [x] `CalendarEndpointTests`, `CalendarCacheKeyTests`, `CalendarTokenTests`, plus a manual end-to-end run recorded below
- [x] Docs: `docs/STYLE.md` (third catch boundary), `docs/STACK.md` (host change), `CLAUDE.md` (a new HTTP surface section), `README.md`

### Slice 4 — Bot surface

- [x] `/mycalendar` in `UpdateRouter` and `CommandMenu`, plus `CalendarSubscriptionService` for the write side
- [x] Replace and turn-off callbacks with their confirm steps, and a cancel that re-renders
- [x] Onboarding, replacement and confirmation strings in three languages
- [x] `UpdateRouterCalendarTests`
- [x] Docs: `CLAUDE.md` vocabulary, `README.md` feature list and setup

### Slice 5 — Wiring, config, deployment

- [ ] Dockerfile: `aspnet` runtime image, `ASPNETCORE_HTTP_PORTS`
- [ ] `QUIZR_PUBLIC_URL` read in `Program.cs`, feature gated on it
- [ ] `docs/DEPLOY.md`: port, domain, env vars, and the health-check warning — restated, not resolved
- [ ] `README.md`: setup, and the "nothing ever connects to it" claim corrected
- [ ] `VISION.md`: feature status moved out of **Later**, decision-log rows
- [ ] Final pass over `PLAN.md` and this file
