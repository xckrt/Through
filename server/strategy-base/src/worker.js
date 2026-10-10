// Общая база стратегий Тропы: «у этого провайдера YouTube чинит стратегия X».
// Провайдера Worker узнаёт сам по соединению (request.cf.asn): клиент не шлёт ни IP, ни идентификаторов.
// IP в базу не пишется: для ограничения частоты хранится только хеш (IP + день + соль), он меняется каждые сутки.
//
//   GET  /v1/strategies          что работает у провайдера того, кто спрашивает
//   POST /v1/report              {tropa, engine, results: [{service, preset, ok}]}
//   GET  /v1/map                 сводка по провайдерам и сервисам (для карты блокировок)

const ID = /^[a-z0-9][a-z0-9-]{0,63}$/;
const DAYS = 60;          // учитываем последние два месяца: провайдеры меняют ТСПУ
const MIN_USERS = 2;      // одиночный отчёт не рекомендация
const MAX_PER_DAY = 60;   // отчётов с одного адреса в сутки

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    try {
      if (req.method === "OPTIONS") return cors(new Response(null, { status: 204 }));
      if (url.pathname === "/v1/strategies" && req.method === "GET") return cors(await strategies(req, env));
      if (url.pathname === "/v1/report" && req.method === "POST") return cors(await report(req, env));
      if (url.pathname === "/v1/map" && req.method === "GET") return cors(await map(env));
      if (url.pathname === "/") return json({ name: "tropa-strategy-base", docs: "https://github.com/xckrt/Tropa" });
      return json({ error: "not found" }, 404);
    } catch (e) {
      return json({ error: "internal" }, 500);
    }
  },
};

function provider(req) {
  return { asn: Number(req.cf?.asn ?? 0), org: String(req.cf?.asOrganization ?? "").slice(0, 80) };
}

function today() {
  return Math.floor(Date.now() / 86400000);
}

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

async function report(req, env) {
  const { asn, org } = provider(req);
  if (!asn) return json({ error: "no asn" }, 400);
  const body = await req.json().catch(() => null);
  const results = Array.isArray(body?.results) ? body.results.slice(0, 30) : [];
  const clean = results.filter(r => ID.test(String(r?.service)) && ID.test(String(r?.preset)) && typeof r?.ok === "boolean");
  if (clean.length === 0) return json({ error: "nothing to store" }, 400);
  const tropa = String(body?.tropa ?? "").slice(0, 20);
  const engine = String(body?.engine ?? "").slice(0, 20);

  const day = today();
  const ip = req.headers.get("CF-Connecting-IP") ?? "";
  const iphash = await sha256(`${ip}|${day}|${env.SALT ?? ""}`);
  const used = await env.DB.prepare("SELECT COUNT(*) AS n FROM reports WHERE iphash = ?1 AND day = ?2").bind(iphash, day).first();
  if ((used?.n ?? 0) + clean.length > MAX_PER_DAY) return json({ error: "too many reports today" }, 429);

  const insert = env.DB.prepare(
    "INSERT INTO reports (day, asn, org, service, preset, ok, iphash, tropa, engine) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)");
  await env.DB.batch(clean.map(r => insert.bind(day, asn, org, r.service, r.preset, r.ok ? 1 : 0, iphash, tropa, engine)));
  return json({ stored: clean.length, asn, org });
}

async function map(env) {
  const rows = await env.DB.prepare(
    `SELECT asn, org, service, COUNT(DISTINCT iphash) AS users, SUM(ok) AS ok, COUNT(*) AS total
       FROM reports WHERE day >= ?1 GROUP BY asn, service HAVING users >= ?2 ORDER BY users DESC LIMIT 500`
  ).bind(today() - DAYS, MIN_USERS).all();
  return json({ days: DAYS, rows: rows.results ?? [] }, 200, { "Cache-Control": "public, max-age=3600" });
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
