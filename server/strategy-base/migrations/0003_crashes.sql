-- сводки об ошибках (только по согласию): тип исключения и имена методов, без сообщений, путей и адресов
CREATE TABLE IF NOT EXISTS crashes (
  signature TEXT    NOT NULL,          -- sha256(app | type | кадры): одинаковые ошибки склеиваются
  day       INTEGER NOT NULL,
  app       TEXT    NOT NULL,          -- tray, service, watchdog
  version   TEXT    NOT NULL,
  type      TEXT    NOT NULL,          -- System.IO.IOException
  frames    TEXT    NOT NULL,          -- JSON-массив «Namespace.Type.Method»
  os        TEXT    NOT NULL DEFAULT '',
  count     INTEGER NOT NULL DEFAULT 1,
  users     TEXT    NOT NULL DEFAULT '', -- суточные хеши через запятую (для «у скольких»), не больше 50
  PRIMARY KEY (signature, day, version)
);
