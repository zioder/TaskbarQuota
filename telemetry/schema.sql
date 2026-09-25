CREATE TABLE IF NOT EXISTS uniques (
  bucket TEXT NOT NULL,
  period TEXT NOT NULL,
  id TEXT NOT NULL,
  channel TEXT NOT NULL,
  version TEXT NOT NULL,
  seen_at TEXT NOT NULL,
  PRIMARY KEY (bucket, period, id)
);

CREATE INDEX IF NOT EXISTS uniques_bucket_period ON uniques (bucket, period);
