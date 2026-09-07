# Deployment

The bot runs as a single container on a VPS managed by [Coolify](https://coolify.io).
GitHub Actions builds the image and Coolify pulls it — the VPS never compiles anything.

```
push to main → build + tests (build job) → image → ghcr.io → webhook → Coolify pulls → restart
```

`.github/workflows/build.yml` owns the left-hand side. Everything right of the webhook is
configured once, by hand, in Coolify — this file is that list.

## The one thing that will bite you

**Exactly one instance may run at a time.** The bot uses long polling: two containers holding
the same token both call `getUpdates`, and Telegram answers one of them with `409 Conflict`
forever. The scheduler would also double-fire — two reminders, two auto-finishes.

There is no replica count to set: Coolify runs one container per application and has no such
setting. What could put two of them side by side is a *rolling update*, where Coolify starts the
replacement before stopping the original.

**The lever is the health check — so leave it unconfigured.** Coolify's rolling updates
[require](https://coolify.io/docs/knowledge-base/rolling-updates) "a valid health check
configured and passing", because that is how it decides the new container is ready to take over.
This bot has none and cannot meaningfully have one: nothing listens on a port (`STACK.md`), it
long-polls. With no health check a rolling update cannot proceed, which is what leaves the
deployment stopping the old container before starting the new one.

So: expose no port, configure no health check. Adding one to make the deployment look tidier is
the change that would break the bot.

Worth confirming once on your first redeploy rather than trusting it, since Coolify's docs state
the requirement without spelling out the fallback:

```bash
docker ps --filter name=quizr                 # expect exactly one container
docker logs <container> 2>&1 | grep -i conflict   # expect nothing
```

A `409 Conflict` in the logs is the unambiguous symptom of two pollers sharing one token.

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
7. **Leave the health check empty and expose no port.** See above — this is what keeps two
   containers from ever running at once.
8. **Copy the deploy webhook URL** from the application's Webhooks tab.

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

## What happens on deploy

Migrations run at startup — `Program.cs` calls `Database.MigrateAsync()` before the bot starts
polling — so there is no migration step in the pipeline and no manual one either. A deploy that
adds a migration applies it as the new container comes up.

The bot re-registers its command menu and profile description on every startup
(`CommandMenu`, `BotProfile`), so a copy change in the strings files needs nothing beyond a
deploy.

## Observability

Logs and metrics both leave the process over OTLP, pushed to a **Seq** instance. One
destination, one protocol, one set of variables — nothing is scraped, and nothing connects to
the bot, which is the property `README.md` cares about: it dials out.

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

### The heartbeat, and why there is still no health check

Everything in "The one thing that will bite you" above still holds: **configure no health
check on the bot application.** It is the lever that enables rolling updates, and a rolling
update is what puts two pollers on one token.

That leaves liveness to be answered some other way, and an HTTP probe was never the right
answer for this process anyway — the bot has no inbound traffic, so "the port answers" would
prove nothing about whether it is still polling. The failure that matters is the loop stopping
while the process stays up, which a probe cannot see.

`quizr.scheduler.ticks` is the answer instead. The scheduler runs every 30 seconds with nobody
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
