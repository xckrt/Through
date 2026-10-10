// node test.mjs — Worker против D1 на node:sqlite: отчёты, рекомендации, лимит, никаких IP в базе.
import { DatabaseSync } from "node:sqlite";
import { readFileSync } from "node:fs";
import assert from "node:assert/strict";
import worker from "./src/worker.js";

const db = new DatabaseSync(":memory:");
db.exec(readFileSync(new URL("./schema.sql", import.meta.url), "utf8"));
db.exec(readFileSync(new URL("./migrations/0002_status.sql", import.meta.url), "utf8"));

// D1 поверх node:sqlite: prepare().bind().all()/first()/run(), batch()
const D1 = {
  prepare(sql) {
    const stmt = db.prepare(sql);
    const make = (args) => ({
      bind: (...a) => make(a),
      all: async () => ({ results: stmt.all(...args) }),
      first: async () => stmt.get(...args) ?? null,
      run: async () => stmt.run(...args),
    });
    return make([]);
  },
  async batch(list) { for (const s of list) await s.run(); },
};
const env = { DB: D1, SALT: "test" };

function req(path, { method = "GET", body, ip = "1.1.1.1", asn = 12389, org = "Rostelecom" } = {}) {
  const r = new Request("https://base.test" + path, {
    method, body: body ? JSON.stringify(body) : undefined,
    headers: { "Content-Type": "application/json", "CF-Connecting-IP": ip },
  });
  Object.defineProperty(r, "cf", { value: { asn, asOrganization: org } });
  return r;
}

async function call(path, opts) {
  const res = await worker.fetch(req(path, opts), env);
  return { status: res.status, body: await res.json() };
}

// один человек — ещё не рекомендация
let r = await call("/v1/report", { method: "POST", ip: "10.0.0.1", body: { tropa: "1.2.0", engine: "v72", results: [{ service: "youtube", preset: "yt-seqovl-681", ok: true }, { service: "youtube", preset: "fake-ttl", ok: false }] } });
assert.equal(r.status, 200); assert.equal(r.body.stored, 2);
r = await call("/v1/strategies");
assert.deepEqual(r.body.services, {}, "один отчёт — не рекомендация");

// второй человек — появляется, и рабочая выше нерабочей
await call("/v1/report", { method: "POST", ip: "10.0.0.2", body: { results: [{ service: "youtube", preset: "yt-seqovl-681", ok: true }, { service: "youtube", preset: "fake-ttl", ok: false }] } });
r = await call("/v1/strategies");
assert.equal(r.body.asn, 12389);
assert.equal(r.body.services.youtube[0].preset, "yt-seqovl-681");
assert.equal(r.body.services.youtube[1].preset, "fake-ttl");

// у другого провайдера своя картина
r = await call("/v1/strategies", { asn: 8359, org: "MTS" });
assert.deepEqual(r.body.services, {});

// мусор отбрасывается, аргументы winws не проходят
r = await call("/v1/report", { method: "POST", ip: "10.0.0.3", body: { results: [{ service: "youtube", preset: "--dpi-desync=fake", ok: true }, { service: "x y", preset: "a", ok: true }] } });
assert.equal(r.status, 400);

// лимит в сутки
const many = Array.from({ length: 30 }, () => ({ service: "discord", preset: "seqovl-568", ok: true }));
for (let i = 0; i < 4; i++) await call("/v1/report", { method: "POST", ip: "10.0.0.9", body: { results: many } });
r = await call("/v1/report", { method: "POST", ip: "10.0.0.9", body: { results: many } });
assert.equal(r.status, 429);

// ранний сигнал: трое под одной стратегией видят поломку, у одного из них уже сработала другая
for (const ip of ["20.0.0.1", "20.0.0.2", "20.0.0.3"])
  await call("/v1/report", { method: "POST", ip, body: { status: [{ service: "discord", blocked: true, protected: true, preset: "seqovl-568", failure: "TlsReset" }] } });
await call("/v1/report", { method: "POST", ip: "20.0.0.1", body: { results: [{ service: "discord", preset: "fake-multi", ok: true }] } });
r = await call("/v1/alerts");
assert.equal(r.body.alerts.length, 1);
assert.equal(r.body.alerts[0].service, "discord");
assert.equal(r.body.alerts[0].preset, "seqovl-568");
assert.equal(r.body.alerts[0].fix, "fake-multi");
r = await call("/v1/alerts", { asn: 8359 });
assert.equal(r.body.alerts.length, 0, "чужой провайдер не тревожится");

// карта: «без обхода режут» у двух людей
for (const ip of ["30.0.0.1", "30.0.0.2"])
  await call("/v1/report", { method: "POST", ip, body: { status: [{ service: "youtube", blocked: true, protected: false, failure: "TransferStalled" }] } });
r = await call("/v1/map");
const yt = r.body.rows.find(x => x.service === "youtube");
assert.equal(yt.blockedShare, 1);
assert.equal(yt.failure, "TransferStalled");
assert.equal(yt.fix, "yt-seqovl-681");

// мусор в статусе не проходит
r = await call("/v1/report", { method: "POST", ip: "30.0.0.9", body: { status: [{ service: "youtube", blocked: "yes", protected: false }] } });
assert.equal(r.status, 400);

// в базе нет IP
const dump = JSON.stringify(db.prepare("SELECT * FROM reports").all()) + JSON.stringify(db.prepare("SELECT * FROM status").all());
assert.ok(!dump.includes("10.0.0."), "IP не должен попадать в базу");

console.log("worker: все проверки прошли");
