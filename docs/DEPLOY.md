# Deployment

The bot runs as a single container on a VPS managed by [Coolify](https://coolify.io).
GitHub Actions builds the image and Coolify pulls it — the VPS never compiles anything.

```
push to main → build + tests (build job) → image → ghcr.io → webhook → Coolify pulls → restart
```

`.github/workflows/build.yml` owns the left-hand side. Everything right of the webhook is
configured once, by hand, in Coolify — this file is that list.

## Only one instance ever does the work

**Exactly one process may poll Telegram.** Two containers holding the same token both call
`getUpdates`, and Telegram answers one of them `409 Conflict` for as long as both are running.
Worse than the conflict, the scheduler double-fires: reminders and reserve promotions are
protected by `Notification`'s unique constraint on `(SignupId, Kind)`, but **auto-finish is
not**, so an overlap would materialise a game's `Participation` rows twice.

**This is now enforced in the process rather than by deployment discipline.** `BotInstanceLock`
takes a Postgres advisory lock at startup; whoever holds it polls Telegram and runs the
scheduler, and whoever does not waits, serves health checks and does nothing. A second container
is therefore *harmless* rather than merely prevented, which is what changed the rule below. See
`docs/HEALTH.md`.

This file used to say: configure no health check, because a passing one is what lets Coolify
start the replacement before stopping the original. That was true and is now obsolete. **A
health check is safe, and worth having** — the sequence it produces is: the new container starts,
finds the lock held, stands by, reports ready, Coolify stops the old one, the lock is released,
and the new one takes over within about five seconds. Nothing polls twice, and nothing is lost:
Telegram queues updates while nobody is asking for them.

Still worth confirming on a redeploy rather than trusting it:

```bash
docker ps --filter name=quizr                      # one container once the deploy settles
docker logs <container> 2>&1 | grep -i conflict    # expect nothing, ever
docker logs <container> 2>&1 | grep -i "leading"   # "This instance is leading"
```

A `409 Conflict` would be the unambiguous symptom of two pollers sharing one token, and with the
lock in place it should now be unreachable. If you ever see one, the lock is not doing its job
and that is a bug rather than a deployment mistake.

## Coolify, once

1. **Enable the API.** Settings → Configuration → Advanced → enable API access.
2. **Create an API token.** Keys & Tokens → API Tokens, with deploy permission. Copy it; it is
   shown once.
3. **Add a Postgres service.** Coolify provisions and backs it up. Note its service name — on
   the shared Docker network that is the hostname.
4. **Add the application** as a *Docker Image* resource pointing at
   `ghcr.io/dezzzu/quizr:latest`.
5. **Let the server pull from GHCR.** The package is private by default, so on the VPS:

   ```bash
   docker login ghcr.io -u <github-username> -p <PAT with read:packages>
   ```

   Alternatively make the package public on GitHub (Packages → quizr → Package settings) and
   skip the login entirely. The image holds no secrets — they all arrive as environment
   variables — so public is a reasonable choice.
6. **Set the environment variables** (below).
7. **Set the port to 8080**, which is what the image exposes and what `ASPNETCORE_HTTP_PORTS`
   sets in the `Dockerfile`. Needed for the health check even if no domain is ever attached.
8. **Configure the health check:**

   | Field | Value |
   | --- | --- |
   | Path | `/health/ready` |
   | Port | `8080` |
   | Method | `GET` |
   | Expected status | `200` |
   | Interval | `30s` |
   | Timeout | `5s` |
   | Retries | `3` |
   | Start period | `40s` |

   `/health/ready` rather than `/health/live`, because it is the one that means something: it
   reports unhealthy when Postgres is unreachable, or when the instance is leading and its
   scheduler has stopped ticking. A standby reports healthy — it is ready to take over, which is
   exactly what Coolify is asking. The start period covers startup migrations and the first
   scheduler tick.

   The image installs `curl` for this. The `aspnet` base ships neither `curl` nor `wget`, and a
   health check runs *inside* the container, so without it the probe fails permanently and every
   deploy hangs waiting for a container that can never report healthy.

9. **Copy the deploy webhook URL** from the application's Webhooks tab.

Only if the calendar feed is wanted — the bot runs perfectly well without it:

10. **Attach a domain.** Coolify's proxy terminates TLS and forwards; nothing in the container
    needs a certificate. `Program.cs` calls `UseForwardedHeaders` with the known-proxy lists
    cleared, because that proxy's address on the Docker network is neither knowable from here
    nor stable — and the container is never reachable except through it.
11. **Set `QUIZR_PUBLIC_URL`** to that domain, with scheme and no trailing path. Until it is
    set the endpoint is not mapped at all and `/mycalendar` says the feed is unavailable, so
    steps 10-11 can be done later, or never.

## GitHub, once

Settings → Environments → **`prod`**:

| Secret | Value |
| --- | --- |
| `COOLIFY_WEBHOOK` | the deploy webhook URL from step 8 |
| `COOLIFY_TOKEN` | the API token from step 2 |

These are environment secrets rather than repository ones, which is why the deploy job declares
`environment: prod`. A job that doesn't name the environment cannot read them — it sees empty
strings, with nothing to say they exist elsewhere. If you move them, move that line too.

Nothing else. Pushing to GHCR uses the `GITHUB_TOKEN` Actions issues for each run, which is why
the deploy job asks for `packages: write` and no registry credentials.

## Environment variables

All configuration is environment variables (`CLAUDE.md`), set on the Coolify application — never
in the repository, and never as GitHub secrets, since the pipeline neither needs nor sees them.

| Variable | Required | Notes |
| --- | --- | --- |
| `QUIZR_BOT_TOKEN` | yes | From @BotFather. |
| `QUIZR_DB` | yes | `Host=<postgres-service>;Port=5432;Database=quizr;Username=quizr;Password=…` |
| `QUIZR_ALERT_CHAT_ID` | no | A chat the bot messages on an unhandled exception. Without it those are logged only. |
| `QUIZR_PUBLIC_URL` | no | e.g. `https://quizr.example.com`. Turns the calendar feed on: the endpoint is mapped and `/mycalendar` hands out links. Unset, neither exists and no tokens are issued. |
| `ASPNETCORE_HTTP_PORTS` | no | Defaults to `8080` from the `Dockerfile`. Only worth setting if Coolify needs a different port. |

## What happens on deploy

Migrations run at startup — `Program.cs` calls `Database.MigrateAsync()` before the bot starts
polling — so there is no migration step in the pipeline and no manual one either. A deploy that
adds a migration applies it as the new container comes up.

Confirm on the first deploy after the calendar feed, rather than trusting it:

```bash
docker ps --filter name=quizr                      # expect exactly one container
docker logs <container> 2>&1 | grep -i conflict    # expect nothing
curl -sI https://<domain>/api/cal/feed.ics             # expect 404 — no token, so nothing to serve
```

That last one answering `404` rather than timing out is the whole of "the domain reaches the
container", and it leaks nothing: a request with no token cannot be told from one with a wrong
token, by design.

The bot re-registers its command menu and profile description on every startup
(`CommandMenu`, `BotProfile`), so a copy change in the strings files needs nothing beyond a
deploy.

## Observability

Logs and metrics both leave the process over OTLP, pushed to a **Seq** instance. One
destination, one protocol, one set of variables — nothing is scraped and nothing connects to the
bot to collect it, which is the property that matters here: telemetry dials out, exactly like
the bot itself. The calendar feed is the one thing anything connects *to*, and it is a separate
concern from this: no metric or log leaves through it.

**Seq is not part of this deployment.** It runs as its own standalone service, shared with
other projects, reachable on the Coolify network as `http://seq`. Nothing in this repository
creates it, and nothing here should — its retention, its upgrades and its storage budget
belong to whoever owns that instance.

Set these on the Coolify application alongside the bot's own variables. `Program.cs` parses
none of them — it only checks whether the endpoint is set at all, so with none of them present
nothing is exported and a local run stays silent instead of retrying an export it can never
make. Developing against the console alone needs no Seq running and no `OTEL_*` set.

| Variable | Value |
| --- | --- |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://seq/ingest/otlp` |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` |
| `OTEL_EXPORTER_OTLP_HEADERS` | `X-Seq-ApiKey=<key>` |

The endpoint is the OTLP **base** path; the SDK appends `/v1/logs` and `/v1/metrics` itself, so
don't write a signal path into it. `http://seq` means port 80, which is where Seq serves both
its UI and its ingestion API. Plain HTTP is right here — the traffic never leaves the Docker
network, and it sidesteps gRPC's hard TLS requirement.

**Seq 2026.1 or newer is required.** That release added OTLP metric ingestion; an older Seq
accepts the logs and silently drops the metrics, which is a confusing way to find out.

### Logs

The bot exports its own logs. There is no log-shipping agent, and removing the one that used
to be here was the point of the change rather than a side effect — see below.

What arrives in Seq is better than a re-parsed console line. The exporter ships each call
site's original message template, so `LogInformation("Promoted {UserId} to game {GameId}", …)`
lands as a template with `UserId` and `GameId` as indexed properties rather than as a sentence
with two numbers buried in it. That is what `CLAUDE.md`'s "always with structured message
templates, never an interpolated string" rule was banking, and this is where it pays out.
`IncludeScopes` carries the per-update scope — update id, chat id, user id — through the same
way.

**stdout stays plain, readable text**, deliberately, because it is now aimed at a person
rather than at a parser. `docker logs` and Coolify's log viewer are where you look when the
container is restart-looping, and they are the *only* place the last few records before a hard
crash survive: the OTLP exporter batches, so a process dying on the way up takes its buffer
with it. That is the one gap in this arrangement, and it is covered by looking in the place
you would already be looking.

Because the Seq instance is shared, give the bot **its own API key** rather than reusing
another project's. A Seq API key can stamp properties onto everything ingested through it
(`Application = 'Quizr'`, say) and can be revoked on its own. The resource already sets
`service.name = quizr`, so filtering works either way — but a key-applied property holds even
if a future producer is configured sloppily. Worth confirming on the first ingest that the
stamping applies to the OTLP endpoints and not only to Seq's native ingestion API; Datalust's
API-key documentation covers the native path explicitly and is quiet about OTLP.

Postgres' own logs no longer reach Seq. They were worth having during the announcement
incident — half that evidence was `duplicate key value` lines — but `docker logs` and Coolify's
viewer still have them, and that was a forensic need rather than a monitoring one. If it ever
becomes continuous, the tool is Datalust's `seq-input-gelf` alongside a `gelf` logging driver
on the Postgres service: no Docker socket, no root container. Not Alloy.

### Metrics

Three instruments are the bot's own, and they are what the alerts below are written against —
`QuizrMetricsTests` pins their names for that reason:

- `quizr.updates` — Telegram updates handled.
- `quizr.exceptions` — tagged `error.type` and `quizr.source`, where the source is the boundary
  that caught it (`update`, `scheduler.team`, `scheduler.game`, …). Every one of those is a
  place the code deliberately swallows a failure to keep running, so this is the only signal
  that any of them is firing.
- `quizr.scheduler.ticks` — the heartbeat. See below.

Runtime and HttpClient instrumentation come along too, which is where Telegram API failure
rates show up.

**Logs and metrics only, no tracing.** An HttpClient *span* records the request URI in
`url.full`, and every Telegram call carries the bot token in its path — the same leak
`Program.cs` already filters out of the HTTP logs. The metrics that instrumentation emits are
labelled with `server.address`, method and status code only, so they carry no secret. Adding
traces means scrubbing that attribute first.

On a shared instance the metric budget is shared too. Seq's free Individual tier allows 100
million metric samples; runtime plus HttpClient instrumentation at the default 60-second export
interval is on the order of 10–15 million a year for this one bot. There is room, but set a
retention policy under Data → Storage with `series` as the deletion target before a third
project arrives, not after.

### The heartbeat, and the probe that asks the same question

The failure that matters is the scheduler loop stopping while the process stays alive. It has to
be asked about specifically: the calendar endpoint answering only proves Kestrel is up, and
`SchedulerHostedService` catches a failed tick and goes round again, so nothing crashes.

Two things watch for it now, and they are the same signal read differently.

`quizr.scheduler.ticks` is the metric: chart the series in Seq and alert when the increase over
five minutes reaches zero. **`GET /health/ready`** is the question: unhealthy when Postgres is
unreachable, or when this instance is leading and has not completed a tick in three minutes.
**`GET /health/live`** reports only that the process answered, which is deliberately almost
nothing — it exists so the two can fail separately, and stopping Postgres under a running bot
demonstrates why: liveness stays `200`, readiness turns `503`.

Coolify's own health check should point at `/health/ready` (see "Coolify, once"). An **off-box**
uptime monitor should point at it too, through the domain if one is attached — that closes the
gap the next section names, since Seq runs on the same VPS as the bot and cannot tell you the box
is gone.

Worth knowing: readiness reports healthy for an instance that holds no lock. A standby is ready —
started, serving, and deliberately idle. Demanding scheduler ticks of it would mean it could
never be declared ready, so Coolify would never stop the leader, so it could never start
ticking. The scheduler runs every 30 seconds with nobody
asking it to, so the counter advancing is proof the process is doing work, and a **gap** in it
is the alert: chart the series in Seq and create the alert from the chart, firing when the
increase over five minutes reaches zero.

Worth pairing with an alert on `quizr.exceptions` grouped by `quizr.source` — which is what
would have surfaced the swallowed per-team failures during the announcement incident — and one
on the rate of `Error`-level events.

### What still isn't covered

Seq runs on the same VPS as the bot. A monitor hosted on the box it watches cannot tell you
the box is gone, so every alert above answers "is the bot misbehaving", not "is anything
running at all".

`QUIZR_ALERT_CHAT_ID` closes part of that — the bot messages a private Telegram channel on an
unhandled exception, from its own process, off the box. What is still open is the case where
the process or the host stops entirely and nothing is left to send anything. The cheap fix is a
dead-man's switch: have the scheduler tick ping a hosted cron monitor, so silence is detected
somewhere that is not this machine. Not done yet, and deliberately not folded into the change
that introduced Seq.
