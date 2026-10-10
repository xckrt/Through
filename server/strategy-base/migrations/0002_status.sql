-- ранний сигнал и карта: «у меня сервис сломан / работает», без IP (только суточный хеш)
CREATE TABLE IF NOT EXISTS status (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  day       INTEGER NOT NULL,
  hour      INTEGER NOT NULL,          -- часы от 1970: для окна раннего сигнала
  asn       INTEGER NOT NULL,
  org       TEXT    NOT NULL,
  country   TEXT    NOT NULL DEFAULT '',  -- из request.cf.country
  region    TEXT    NOT NULL DEFAULT '',  -- из request.cf.region (область), без города и адреса
  service   TEXT    NOT NULL,
  blocked   INTEGER NOT NULL,          -- 1 — не работает
  protected INTEGER NOT NULL,          -- 1 — проверка под обходом, 0 — без обхода (то, что режет провайдер)
  preset    TEXT    NOT NULL DEFAULT '',
  failure   TEXT    NOT NULL DEFAULT '',  -- TlsReset, TransferStalled, ...
  iphash    TEXT    NOT NULL,
  tropa     TEXT    NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS status_alert ON status (asn, hour, protected, blocked);
CREATE INDEX IF NOT EXISTS status_day ON status (day, protected);
CREATE INDEX IF NOT EXISTS status_ip ON status (iphash, day);
