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

Two paths out, both push-based, because the bot has no inbound anything and that is worth
keeping (`README.md`: nothing ever connects to it — it dials out).

| What | How it leaves | Needs |
| --- | --- | --- |
| Metrics | The process pushes OTLP itself | `OTEL_*` variables on the application |
| Logs | Grafana Alloy reads Docker's logs on the host | `observability/alloy.alloy`, run as its own service |

### Metrics

Set these on the Coolify application alongside the bot's own variables. They configure the
OpenTelemetry exporter directly — `Program.cs` parses none of them, it only checks whether the
endpoint is set at all, so nothing is exported when they are absent and a local run stays
silent instead of retrying an export it can never make.

| Variable | Value |
| --- | --- |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | the OTLP gateway URL from Grafana Cloud → Connections → OTLP |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` |
| `OTEL_EXPORTER_OTLP_HEADERS` | `Authorization=Basic <base64 of instanceID:token>` |

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

**Metrics only, no tracing.** An HttpClient *span* records the request URI in `url.full`, and
every Telegram call carries the bot token in its path — the same leak this file's sibling
comment in `Program.cs` already filters out of the HTTP logs. The metrics that the same
instrumentation emits are labelled with `server.address`, method and status code only, so they
carry no secret. Adding traces means scrubbing that attribute first.

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
is the alert:

```
sum(increase(quizr_scheduler_ticks_total[5m])) == 0
```

Worth pairing with an alert on `quizr_exceptions_total` by `quizr.source`, which is what would
have surfaced the swallowed per-team failures during the announcement incident, and a Loki
alert on the rate of `level="Error"` lines.

Set both to notify somewhere that is not the VPS. A monitor hosted on the box it watches
cannot tell you the box is gone, which is the main reason the stack is hosted rather than
self-run.

### Logs

`observability/alloy.alloy` is the Alloy config: it discovers every container on the host,
reads their stdout, lifts `LogLevel` into a label and writes to Grafana Cloud Loki. It covers
Postgres as well as the bot on purpose — half the evidence during the announcement incident
was Postgres' own `duplicate key value` lines, and reading them beside the bot's is the point.

**Smoke-test it by hand first.** The config has never run, and iterating on a managed
resource is a slow way to find a syntax error:

```bash
docker run --rm \
  -v /path/to/alloy.alloy:/etc/alloy/config.alloy:ro \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
  -e LOKI_URL=... -e LOKI_USERNAME=... -e LOKI_PASSWORD=... \
  grafana/alloy:v1.19.2 run /etc/alloy/config.alloy
```

Parse errors show up immediately; logs should reach Grafana → Explore → Loki within a minute,
carrying a `container` label. If it can't read the socket, add `--user root`.

**Then promote it.** `observability/docker-compose.yml` is the managed version. Two details
that are easy to get wrong and produce errors that don't name their own cause:

- It is **not** Coolify's standalone *Docker Compose* resource — that one wants pasted YAML and
  gives the file no repository to sit in, so the config it mounts would not exist. Use
  **Public Repository** and set the build pack to Docker Compose, which is what makes Coolify
  check the repo out.
- The compose file is at `/observability/docker-compose.yml`, but the **project directory is
  the repository root**, so the bind mount inside it reads `./observability/alloy.alloy`. Get
  that wrong and Docker creates a *directory* at the missing source path and then refuses to
  mount it over a file, which is what the "not a directory" OCI error means. Clean up the
  stray directory before retrying, or the next attempt fails the same way.

Set `LOKI_URL`, `LOKI_USERNAME` and `LOKI_PASSWORD` on the resource — from Grafana
Cloud → Connections → Loki, where the username is the numeric instance id, not an email, and
both differ from the OTLP credentials above.

A health check on *this* service is fine — the rolling-update hazard is specific to the bot
and its single Telegram token.

It reads the Docker socket, which is how it enumerates containers and tails their logs.
Mounting it read-only limits what the container can *do* with Docker, not what it can see —
every container's logs on the host are in scope. That is the point here, and it is also why
that one service runs as root where the bot deliberately doesn't.

### Querying it

**Do not query by container name.** Coolify names containers `<resource-uuid>-<deploy-timestamp>`
— `rrnw2v5key9vfuhdwyukwmhh-125121626027` — and the timestamp changes on every deploy, so a
query written against one stops matching after the next merge to `main`.

`alloy.alloy` relabels around this: streams are labelled with Coolify's own `resourceName`, so
the bot is `{container="quizr-bot"}` and stays that way across deploys. The relabel is a
cascade, falling back to the uuid without its timestamp and then to the raw name, so a
container Coolify didn't create still arrives labelled with something.

The image also carries `org.opencontainers.image.revision`, which is the commit that built it —
handy for answering "what is actually running" from `docker inspect`, though deliberately not a
Loki label, since it would churn a stream per deploy just as badly.

### If nothing appears in Loki

Split the problem before touching the config — the failure looks identical from the outside
whether the credentials are wrong, the pipeline is wrong, or you are reading the wrong
datasource:

```bash
curl -u "$LOKI_USERNAME:$LOKI_PASSWORD" -H 'Content-Type: application/json' -X POST "$LOKI_URL" \
  --data-raw '{"streams":[{"stream":{"job":"manual-test"},"values":[["'"$(date +%s)"'000000000","hi"]]}]}'
```

A `204` means the credentials and URL are right and the fault is downstream of them. If that
line still doesn't appear in Grafana, you are querying a different datasource than you pushed
to — a Grafana Cloud org with more than one stack makes this very easy, and it costs an hour
if you assume the pipeline is broken instead.

Nothing here is required for the bot to work. With no `OTEL_EXPORTER_OTLP_ENDPOINT` set and no
Alloy running, it behaves exactly as it did before — stdout and `QUIZR_ALERT_CHAT_ID`.

## Rolling back

Every build is pushed twice: `:latest` and `:sha-<commit>`. To roll back, point the Coolify
application at the `:sha-…` tag of a known-good commit and redeploy. Reverting on `main` and
letting the pipeline run works too, and is preferable when the bad commit included a migration —
a rollback of the image does not roll back the schema.

## Rebuild periodically

`CLAUDE.md` asks for the image to be rebuilt periodically even without code changes: `tzdata`
lives in the image, and a stale copy produces wrong offsets after a country changes its DST
rules — silently, with no error. Re-running the workflow from the Actions tab is enough.
