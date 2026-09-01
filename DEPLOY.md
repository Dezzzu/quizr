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

Repository → Settings → Secrets and variables → Actions:

| Secret | Value |
| --- | --- |
| `COOLIFY_WEBHOOK` | the deploy webhook URL from step 8 |
| `COOLIFY_TOKEN` | the API token from step 2 |

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

## Rolling back

Every build is pushed twice: `:latest` and `:sha-<commit>`. To roll back, point the Coolify
application at the `:sha-…` tag of a known-good commit and redeploy. Reverting on `main` and
letting the pipeline run works too, and is preferable when the bad commit included a migration —
a rollback of the image does not roll back the schema.

## Rebuild periodically

`CLAUDE.md` asks for the image to be rebuilt periodically even without code changes: `tzdata`
lives in the image, and a stale copy produces wrong offsets after a country changes its DST
rules — silently, with no error. Re-running the workflow from the Actions tab is enough.
