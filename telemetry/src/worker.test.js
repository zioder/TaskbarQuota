import assert from "node:assert/strict";
import test from "node:test";
import { isoWeekPeriod, utcPeriods, validatePing } from "./worker.js";

const id = "a".repeat(64);

function ping(overrides = {}) {
  const now = new Date("2026-09-12T15:00:00Z");
  const periods = utcPeriods(now);
  return {
    now,
    body: {
      schema: 1,
      day: { period: periods.day, id },
      week: { period: periods.week, id: "b".repeat(64) },
      month: { period: periods.month, id: "c".repeat(64) },
      version: "1.3.2",
      channel: "github",
      ...overrides,
    },
  };
}

test("utcPeriods uses ISO weeks", () => {
  assert.deepEqual(utcPeriods(new Date("2026-09-12T23:00:00Z")), {
    day: "2026-09-12",
    week: "2026-W37",
    month: "2026-09",
  });
  assert.equal(isoWeekPeriod(new Date("2025-12-29T00:00:00Z")), "2026-W01");
  assert.equal(isoWeekPeriod(new Date("2027-01-01T00:00:00Z")), "2026-W53");
});

test("validatePing accepts a current UTC payload", () => {
  const { body, now } = ping();
  assert.equal(validatePing(body, now), null);
});

test("validatePing rejects identifiers, channels, and stale periods", () => {
  const { now } = ping();
  assert.equal(validatePing({ schema: 2 }, now), "invalid_schema");
  assert.equal(validatePing(ping({ channel: "web" }).body, now), "invalid_channel");
  assert.equal(validatePing(ping({ version: "no spaces allowed" }).body, now), "invalid_version");
  assert.equal(
    validatePing(ping({ day: { period: "2026-09-10", id } }).body, now),
    "stale_day",
  );
  assert.equal(
    validatePing(ping({ week: { period: "2026-W30", id: "b".repeat(64) } }).body, now),
    "stale_week",
  );
});

test("validatePing allows an adjacent ISO week across a year boundary", () => {
  const now = new Date("2026-01-01T12:00:00Z");
  const { body } = ping();
  body.day = { period: "2026-01-01", id };
  body.week = { period: "2025-W52", id: "b".repeat(64) };
  body.month = { period: "2026-01", id: "c".repeat(64) };
  assert.equal(validatePing(body, now), null);
});
