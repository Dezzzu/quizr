# Quizr — idea, vision and plans

Status: **product description settled, implementation not started.**

---

## The idea

A fifty-person pub quiz team lives in one Telegram chat. The captain registers the team for
a quiz, posts a message with the date, place and time, and everyone who wants to play leaves
a reaction. That reaction history is the roster: who's coming, who registered first when more
people want in than there are seats.

Quizr replaces that with a bot that owns the roster, and turns the chat messages into views
generated from it.

## The problems it exists to solve

1. Telegram loses pinned messages, and they have to be found and re-pinned by hand.
2. People forget they registered, so someone has to remind them manually.
3. Bringing a friend is tracked by hand.
4. A reaction removed by accident drops someone out of the queue silently.
5. Pinned messages have to be manually kept up to date.
6. Registration for different franchises opens at different times, so games never appear in
   date order.

## The shift

Every one of those follows from a single property: **a reaction is an event with no memory.**
No trustworthy ordering, no capacity, no way to express a guest, no way to remind anyone, and
it lives on a message Telegram can lose.

So the state moves off the message and into a database the bot owns. Both chat messages become
generated views, rewritten whenever the roster changes. Nothing of value lives in a message any
more, which is what makes a lost pin or a deleted post cost nothing.

```
Player taps a button  ─┐
                       ├─►  Bot  ─►  Roster (source of truth)  ─┬─►  Announcement post
Captain edits anything ─┘                                       └─►  Pinned Board
```

---

## How it works in the chat

Two kinds of bot message. That's the whole surface.

### The announcement — one per game

Posted when the captain creates the game. Buttons instead of reactions, and the body is
rewritten by the bot on every change, so what's on screen is always the real roster.

```
Квиз, плиз! #142
Thu 4 Sep, 19:30 · Пивная станция · 500 ₽ · music

Playing — 18/18
  1. Дмитрий              10. Саша
  2. Аня                  11. Марина
  3. гость Ани  (+1)      12. Миша  (guest of Марина)
  4. Костя                13. Игорь
  …

Reserve
 19. Лена                 20. Паша  (+1)

[ ✅ I'm in ]  [ ➕ Bring a friend ]  [ ❌ Can't make it ]
```

Queue positions are shown publicly on purpose: it ends every argument about who was first,
and it makes an accidental drop-out visible to the person who caused it.

### The Board — one per chat, pinned

Every upcoming game, always sorted by date, whatever order registration happened to open in.
The bot keeps it pinned and restores the pin silently when Telegram loses it. Each row links
to that game's announcement, which is what makes a three-week-old post findable.

```
Upcoming games

Thu  4 Sep   Квиз, плиз! #142      · Пивная станция            18/18 · 2 res.
Sat  6 Sep   Мозгобойня #88        · Бар Гагарин               11/16
Thu 11 Sep   Квиз, плиз! #143      · Пивная станция             4/18
Fri 19 Sep   Эйнштейн Party #31    · Дорогая, я перезвоню       7/24
```

Only the Board is pinned. Pinning every game post gives Telegram more things to lose, and
pins are ordered by when a message was *sent* — which is precisely problem 6.

---

## Concepts

**Team.** One Telegram chat is one team. It owns its games, franchises, timezone and captains.
A person can belong to several teams — useful if someone moves city — and each team's calendar
stays separate.

**Captain.** Chat admins are captains by default, with explicit grant and revoke on top.
A captain can do anything a player can do, on that player's behalf: register them, drop them,
name their guest, correct their attendance. That one rule removes a whole category of edge cases.

**Franchise.** A real entity, not a text label: name, default venue, default capacity, default
price. Games are created by cloning the last one — pick the franchise, fix the date, type the
number. It also answers "who turns up for which franchise" for free.

**Game.** Franchise, number or title, venue, date and time, capacity, price, optional tags,
free-text notes. Every field stays editable after posting. Money is displayed, never tracked —
it's settled at the venue.

**Seats and the queue.** Capacity is a venue restriction on team size, so the rule is simply
first come, first served. Everyone gets a timestamp; the first *capacity* entries are playing
and the rest form the reserve. When someone drops, the first reserve is promoted automatically,
notified, and the seat is held for them indefinitely — if they never respond, a captain removes
them by hand.

Dropping out removes you completely. Adding yourself back gives a fresh timestamp at the end
of the queue, often the reserve. That's intended, and it doubles as a way for someone unsure
about a game to step aside without blocking a seat.

**Four kinds of participant.**

- **Members** — people in the chat.
- **Guests** — brought by a member, anonymous by default with an optional name, no limit on
  how many. A guest occupies a seat and holds their own queue position; the team is trusted
  to be fair about it.
- **Team guests** — when a member drops, they choose whether their guests drop with them or
  stay. A guest who stays becomes the team's, with no owner. Only *named* guests may stay,
  because an ownerless anonymous +1 is a person nobody can identify at the door.
- **Venue-assigned players** — strangers the organisers occasionally add to the team on the
  night, recorded after the fact on the same screen used to correct attendance.

A guest who keeps coming gets added to the chat and becomes a member. That's a social step,
not a feature.

---

## Game lifecycle

```
        ┌──────────────────────┐
        │ Full                 │   reserve queue forms
        │ reserve queue forms  │
        └──────────┬───────────┘
      last seat ▲  │ ▼ no reserve left
         taken  │  │
        ┌───────┴──┴───────────┐    start   ┌──────────────┐  Finish tapped  ┌──────────────┐
        │ Open                 │───────────►│ In progress  │──── or 4 h ────►│ Finished     │
        │ join or drop freely  │    time    │ 4-hour grace │                 │ counted as   │
        └──────────┬───────────┘            │ still live   │                 │ played       │
                   │                        └──────────────┘                 └──────────────┘
    captain declines│
                   ▼
        ┌──────────────────────┐
        │ Declined             │
        │ archived, not counted│
        └──────────────────────┘
```

A game finishes itself four hours after kickoff and assumes everyone showed up, so the
ordinary case needs no input at all. The captain's post-game screen exists only to mark
someone absent, add a venue-assigned player, or decline the game entirely. A finished game
keeps its roster editable by captains forever.

---

## How the six problems are answered

| Problem | Answer |
| --- | --- |
| Lost pins | Exactly one pinned message per chat. The bot verifies the pin and restores it silently, reposting from the database if the message is gone. |
| Manual reminders | The bot knows the date and the roster, so it reminds registered players before each game, and lets a captain nudge a chosen subset in one tap. |
| Tracking guests | A **Bring a friend** button. Guests take real seats with real queue positions, can be named, and survive their inviter dropping out. |
| Accidental un-registration | Buttons aren't toggles — **I'm in** pressed twice keeps you in. Leaving takes a confirmation. Positions are public, so a slip is visible, and every action is logged regardless. |
| Keeping pins current | Stops being a task. Board and announcements are generated from the roster and cannot drift from reality. |
| Games out of date order | The Board sorts by game date, always — independent of when each franchise opened registration. |

---

## Roadmap

### Phase 1 — the bot

Everything runs in the team chat. Announcement posts with buttons, the pinned Board, the
queue, guests, reminders, the archive. **No infrastructure at all**: no domain, no TLS, no
inbound ports. The bot polls Telegram and keeps a local database, and runs on anything left
switched on.

### Phase 2 — the mini app

A second view onto the same data, added once the bot has been used in anger. The captain's
game-creation UI moves first, because that is where the friction is — seven fields through a
chat conversation is tedious and it's the most frequent organiser task. Then the calendar,
the phone-calendar subscription feed, personal history.

### Phase 3 — open the app up

Read-mostly views for everyone: my upcoming games, my history, the full calendar.

**The rule that keeps this honest:** the app never becomes required for playing. Anyone who
only ever taps one button in the group chat stays a first-class member of the team.

---

## Feature status

**Bot v1** — the thing worth building first.

- Button sign-up with a recorded timestamp
- Capacity and a reserve queue; games marked full
- Automatic promotion from the reserve, seat held
- Queue positions visible to everyone
- Guests — unlimited, occupying seats, optionally named
- Team guests when the inviter drops (named only)
- Captains act on anyone's behalf
- Announcement post, rewritten on every change
- Pinned Board, date-ordered, self-repinning
- Franchises with defaults, and clone-to-create
- Per-team timezone, set by the captain
- Multiple teams, one per chat — in the data model from day one, UI later
- Auto-finish four hours after kickoff, plus an explicit Finish button
- Everyone attended by default; captains mark absences
- Add venue-assigned players who were never registered
- Decline a game the team chose not to play
- Archive, browsable by everyone
- Reminders before a game
- Reserve promotion ping

**Later** — wanted, not yet.

- Game tags (music, detective, …)
- Interface language following each person's Telegram
- Results — score and placement
- Statistics, captains only at first, opened up once it's clear which numbers are corrosive
- Nudge a chosen subset, tagged in the team chat
- Captain's game-creation UI (first thing to build in the app)
- Calendar view — all games, or only mine
- Phone-calendar subscription feed
- Personal history for players

**Parked** — not rejected, just not now.

- Badges and milestones
- Full data export

**Ruled out.**

- Money and debt tracking — settled at the venue; price is just a field
- Lineup building and player strengths — capacity is a venue limit, not a selection problem
- Photos and a memory wall
- DMs as the main notification channel

---

## Decisions already made

Recorded so they don't get re-argued.

| Decision | Why |
| --- | --- |
| Chat first, app later | No overcommitment. The bot is the product; the app is a second view added once the bot has been used in anger. |
| Buttons, not reactions | Reactions can't express a guest, can't be trusted for ordering, and are one careless tap from losing your place. |
| Only the Board is pinned | One pin to defend, and the only view that can be date-ordered independently of when messages were sent. |
| Games are posted immediately on creation | No drafts, no scheduled publishing. The captain posting the announcement *is* the registration-open alarm — that mechanism already works. |
| First come, first served | Capacity is a venue restriction on team size, not a selection problem. Nobody picks who plays. |
| Guests are unlimited and take seats | The team is trusted to be fair rather than rate-limited by software. |
| Guests are anonymous unless named | Naming is optional friction — but a guest who outlives their inviter must be named, or they drop. |
| Frequent guests get added to the chat | Guest records are per-game. Someone who keeps coming becomes a member. |
| Everyone attended unless told otherwise | Attendance is a correction, never a chore. Same shape one level up: a game counts as played unless declined. |
| Games stay live for four hours after kickoff | Plus an explicit Finish button. Late arrivals and last-minute changes are normal. |
| Rosters are never frozen for captains | Composition changes on the night; the archive should record what happened. |
| No undo window on dropping out | Dropping removes you entirely; re-registering puts you at the back. Simple, and it lets an unsure person step aside without holding a seat. |
| A freed seat is promoted automatically and held | The next person is notified and the seat waits. If they go quiet, the captain steps in — no timers. |
| Archive open to everyone, statistics captain-only | Anyone can page back through old games. Aggregate numbers stay private until it's clear which are corrosive. |
| Captains are chat admins, plus grants | Zero setup for a new team, and it matches how the chat is already organised. |
| One chat is one team | A person can be in several teams; calendars stay separate. Other teams can use the same bot. |
| Timezone is set per team by the captain | Not inferred from anyone's device. |

## Still open

None of these block starting.

- **When do reminders fire?** A day before and a few hours before is the obvious guess, but
  nothing's chosen. Also: whole chat, or only tag the registered players?
- **Do venue-assigned players count in statistics?** They played with the team but aren't of
  it. Probably recorded and excluded — but that's a guess.
- **Which language do group posts use?** Per-person language works in private messages and
  the app, but a single group message can only be written once. Needs a team-level setting.
- **What if a game is never declined but wasn't played?** Default-played means a forgotten
  decline silently inflates the count. Accepted for now; worth a gentle prompt if it turns
  out to happen.

## Telegram limits worth designing around

- **A bot cannot message anyone who hasn't started it.** The single hardest constraint.
  Quiet, targeted notifications need a one-time push to get people to start the bot; until
  then reminders live in the group and work by tagging.
- **The bot must be a chat admin** to pin, and to see messages that aren't commands.
- **Around twenty messages a minute per group.** Edits get debounced, reminders batched.
- **Mini app buttons don't work inline in groups.** When the app arrives, announcements open
  it through a normal link button carrying the game id.
- **Deep-link payloads are short and untrusted.** They carry an id, never data, and
  permission is always checked server-side.
