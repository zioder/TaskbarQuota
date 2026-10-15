const HASH = /^[a-f0-9]{64}$/;
const DAY = /^\d{4}-\d{2}-\d{2}$/;
const WEEK = /^\d{4}-W\d{2}$/;
const MONTH = /^\d{4}-\d{2}$/;
const VERSION = /^[0-9A-Za-z.+_-]{1,32}$/;
const CHANNELS = new Set(["store", "github"]);

export default {
  async fetch(request, env) {
    return handleRequest(request, env);
  },
};

export async function handleRequest(request, env) {
  const url = new URL(request.url);

  if (request.method === "OPTIONS") {
    return cors(new Response(null, { status: 204 }));
  }

  if (request.method === "POST" && url.pathname === "/v1/ping") {
    return cors(await handlePing(request, env));
  }

  if (request.method === "GET" && (url.pathname === "/v1/stats" || url.pathname === "/")) {
    const unauthorized = requireStatsToken(url, env);
    if (unauthorized) return cors(unauthorized);
    if (url.pathname === "/") return cors(await handleDashboard(env));
    return cors(await handleStats(env));
  }

  return cors(json({ error: "not_found" }, 404));
}

export function utcPeriods(now = new Date()) {
  const day = now.toISOString().slice(0, 10);
  const month = day.slice(0, 7);
  return { day, week: isoWeekPeriod(now), month };
}

export function validatePing(body, now = new Date()) {
  if (!body || body.schema !== 1) return "invalid_schema";
  if (!isPeriodId(body.day, DAY) || !isPeriodId(body.week, WEEK) || !isPeriodId(body.month, MONTH)) {
    return "invalid_ids";
  }
  if (typeof body.version !== "string" || !VERSION.test(body.version)) return "invalid_version";
  if (!CHANNELS.has(body.channel)) return "invalid_channel";

  const current = utcPeriods(now);
  if (!isRecentDay(body.day.period, current.day)) return "stale_day";
  if (!isSameOrAdjacentIsoWeek(body.week.period, current.week)) return "stale_week";
  if (!isSameOrAdjacentMonth(body.month.period, current.month)) return "stale_month";
  return null;
}

export function isoWeekPeriod(date) {
  const utc = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()));
  const day = utc.getUTCDay() || 7;
  utc.setUTCDate(utc.getUTCDate() + 4 - day);
  const yearStart = new Date(Date.UTC(utc.getUTCFullYear(), 0, 1));
  const week = Math.ceil(((utc - yearStart) / 86400000 + 1) / 7);
  return `${utc.getUTCFullYear()}-W${String(week).padStart(2, "0")}`;
}

async function handlePing(request, env) {
  let body;
  try {
    body = await request.json();
  } catch {
    return json({ error: "invalid_json" }, 400);
  }

  const error = validatePing(body);
  if (error) return json({ error }, 400);

  const seenAt = new Date().toISOString();
  try {
    await Promise.all([
      record(env.DB, "day", body.day.period, body.day.id, body.channel, body.version, seenAt),
      record(env.DB, "week", body.week.period, body.week.id, body.channel, body.version, seenAt),
      record(env.DB, "month", body.month.period, body.month.id, body.channel, body.version, seenAt),
    ]);
  } catch (cause) {
    console.error("telemetry insert failed", cause);
    return json({ error: "unavailable" }, 503);
  }

  return new Response(null, { status: 204 });
}

async function handleStats(env) {
  return json(await collectStats(env));
}

async function handleDashboard(env) {
  const stats = await collectStats(env);
  const html = `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1"/>
  <title>TaskbarQuota usage</title>
  <style>
    :root { color-scheme: dark; }
    body { font: 16px/1.5 ui-sans-serif, system-ui, sans-serif; margin: 0; background: #111; color: #eee; }
    main { max-width: 720px; margin: 0 auto; padding: 32px 20px; }
    h1 { font-size: 1.4rem; font-weight: 650; }
    p { color: #bbb; }
    .cards { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; margin: 24px 0; }
    .card { background: #1c1c1c; border: 1px solid #2a2a2a; border-radius: 12px; padding: 16px; }
    .label { color: #999; font-size: 0.8rem; text-transform: uppercase; letter-spacing: 0.04em; }
    .value { font-size: 2rem; font-weight: 700; margin-top: 4px; }
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: 8px 0; border-bottom: 1px solid #2a2a2a; }
    th { color: #999; font-size: 0.8rem; font-weight: 600; }
  </style>
</head>
<body>
  <main>
    <h1>TaskbarQuota anonymous usage</h1>
    <p>Unique rotating IDs received for the current UTC day, ISO week, and month. No personal data is stored.</p>
    <div class="cards">
      <div class="card"><div class="label">Daily</div><div class="value">${stats.day.users}</div><div class="label">${stats.day.period}</div></div>
      <div class="card"><div class="label">Weekly</div><div class="value">${stats.week.users}</div><div class="label">${stats.week.period}</div></div>
      <div class="card"><div class="label">Monthly</div><div class="value">${stats.month.users}</div><div class="label">${stats.month.period}</div></div>
    </div>
    <h2>Last 30 days</h2>
    <table>
      <thead><tr><th>Day</th><th>Users</th><th>Store</th><th>GitHub</th></tr></thead>
      <tbody>
        ${stats.history.map((row) => `<tr><td>${row.period}</td><td>${row.users}</td><td>${row.store}</td><td>${row.github}</td></tr>`).join("")}
      </tbody>
    </table>
  </main>
</body>
</html>`;
  return new Response(html, { headers: { "content-type": "text/html; charset=utf-8" } });
}

async function collectStats(env) {
  const current = utcPeriods();
  const [day, week, month, historyRows, channelRows] = await Promise.all([
    count(env.DB, "day", current.day),
    count(env.DB, "week", current.week),
    count(env.DB, "month", current.month),
    env.DB.prepare(
      `SELECT period, COUNT(*) AS users,
              SUM(CASE WHEN channel = 'store' THEN 1 ELSE 0 END) AS store,
              SUM(CASE WHEN channel = 'github' THEN 1 ELSE 0 END) AS github
       FROM uniques
       WHERE bucket = 'day'
       GROUP BY period
       ORDER BY period DESC
       LIMIT 30`,
    ).all(),
    env.DB.prepare(
      `SELECT bucket, period, channel, COUNT(*) AS users
       FROM uniques
       WHERE (bucket = 'day' AND period = ?)
          OR (bucket = 'week' AND period = ?)
          OR (bucket = 'month' AND period = ?)
       GROUP BY bucket, period, channel`,
    ).bind(current.day, current.week, current.month).all(),
  ]);

  const channels = { day: emptyChannels(), week: emptyChannels(), month: emptyChannels() };
  for (const row of channelRows?.results ?? []) {
    if (channels[row.bucket] && (row.channel === "store" || row.channel === "github")) {
      channels[row.bucket][row.channel] = Number(row.users) || 0;
    }
  }

  return {
    day: { period: current.day, users: day, channels: channels.day },
    week: { period: current.week, users: week, channels: channels.week },
    month: { period: current.month, users: month, channels: channels.month },
    history: (historyRows?.results ?? []).map((row) => ({
      period: row.period,
      users: Number(row.users) || 0,
      store: Number(row.store) || 0,
      github: Number(row.github) || 0,
    })),
  };
}

async function record(db, bucket, period, id, channel, version, seenAt) {
  await db.prepare(
    `INSERT INTO uniques (bucket, period, id, channel, version, seen_at)
     VALUES (?, ?, ?, ?, ?, ?)
     ON CONFLICT(bucket, period, id) DO UPDATE SET
       channel = excluded.channel,
       version = excluded.version,
       seen_at = excluded.seen_at`,
  ).bind(bucket, period, id, channel, version, seenAt).run();
}

async function count(db, bucket, period) {
  const row = await db.prepare(
    "SELECT COUNT(*) AS users FROM uniques WHERE bucket = ? AND period = ?",
  ).bind(bucket, period).first();
  return Number(row?.users) || 0;
}

function emptyChannels() {
  return { store: 0, github: 0 };
}

function isPeriodId(value, periodPattern) {
  return value
    && typeof value.period === "string"
    && periodPattern.test(value.period)
    && typeof value.id === "string"
    && HASH.test(value.id);
}

function isRecentDay(period, today) {
  const value = Date.parse(`${period}T00:00:00Z`);
  const current = Date.parse(`${today}T00:00:00Z`);
  if (Number.isNaN(value) || Number.isNaN(current)) return false;
  const deltaDays = Math.abs(value - current) / 86400000;
  return deltaDays <= 1;
}

function isSameOrAdjacentIsoWeek(period, current) {
  const left = isoWeekMondayUtc(period);
  const right = isoWeekMondayUtc(current);
  if (left === null || right === null) return false;
  return Math.abs(left - right) <= 7 * 86400000;
}

function isSameOrAdjacentMonth(period, current) {
  return monthIndex(period) !== null
    && monthIndex(current) !== null
    && Math.abs(monthIndex(period) - monthIndex(current)) <= 1;
}

function isoWeekMondayUtc(period) {
  const match = /^(\d{4})-W(\d{2})$/.exec(period);
  if (!match) return null;
  const year = Number(match[1]);
  const week = Number(match[2]);
  const jan4 = new Date(Date.UTC(year, 0, 4));
  const weekday = jan4.getUTCDay() || 7;
  const monday = new Date(jan4);
  monday.setUTCDate(jan4.getUTCDate() - (weekday - 1) + (week - 1) * 7);
  return monday.getTime();
}

function monthIndex(period) {
  const match = /^(\d{4})-(\d{2})$/.exec(period);
  if (!match) return null;
  return Number(match[1]) * 12 + Number(match[2]);
}

function requireStatsToken(url, env) {
  if (!env?.STATS_TOKEN) return null;
  if (url.searchParams.get("token") === env.STATS_TOKEN) return null;
  return json({ error: "unauthorized" }, 401);
}

function json(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json; charset=utf-8" },
  });
}

function cors(response) {
  const headers = new Headers(response.headers);
  headers.set("access-control-allow-origin", "*");
  headers.set("access-control-allow-methods", "GET, POST, OPTIONS");
  headers.set("access-control-allow-headers", "content-type");
  return new Response(response.body, { status: response.status, headers });
}
