# Health checks

Two endpoints, and the change that makes it safe to point Coolify at one of them.

This file is the design and the live implementation plan. **It describes the system, not the
intention** — when implementation forces a different decision, this file changes in the same
commit. `docs/DEPLOY.md` is where the operational rules end up once they are true.

## Why this is not just "add an endpoint"

`docs/DEPLOY.md` forbids configuring a Coolify health check, and the reason is not squeamishness:
a passing health check is how Coolify decides a replacement container is ready, which is what
lets it start one **before** stopping the original. Two containers polling one bot token collide
— reminders and reserve promotions survive it, because `Notification`'s unique constraint on
`(SignupId, Kind)` rejects the duplicate, but **auto-finish has no such guard** and would
materialise a game's `Participation` rows twice.

So the feature is really two: endpoints worth having, and a reason it becomes safe to use them.

| | Delivers | Needs |
| --- | --- | --- |
| Endpoints alone | An off-box uptime monitor, which closes the gap `docs/DEPLOY.md` names as still open: Seq runs on the same VPS as the bot, so nothing currently notices if the box disappears | Nothing |
| Coolify's own health check | Deploy gating and health visibility in the UI | A second container being *harmless*, not merely prevented |

## What the checks assert

| Endpoint | Asserts | Why |
| --- | --- | --- |
| `GET /health/live` | The process answered | Deliberately trivial. It says the thread pool is not wedged and nothing else — see the gap below. |
| `GET /health/ready` | Postgres is reachable, **and** — only if this instance holds the singleton lock — the scheduler has ticked within 3 minutes | The tick is the one signal with teeth. `SchedulerHostedService` catches per-tick failures and keeps going, so ticks stopping while the process lives is exactly the failure a probe can see and a crash cannot. |

Three minutes is six missed ticks at the 30-second interval, which is slack enough that a slow
database does not flap the check.

**Pending migrations are deliberately not checked.** `Program.cs` migrates before the host
starts and a failure there stops it, so "started but unmigrated" is not a reachable state, and a
check that cannot fail is noise on every probe.

### The standby paradox, and why the tick check is conditional

Make readiness require a recent tick unconditionally and the feature defeats itself:

1. the old container holds the lock and is ticking
2. the new container starts, cannot get the lock, and therefore does not tick
3. Coolify waits for the new one to be ready — which it can never become
4. the deploy times out

So readiness means **"I can serve, and I am ready to take over"**: Postgres reachable, plus the
tick check only for whichever instance is actually leading. A standby is legitimately ready and
legitimately idle.

### What no probe here can see

The Telegram polling loop. `Telegram.Bot`'s receiver blocks and exposes no heartbeat, and a quiet
chat is indistinguishable from a dead loop by counting updates. It needs no probe: an exception
out of `BotHostedService.ExecuteAsync` stops the host (`BackgroundServiceExceptionBehavior` is
`StopHost`, the default), so a broken bot loop already kills the container. It is the *scheduler*
that survives its own failures, which is why the scheduler is what gets checked.

## The singleton guard

A Postgres **session-level advisory lock**, taken on a dedicated connection held open for the
life of the process. Whoever holds it polls Telegram and runs the scheduler; whoever does not
waits and retries.

- **No table, no lease, no expiry.** Postgres drops the lock when the session ends, so a
  hard-killed container releases it with nothing to clean up and no clock skew to reason about.
- **`STACK.md` says "there are no locks in this system."** That rule is about domain state — it
  exists so nobody reaches for a lock instead of the derived playing/reserve split. This is
  process-singleton election, which is a different question, and the rule gets a sentence saying
  so rather than a silent exception.

**If the lock connection drops, the process stops** and Coolify restarts it. Simpler than
standing down and re-acquiring, and consistent with what already happens: a database outage at
startup fails `MigrateAsync` and stops the host today, so a database outage mid-run stopping it
is not a new behaviour, only a wider one.

## A bug this would otherwise introduce

`CalendarRateLimits` installed both its limits as `options.GlobalLimiter`, with a comment saying
that is only defensible while the feed is the single route — "that stops being true the moment a
second route is added". This is that moment.

Health probes carry no `t` parameter, so every one of them would land in the **same empty-string
partition** as every malformed feed request, against a limit of ten a minute. A probe every ten
seconds plus any scanning traffic exhausts it, the endpoint answers `429`, Coolify reads that as
unhealthy, and the container gets restarted for being healthy.

**The fix is not the one this file first proposed.** Moving *both* limits to a named policy is
not expressible: chaining produces a `PartitionedRateLimiter` and `AddPolicy` takes a
partitioner, and a single partition cannot carry two dimensions. Splitting them turned out to be
the better design anyway, because the two limits were never the same kind of thing:

| Limit | Registered as | Why |
| --- | --- | --- |
| 60/min per client address | `GlobalLimiter` | Protects the process whatever route a flood aims at, and it is the **only** thing that can refuse a scanner — every request it makes carries a fresh token and so lands in a partition of its own, which the feed's policy can never refuse |
| 10/min per token | A policy on the feed endpoint | Stops one misconfigured calendar client outweighing all real traffic |

Both apply to a feed request: the middleware takes a lease from the global limiter *and* from the
endpoint's policy, and either can refuse. Everything else — health probes included — gets the
global limit only, at sixty a minute, which a probe every ten seconds does not come near.

The file moves to `Quizr.App/Http/RateLimits.cs` with it: once the address limit covers routes
that have nothing to do with calendars, `CalendarRateLimits` was the wrong name.

## Test plan

**Unit** — a fake clock and a stub lock:

- ready when Postgres answers and the tick is recent
- unhealthy when the last tick is older than the window
- **ready while holding no lock and never ticking** — the standby case, and the one that would
  hang a deploy if it regressed
- live answers while ready is failing, since they are different questions

**Integration** — `PostgresFixture`:

- two `BotInstanceLock`s against one database: the first leads, the second does not
- the second acquires once the first is disposed
- health endpoints are not rate limited, however many times they are called

## Rollout

Ordered so each step is independently safe:

1. Rate limits move to a per-endpoint policy. No behaviour change to the feed; nothing else yet.
2. Endpoints ship. **Do not configure Coolify's health check.** Point an external uptime monitor
   at `/health/ready` — the off-box liveness that is missing today.
3. The guard ships. Verify in the logs that a second container stands down rather than polls.
4. Only then does `docs/DEPLOY.md`'s rule flip, in its own commit, so the change of policy is
   reviewable on its own.

The endpoints are unauthenticated and bodies stay bare `Healthy`/`Unhealthy`: no component
detail, no versions, no connection strings.

## Implementation plan

Kept live. Ticked as completed; amended in the same commit when reality diverges.

### Slice 1 — Scope the rate limiter

- [x] The per-address limit stays global; the per-token limit becomes a policy the feed endpoint
      opts into with `RequireRateLimiting`. Not the single named policy this file first proposed
      — see above for why that is not expressible, and why the split is better
- [x] `CalendarRateLimits` → `Quizr.App/Http/RateLimits.cs`, since the global half is not the
      calendar's concern
- [x] `RateLimitsTests`: the address budget, that two addresses do not share one, and that every
      token gets its own partition — which is the property that makes the global limit load-bearing
- [x] Verified against a running process, because the wiring is what could break rather than the
      numbers: eleven requests on one token get `404 x10` then `429`; with a fresh token each
      time — which the feed's policy can never refuse — the address limit still refuses at the
      expected point

### Slice 2 — The endpoints

- [x] `SchedulerHeartbeat` singleton, written by `SchedulerHostedService` at the same moment as
      the metric, and for the same reason read differently
- [x] `SchedulerHealthCheck` and `DatabaseHealthCheck`, registered with `AddHealthChecks` and
      tagged `ready`
- [x] `MapHealthChecks` for both routes; liveness runs no checks and opts out of rate limiting
- [x] `SchedulerHealthCheckTests` — recent tick, stale tick, the exact boundary, and never
      ticked. **The standby case moved to slice 3**: there is no lock yet, so no instance can be
      legitimately idle, and a test asserting otherwise would be asserting nothing
- [x] Docs: this file, `CLAUDE.md`'s HTTP surface section, and a note in `docs/DEPLOY.md` so it
      does not describe a bot without health endpoints while one has them

Verified against a running process, since what could break is the wiring:

| Checked | Result |
| --- | --- |
| `/health/live`, `/health/ready` with Postgres up | `200 Healthy` both |
| 120 rapid requests to `/health/live` | zero non-200s — the rate-limit exemption holds |
| `/health/ready` under load | first `429` at the 60th, so it is bounded as intended |
| **Postgres stopped** | `/health/live` still `200 Healthy`, `/health/ready` `503 Unhealthy` |

That last row is the whole reason the two are separate questions rather than one.

### Slice 3 — The singleton guard

- [x] `BotInstanceLock`: acquires in the background, holds, stops the application if its session
      drops. Acquisition must not block startup — a standby that blocked would never serve
      `/health/ready`, never be declared ready, and so deadlock the very rolling update it exists
      to make safe
- [x] `BotHostedService` and `SchedulerHostedService` wait on it; the scheduler re-checks every
      tick, which bounds the damage in the seconds between losing the lock and exiting
- [x] Readiness reports the tick check only while leading, plus a `LeadershipHealthCheck` so a
      standby is legible rather than merely silent
- [x] `BotInstanceLockTests` against real Postgres — lead, stand by, hand over
- [x] Docs: the no-locks rule in `CLAUDE.md` gains the distinction

**The connection must not be pooled, and that is the whole feature.** An advisory lock belongs
to its session, and a pooled connection handed back keeps that session alive: Npgsql defers the
`DISCARD ALL` that would release the lock until the connection is next *used*, which on shutdown
is never. The lock outlives the container that took it, and the replacement waits for an idle
timeout rather than taking over. Nothing about the code looked wrong — the handover test failed,
which is why it was written before the mechanism was trusted.

Verified with two real processes against one database:

| | |
| --- | --- |
| A starts | "This instance is leading", polls Telegram |
| B starts | "Another instance is leading", **never calls Telegram at all** |
| B's `/health/ready` | `200 Healthy` — a standby is ready, which is what lets a rolling update finish |
| A stopped | B logs "This instance is leading" and starts polling, with nothing coordinating it |

### Slice 4 — Flip the rule

- [x] `docs/DEPLOY.md`: "The one thing that will bite you" becomes "Only one instance ever does
      the work" — the constraint is unchanged, what enforces it is not. Coolify's health check
      values, and the heartbeat section rewritten around the probe rather than around its absence
- [x] `CLAUDE.md` and `docs/CALENDAR.md`: the "never configure a health check" warnings replaced,
      each pointing at what superseded it rather than being quietly deleted
- [x] **`curl` added to the image**, which nearly went unnoticed — see below

**The image had no HTTP client, and that would have broken every deploy.** A container health
check runs *inside* the container, and `mcr.microsoft.com/dotnet/aspnet:10.0` ships neither
`curl` nor `wget`. Configuring the check as documented would have produced a container that could
never report healthy, and with rolling updates now enabled a deploy waits for exactly that — so
every deploy would have hung, on the change whose entire purpose was to make deploys safer.
Checked by running the probe inside the built image rather than by assuming: `curl` reaches
`http://localhost:8080/health/live` and gets `200`.

`bash` is already in the base image, so adding `curl` gives an attacker who reached code
execution nothing they did not already have.
