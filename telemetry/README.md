# Anonymous usage telemetry

TaskbarQuota can send **one anonymous ping per UTC day** so you can estimate daily, weekly, and monthly active users without accounts or personal data.

## What the app sends

Each ping is a JSON body like:

```json
{
  "schema": 1,
  "day": { "period": "2026-09-12", "id": "<64-char hmac>" },
  "week": { "period": "2026-W37", "id": "<64-char hmac>" },
  "month": { "period": "2026-09", "id": "<64-char hmac>" },
  "version": "1.3.2",
  "channel": "github"
}
```

The HMAC key is a 32-byte random salt stored only in `%LOCALAPPDATA%\TaskbarQuota\anonymous-telemetry-salt.bin`. That salt never leaves the PC. Because the hashed message includes the day, ISO week, or month, the collector cannot join the same install across those windows.

The ping does **not** include names, accounts, IPs (the worker does not store them), providers, quota, or agent activity. Debug builds never send. Users can turn the ping off in Settings.

`channel` is `store` or `github`. Microsoft Store Partner Center already has its own active-device numbers; this collector is for a combined Store + GitHub estimate.

## Deploy the collector

The ingest API is a Cloudflare Worker with a D1 database.

1. Create a free Cloudflare account and run:

   ```bash
   cd telemetry
   npx wrangler login
   npx wrangler d1 create taskbarquota-telemetry
   ```

2. Paste the printed `database_id` into `wrangler.toml`.

3. Apply the schema and deploy:

   ```bash
   npx wrangler d1 execute taskbarquota-telemetry --remote --file=schema.sql
   npx wrangler deploy
   ```

4. Optional: set a stats token so the dashboard is not public:

   ```bash
   npx wrangler secret put STATS_TOKEN
   ```

5. Copy the deployed URL (for example `https://taskbarquota-telemetry.<account>.workers.dev/v1/ping`) into `AnonymousTelemetryService.DefaultIngestUrl` in `src/TaskbarQuota.App/Services/AnonymousTelemetryService.cs`. Pings are not sent while that constant is empty.

## Read DAU / WAU / MAU

- JSON: `GET /v1/stats` (add `?token=...` if `STATS_TOKEN` is set)
- HTML: `GET /`

Counts are unique IDs received for the current UTC day, ISO week, and month. A user who never launches the app that day is not counted. Opt-outs and debug builds are not counted. Clock skew around midnight UTC can split a tiny number of pings across two days.

## Local tests

```bash
cd telemetry
node --test
```
