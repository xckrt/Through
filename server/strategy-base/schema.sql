-- Общая база стратегий Тропы (Cloudflare D1)
CREATE TABLE IF NOT EXISTS reports (
  id      INTEGER PRIMARY KEY AUTOINCREMENT,
  day     INTEGER NOT NULL,          -- сутки от 1970-01-01 (без точного времени)
  asn     INTEGER NOT NULL,          -- провайдер, из request.cf.asn
  org     TEXT    NOT NULL,          -- его название, из request.cf.asOrganization
  service TEXT    NOT NULL,          -- youtube, discord, ...
  preset  TEXT    NOT NULL,          -- id стратегии из presets.json
  ok      INTEGER NOT NULL,          -- 1 — починила, 0 — не помогла
  iphash  TEXT    NOT NULL,          -- sha256(IP | день | соль): только для лимита и подсчёта людей, меняется каждые сутки
  tropa   TEXT    NOT NULL DEFAULT '',
  engine  TEXT    NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS reports_asn ON reports (asn, day);
CREATE INDEX IF NOT EXISTS reports_ip ON reports (iphash, day);
