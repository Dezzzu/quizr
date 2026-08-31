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

So in Coolify the application must be **1 replica**, and the old container must **stop before**
the new one starts. A rolling deployment that briefly overlaps them is not merely untidy here;
it breaks the bot until one is killed. If you see `409 Conflict` in the logs, this is why.

For the same reason there is no port to expose and no HTTP health check to configure. Nothing
listens (`STACK.md`) — Coolify should treat this as a plain worker.

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
7. **Set replicas to 1** and the deployment strategy to stop-then-start. See above.
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
