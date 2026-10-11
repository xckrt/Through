// Общая база стратегий Тропы: «у этого провайдера YouTube чинит стратегия X» и «сейчас у многих сломался Discord».
// Провайдера и регион Worker узнаёт сам по соединению (request.cf): клиент не шлёт ни IP, ни идентификаторов.
// IP в базу не пишется: для ограничения частоты хранится только хеш (IP + день + соль), он меняется каждые сутки.
//
//   GET  /v1/strategies   что работает у провайдера того, кто спрашивает
//   GET  /v1/alerts       что у этого провайдера массово сломалось под обходом за последние часы и что помогло
//   POST /v1/report       {tropa, engine, results: [{service, preset, ok}], status: [{service, blocked, protected, preset?, failure?}]}
//   GET  /v1/map          провайдеры × сервисы за 7 дней: доля «режут», чем, что помогает (для карты блокировок)
//   GET  /v1/catalog      обновляемый список доменов для обхода: {version, services:{service:[domains]}}
//   POST /v1/crash        сводка об ошибке (по согласию): {app, version, type, frames[], os} — без сообщений и путей
//   GET  /v1/crashes      сводка ошибок для разработчика, заголовок Authorization: Bearer <ADMIN_KEY>

const ID = /^[a-z0-9][a-z0-9-]{0,63}$/;
const FAILURE = /^[A-Za-z]{1,32}$/;
const DAYS = 60;          // рекомендации: последние два месяца, провайдеры меняют ТСПУ
const MIN_USERS = 2;      // одиночный отчёт не рекомендация
const MAX_PER_DAY = 120;  // записей с одного адреса в сутки
const ALERT_HOURS = 3;    // окно раннего сигнала
const ALERT_USERS = 3;    // сколько разных людей должны увидеть поломку
const MAP_DAYS = 7;

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    try {
      if (req.method === "OPTIONS") return cors(new Response(null, { status: 204 }));
      if (url.pathname === "/v1/strategies" && req.method === "GET") return cors(await strategies(req, env));
      if (url.pathname === "/v1/alerts" && req.method === "GET") return cors(await alerts(req, env));
      if (url.pathname === "/v1/report" && req.method === "POST") return cors(await report(req, env));
      if (url.pathname === "/v1/map" && req.method === "GET") return cors(await map(env));
      if (url.pathname === "/v1/catalog" && req.method === "GET") return cors(catalog());
      if (url.pathname === "/v1/crash" && req.method === "POST") return cors(await crash(req, env));
      if (url.pathname === "/v1/crashes" && req.method === "GET") return await crashes(req, env);
      if (url.pathname === "/") return json({ name: "tropa-strategy-base", docs: "https://github.com/xckrt/Tropa" });
      return json({ error: "not found" }, 404);
    } catch (e) {
      return json({ error: "internal" }, 500);
    }
  },
};

function provider(req) {
  return {
    asn: Number(req.cf?.asn ?? 0),
    org: String(req.cf?.asOrganization ?? "").slice(0, 80),
    country: String(req.cf?.country ?? "").slice(0, 2),
    region: String(req.cf?.region ?? "").slice(0, 60),
  };
}

const today = () => Math.floor(Date.now() / 86400000);
const hourNow = () => Math.floor(Date.now() / 3600000);

async function strategies(req, env) {
  const { asn, org } = provider(req);
  if (!asn) return json({ asn: 0, org, services: {} });
  const rows = await env.DB.prepare(
    `SELECT service, preset, SUM(ok) AS ok, COUNT(*) - SUM(ok) AS fail, COUNT(DISTINCT iphash) AS users
       FROM reports WHERE asn = ?1 AND day >= ?2
      GROUP BY service, preset HAVING users >= ?3`
  ).bind(asn, today() - DAYS, MIN_USERS).all();

  const services = {};
  for (const r of rows.results ?? []) {
    // оценка Лапласа: 1 из 1 не обгоняет 40 из 45
    const score = (r.ok + 1) / (r.ok + r.fail + 2);
    (services[r.service] ??= []).push({ preset: r.preset, ok: r.ok, fail: r.fail, users: r.users, score: Math.round(score * 1000) / 1000 });
  }
  for (const list of Object.values(services)) list.sort((a, b) => b.score - a.score);
  return json({ asn, org, services }, 200, { "Cache-Control": "public, max-age=900" });
}

// ранний сигнал: под обходом с одной и той же стратегией сервис сломался у нескольких людей за последние часы
async function alerts(req, env) {
  const { asn, org } = provider(req);
  if (!asn) return json({ asn: 0, org, alerts: [] });
  const since = hourNow() - ALERT_HOURS;
  const broken = await env.DB.prepare(
    `SELECT service, preset, COUNT(DISTINCT iphash) AS users, MIN(hour) AS since
       FROM status WHERE asn = ?1 AND hour >= ?2 AND protected = 1 AND blocked = 1 AND preset <> ''
      GROUP BY service, preset HAVING users >= ?3`
  ).bind(asn, since, ALERT_USERS).all();

  const list = [];
  for (const b of broken.results ?? []) {
    // что за последние сутки у этого провайдера чинило сервис (кроме сломавшейся)
    const fix = await env.DB.prepare(
      `SELECT preset, COUNT(DISTINCT iphash) AS users FROM reports
        WHERE asn = ?1 AND service = ?2 AND ok = 1 AND day >= ?3 AND preset <> ?4
        GROUP BY preset ORDER BY users DESC LIMIT 1`
    ).bind(asn, b.service, today() - 1, b.preset).first();
    list.push({ service: b.service, preset: b.preset, users: b.users, since: b.since * 3600, fix: fix?.preset ?? null });
  }
  return json({ asn, org, alerts: list }, 200, { "Cache-Control": "public, max-age=300" });
}

async function report(req, env) {
  const p = provider(req);
  if (!p.asn) return json({ error: "no asn" }, 400);
  const body = await req.json().catch(() => null);
  const results = (Array.isArray(body?.results) ? body.results.slice(0, 30) : [])
    .filter(r => ID.test(String(r?.service)) && ID.test(String(r?.preset)) && typeof r?.ok === "boolean");
  const status = (Array.isArray(body?.status) ? body.status.slice(0, 30) : [])
    .filter(s => ID.test(String(s?.service)) && typeof s?.blocked === "boolean" && typeof s?.protected === "boolean"
      && (s.preset == null || ID.test(String(s.preset))) && (s.failure == null || FAILURE.test(String(s.failure))));
  if (results.length + status.length === 0) return json({ error: "nothing to store" }, 400);
  const tropa = String(body?.tropa ?? "").slice(0, 20);
  const engine = String(body?.engine ?? "").slice(0, 20);

  const day = today();
  const hour = hourNow();
  const ip = req.headers.get("CF-Connecting-IP") ?? "";
  const iphash = await sha256(`${ip}|${day}|${env.SALT ?? ""}`);
  const used = await env.DB.prepare(
    "SELECT (SELECT COUNT(*) FROM reports WHERE iphash = ?1 AND day = ?2) + (SELECT COUNT(*) FROM status WHERE iphash = ?1 AND day = ?2) AS n"
  ).bind(iphash, day).first();
  if ((used?.n ?? 0) + results.length + status.length > MAX_PER_DAY) return json({ error: "too many reports today" }, 429);

  const insR = env.DB.prepare(
    "INSERT INTO reports (day, asn, org, service, preset, ok, iphash, tropa, engine) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)");
  const insS = env.DB.prepare(
    `INSERT INTO status (day, hour, asn, org, country, region, service, blocked, protected, preset, failure, iphash, tropa)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)`);
  await env.DB.batch([
    ...results.map(r => insR.bind(day, p.asn, p.org, r.service, r.preset, r.ok ? 1 : 0, iphash, tropa, engine)),
    ...status.map(s => insS.bind(day, hour, p.asn, p.org, p.country, p.region, s.service, s.blocked ? 1 : 0, s.protected ? 1 : 0,
      s.preset ?? "", s.failure ?? "", iphash, tropa)),
  ]);
  return json({ stored: results.length + status.length, asn: p.asn, org: p.org });
}

// карта: что у провайдера режут без обхода (status.protected = 0) и что помогает (reports)
async function map(env) {
  const rows = await env.DB.prepare(
    `SELECT asn, MAX(org) AS org, MAX(country) AS country, service,
            COUNT(DISTINCT iphash) AS users, SUM(blocked) AS blocked, COUNT(*) AS total
       FROM status WHERE day >= ?1 AND protected = 0
      GROUP BY asn, service HAVING users >= ?2 ORDER BY users DESC LIMIT 600`
  ).bind(today() - MAP_DAYS, MIN_USERS).all();
  const failures = await env.DB.prepare(
    `SELECT asn, service, failure, COUNT(*) AS n FROM status
      WHERE day >= ?1 AND protected = 0 AND blocked = 1 AND failure <> '' GROUP BY asn, service, failure`
  ).bind(today() - MAP_DAYS).all();
  const fixes = await env.DB.prepare(
    `SELECT asn, service, preset, COUNT(DISTINCT iphash) AS users FROM reports
      WHERE day >= ?1 AND ok = 1 GROUP BY asn, service, preset`
  ).bind(today() - DAYS).all();

  const top = (list, key) => {
    const best = new Map();
    for (const x of list) {
      const k = `${x.asn}|${x.service}`;
      if (!best.has(k) || best.get(k)[key] < x[key]) best.set(k, x);
    }
    return best;
  };
  const f = top(failures.results ?? [], "n");
  const fx = top(fixes.results ?? [], "users");
  const out = (rows.results ?? []).map(r => ({
    asn: r.asn, org: r.org, country: r.country, service: r.service, users: r.users,
    blockedShare: Math.round((r.blocked / r.total) * 100) / 100,
    failure: f.get(`${r.asn}|${r.service}`)?.failure ?? null,
    fix: fx.get(`${r.asn}|${r.service}`)?.preset ?? null,
  }));
  return json({ days: MAP_DAYS, updated: new Date().toISOString(), rows: out }, 200, { "Cache-Control": "public, max-age=1800" });
}

const FRAME = /^[A-Za-z_][A-Za-z0-9_.`<>+]{0,200}$/;
const CRASH_APPS = new Set(["tray", "service", "watchdog"]);

async function crash(req, env) {
  const b = await req.json().catch(() => null);
  const app = String(b?.app ?? "");
  const version = String(b?.version ?? "").slice(0, 20);
  const type = String(b?.type ?? "");
  const frames = Array.isArray(b?.frames) ? b.frames.slice(0, 8).map(String) : [];
  if (!CRASH_APPS.has(app) || !/^[0-9][0-9A-Za-z.+-]{0,19}$/.test(version) || !FRAME.test(type) || !frames.every(f => FRAME.test(f)))
    return json({ error: "bad report" }, 400);
  const os = String(b?.os ?? "").replace(/[^0-9A-Za-z .()-]/g, "").slice(0, 60);
  const day = today();
  const user = (await sha256(`${req.headers.get("CF-Connecting-IP") ?? ""}|${day}|${env.SALT ?? ""}`)).slice(0, 12);
  const signature = await sha256(`${app}|${type}|${frames.join("|")}`);
  const row = await env.DB.prepare("SELECT count, users FROM crashes WHERE signature = ?1 AND day = ?2 AND version = ?3").bind(signature, day, version).first();
  if (row) {
    const users = row.users ? row.users.split(",") : [];
    if (!users.includes(user) && users.length < 50) users.push(user);
    else if (users.includes(user) && row.count > 200) return json({ stored: false }); // одна машина не забивает базу
    await env.DB.prepare("UPDATE crashes SET count = count + 1, users = ?4 WHERE signature = ?1 AND day = ?2 AND version = ?3")
      .bind(signature, day, version, users.join(",")).run();
  } else {
    await env.DB.prepare("INSERT INTO crashes (signature, day, app, version, type, frames, os, users) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)")
      .bind(signature, day, app, version, type, JSON.stringify(frames), os, user).run();
  }
  return json({ stored: true });
}

async function crashes(req, env) {
  if (!env.ADMIN_KEY || req.headers.get("Authorization") !== `Bearer ${env.ADMIN_KEY}`) return json({ error: "forbidden" }, 403);
  const rows = await env.DB.prepare(
    `SELECT signature, app, version, type, frames, SUM(count) AS count, MAX(day) AS lastDay, GROUP_CONCAT(users) AS users
       FROM crashes WHERE day >= ?1 GROUP BY signature, version ORDER BY count DESC LIMIT 200`
  ).bind(today() - 30).all();
  const out = (rows.results ?? []).map(r => ({
    app: r.app, version: r.version, type: r.type, frames: JSON.parse(r.frames), count: r.count,
    machines: new Set(String(r.users ?? "").split(",").filter(Boolean)).size, last: new Date(r.lastDay * 86400000).toISOString().slice(0, 10),
  }));
  return json({ days: 30, crashes: out });
}

// Обновляемый список доменов для обхода: сервис → новые домены для известных Тропе сервисов.
// Клиент берёт только известные сервисы и строго проверяет каждый домен; порты и аргументы winws
// отсюда НЕ берутся и взяться не могут. Повышайте version при каждом изменении списка.
function catalog() {
  return json({
    version: 1,
    services: {
      // Пример: "youtube": ["новый-заблокированный-домен.com"],
      // Добавляйте сюда новые заблокированные домены youtube/discord и повышайте version —
      // у всех Троп список обновится сам, без обновления программы.
    },
  });
}

async function sha256(text) {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(d)].map(b => b.toString(16).padStart(2, "0")).join("");
}

function json(data, status = 200, headers = {}) {
  return new Response(JSON.stringify(data), { status, headers: { "Content-Type": "application/json; charset=utf-8", ...headers } });
}

function cors(resp) {
  const r = new Response(resp.body, resp);
  r.headers.set("Access-Control-Allow-Origin", "*");
  r.headers.set("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
  r.headers.set("Access-Control-Allow-Headers", "Content-Type");
  return r;
}
