// node --test: pc-login-start and pc-login-poll with a fake Db.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import { createHandler as startHandler } from "../../supabase/functions/pc-login-start/index.ts";
import { createHandler as pollHandler } from "../../supabase/functions/pc-login-poll/index.ts";
import { consumeString, sha256hex, startString } from "../../supabase/functions/_shared/pop.ts";
import { b64uEncode } from "../../supabase/functions/_shared/entitlement.ts";
import { deps, FakeDb, HASH_ARGS, hex, nonce, NOW, payload, PC, pcKey, post, sign, testEnv, USER_A, USER_B,
  verifyTokens, captureConsole, assertDefaultLog, type PcKey } from "./_helpers.ts";

const PRO = ["profiles", "rgb.sync", "automations", "monitoring.extended", "files.unlimited"];

async function startBody(signer: PcKey, spkiB64u: string, over: Record<string, unknown> = {}, signedName?: string) {
  const n = nonce(), ts = NOW, name = (over.name as string) ?? "Gaming PC";
  const sig = await sign(signer, await startString(PC, spkiB64u, signedName ?? name, n, ts));
  return { pcId: PC, spki: spkiB64u, name, nonce: n, ts, sig, ...over };
}

function startDb(stored: Uint8Array | null, status = "ok") {
  return new FakeDb({ pc_key_get: () => (stored ? hex(stored) : null), pc_login_start: () => status });
}

function assertHashesOnly(db: FakeDb, secrets: string[], lines: string[]) {
  for (const c of db.calls) {
    for (const k of HASH_ARGS) {
      if (k in c.args) assert.match(c.args[k] as string, /^\\x[0-9a-f]{64}$/, k);
    }
    const s = JSON.stringify(c.args);
    for (const sec of secrets) assert.ok(!s.includes(sec), "secret in rpc args of " + c.name);
    assert.ok(!s.includes("198.51.100.7"), "IP in rpc args");
  }
  for (const l of lines) {
    for (const sec of secrets) assert.ok(!l.includes(sec), "secret in log");
    assert.ok(!l.includes("198.51.100.7"));
  }
}

test("start, first binding: signer = spki, nonce/ts from signed string, only HMACs reach SQL", async () => {
  const k = await pcKey();
  const env = testEnv();
  const db = startDb(null);
  const d = deps(db, env);
  const body = await startBody(k, k.spkiB64u);
  const res = await startHandler(d)(post(body));
  assert.equal(res.status, 200);
  const out = await res.json();
  assert.deepEqual(Object.keys(out).sort(), ["deviceCode", "expiresIn", "interval", "userCode"]);
  assert.equal(out.expiresIn, 300);
  assert.equal(out.interval, 5);
  assert.match(out.userCode, /^[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{8}$/);
  assert.equal(Buffer.from(out.deviceCode, "base64url").length, 32);
  assert.deepEqual(db.names(), ["rate_limit_hit", "rate_limit_hit", "pc_key_get", "pc_login_start"]);
  const a = db.calls[3].args;
  assert.equal(a.p_signer_spki, hex(k.spki));
  assert.equal(a.p_spki, hex(k.spki));
  assert.equal(a.p_pc_id, PC);
  assert.equal(a.p_name, "Gaming PC");
  assert.equal(a.p_nonce, hex(Buffer.from(body.nonce, "base64url")));
  assert.equal(a.p_ts, new Date(NOW * 1000).toISOString());
  const pep = secretBytes(env.CODE_HASH_PEPPER);
  assert.equal(a.p_device_code_hash, "\\x" + createHmac("sha256", pep).update(out.deviceCode).digest("hex"));
  assert.equal(a.p_user_code_hash, "\\x" + createHmac("sha256", pep).update(out.userCode).digest("hex"));
  assertHashesOnly(db, [out.deviceCode, out.userCode], d.lines);
  // IP limits: hashed key, buckets and limits as specified.
  const [ip, glob] = db.calls.map((c) => c.args);
  assert.deepEqual([ip.p_bucket, ip.p_limit, ip.p_window_sec], ["edge_ip_start", 20, 600]);
  assert.equal(ip.p_key, createHmac("sha256", pep).update("nexus-ip-v1|198.51.100.7").digest("base64url"));
  assert.deepEqual([glob.p_bucket, glob.p_limit, glob.p_window_sec], ["edge_start_global", 2000, 600]);
});

test("start: signature by key A with spki = B (first binding) -> pc.key_required, no pc_login_start", async () => {
  const a = await pcKey(), b = await pcKey();
  const db = startDb(null);
  const res = await startHandler(deps(db))(post(await startBody(a, b.spkiB64u)));
  assert.equal(res.status, 403);
  assert.deepEqual(await res.json(), { error: "pc.key_required" });
  assert.ok(!db.names().includes("pc_login_start"));
});

test("start, rebind: only the stored key signs; signer passed to SQL is the stored key", async () => {
  const old = await pcKey(), nw = await pcKey();
  let db = startDb(old.spki);
  let res = await startHandler(deps(db))(post(await startBody(nw, nw.spkiB64u)));
  assert.deepEqual(await res.json(), { error: "pc.key_required" });
  assert.ok(!db.names().includes("pc_login_start"));
  db = startDb(old.spki);
  res = await startHandler(deps(db))(post(await startBody(old, nw.spkiB64u)));
  assert.equal(res.status, 200);
  const a = db.calls.find((c) => c.name === "pc_login_start")!.args;
  assert.equal(a.p_signer_spki, hex(old.spki));
  assert.equal(a.p_spki, hex(nw.spki));
});

test("start: tampered name, pcId or ts -> pc.key_required without SQL write", async () => {
  const k = await pcKey();
  for (const over of [{}, { ts: NOW + 1 }]) {
    const db = startDb(null);
    const body = await startBody(k, k.spkiB64u, { name: "Other" }, "Gaming PC");
    const res = await startHandler(deps(db))(post({ ...body, ...over }));
    assert.deepEqual(await res.json(), { error: "pc.key_required" });
    assert.ok(!db.names().includes("pc_login_start"));
  }
  const db = startDb(null);
  const body = await startBody(k, k.spkiB64u);
  const res = await startHandler(deps(db))(post({ ...body, pcId: "96a77e46-afba-4ab0-8f08-68ff922e9cc2" }));
  assert.deepEqual(await res.json(), { error: "pc.key_required" });
  assert.ok(!db.names().includes("pc_login_start"));
});

test("start: malformed SPKI, signature, nonce, name, pcId -> 400 with no rpc at all", async () => {
  const k = await pcKey();
  const sp = Buffer.from(k.spki);
  const comp = Buffer.concat([Buffer.from([0x30, 0x39]), sp.subarray(2, 24), Buffer.from([0x22, 0x00, 0x02 | (sp[90] & 1)]), sp.subarray(27, 59)]);
  const p384 = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-384" }, true, ["sign"]);
  const p384spki = Buffer.from(await crypto.subtle.exportKey("spki", p384.publicKey));
  const off = Buffer.from(sp); off[90] ^= 1;
  const good = await startBody(k, k.spkiB64u);
  const sig = Buffer.from(good.sig, "base64url");
  const der = Buffer.concat([Buffer.from([0x30, 0x46, 0x02, 0x21, 0x00]), sig.subarray(0, 32), Buffer.from([0x02, 0x21, 0x00]), sig.subarray(32)]);
  const cases: Record<string, unknown>[] = [
    { spki: comp.toString("base64url") },
    { spki: p384spki.toString("base64url") },
    { spki: Buffer.concat([sp, Buffer.from([0])]).toString("base64url") },
    { spki: off.toString("base64url") },
    { spki: sp.toString("base64") },
    { sig: der.toString("base64url") },
    { sig: sig.subarray(0, 63).toString("base64url") },
    { sig: Buffer.concat([sig, Buffer.from([0])]).toString("base64url") },
    { nonce: b64uEncode(new Uint8Array(15)) },
    { nonce: b64uEncode(new Uint8Array(33)) },
    { name: "" }, { name: "x".repeat(65) }, { name: "a‮b" }, { name: "a\u0007" }, { name: "a\ud800" },
    { pcId: PC.toUpperCase() },
    { ts: "1795348800" }, { ts: 1.5 },
    { extra: 1 },
  ];
  for (const over of cases) {
    const db = startDb(null);
    const res = await startHandler(deps(db))(post({ ...good, ...over }));
    assert.equal(res.status, 400, JSON.stringify(over));
    assert.deepEqual(await res.json(), { error: "bad.request" });
    assert.equal(db.calls.length, 0, JSON.stringify(over));
  }
  const db = startDb(null);
  const { name: _n, ...missing } = good;
  assert.equal((await startHandler(deps(db))(post(missing))).status, 400);
  const dup = JSON.stringify(good).replace("{", `{"pcId":"${PC}",`);
  assert.equal((await startHandler(deps(db))(post(dup))).status, 400);
  assert.equal((await startHandler(deps(db))(new Request("http://e/f", { method: "GET" }))).status, 405);
  assert.equal((await startHandler(deps(db))(post(JSON.stringify({ ...good, name: "x".repeat(8200) })))).status, 413);
  assert.equal(db.calls.length, 0);
});

test("start: IP limit before signature check, with retryAfter; global limit; SQL statuses", async () => {
  const k = await pcKey();
  const lim = new FakeDb({ rate_limit_hit: (a) => a.p_bucket !== "edge_ip_start" });
  let res = await startHandler(deps(lim))(post(await startBody(k, k.spkiB64u)));
  assert.equal(res.status, 429);
  assert.equal(res.headers.get("retry-after"), "600");
  assert.deepEqual(await res.json(), { error: "rate.limited", retryAfter: 600 });
  assert.deepEqual(lim.names(), ["rate_limit_hit"]);
  const glob = new FakeDb({ rate_limit_hit: (a) => a.p_bucket !== "edge_start_global" });
  res = await startHandler(deps(glob))(post(await startBody(k, k.spkiB64u)));
  assert.deepEqual(await res.json(), { error: "rate.limited", retryAfter: 600 });
  assert.ok(!glob.names().includes("pc_key_get"));
  // A different client IP gets a different key.
  const db1 = startDb(null), db2 = startDb(null);
  await startHandler(deps(db1))(post(await startBody(k, k.spkiB64u), { "x-forwarded-for": "203.0.113.9, 10.0.0.1" }));
  await startHandler(deps(db2))(post(await startBody(k, k.spkiB64u)));
  assert.notEqual(db1.calls[0].args.p_key, db2.calls[0].args.p_key);
  for (const [st, code, http, ra] of [["rate.limited", "rate.limited", 429, 600], ["nonce.replay", "nonce.replay", 409, undefined],
    ["request.stale", "request.stale", 400, undefined], ["pc.key_required", "pc.key_required", 403, undefined],
    ["weird", "internal", 500, undefined]] as const) {
    const db = startDb(null, st);
    const r = await startHandler(deps(db))(post(await startBody(k, k.spkiB64u)));
    assert.equal(r.status, http);
    assert.deepEqual(await r.json(), ra ? { error: code, retryAfter: ra } : { error: code });
  }
});

test("config: short or missing secret, missing ENT_* or db -> 500 internal, no SQL, no name in body", async () => {
  const k = await pcKey();
  for (const over of [{ CODE_HASH_PEPPER: "x".repeat(31) }, { REFRESH_HASH_PEPPER: undefined }, { REFRESH_DERIVE_KEY: "" },
    { ENT_ISS: undefined }, { ENT_KID: undefined }, { ENT_SIGNING_KEY_PKCS8_B64: "AAAA" }]) {
    const db = startDb(null);
    const res = await startHandler(deps(db, testEnv(over)))(post(await startBody(k, k.spkiB64u)));
    assert.equal(res.status, 500);
    assert.equal(await res.text(), '{"error":"internal"}');
    assert.equal(db.calls.length, 0);
  }
  const res = await startHandler(deps(null))(post(await startBody(k, k.spkiB64u)));
  assert.equal(res.status, 500);
});

// ---- poll ----

interface PollState { key: PcKey; consume: (a: Record<string, unknown>) => unknown; codePc?: string; ent?: (u: string) => unknown }

function pollDb(s: PollState) {
  return new FakeDb({
    pc_login_code_key: () => [{ pc_id: s.codePc ?? PC, public_key_spki: hex(s.key.spki) }],
    pc_login_consume: s.consume,
    entitlement_for_user: (a) => s.ent ? s.ent(a.p_user_id as string)
      : [a.p_user_id === USER_A ? { state: "active", plan: "pro", features: PRO } : { state: "active", plan: "free", features: [] }],
  });
}

async function pollBody(k: PcKey, deviceCode: string, over: Record<string, unknown> = {}) {
  const n = nonce(20), ts = NOW;
  return { pcId: PC, deviceCode, nonce: n, ts, sig: await sign(k, await consumeString(PC, deviceCode, n, ts)), ...over };
}

const DC = b64uEncode(new Uint8Array(32).fill(7));

test("poll ok: Pro by user_id from consume, token verifies, no user_id/rebound, only HMACs", async () => {
  const k = await pcKey();
  const env = testEnv();
  const db = pollDb({ key: k, consume: () => [{ status: "ok", pc_id: PC, user_id: USER_A, rebound: true }] });
  const d = deps(db, env);
  const body = await pollBody(k, DC);
  const res = await pollHandler(d)(post(body));
  assert.equal(res.status, 200);
  const out = await res.json();
  assert.deepEqual(Object.keys(out).sort(), ["entitlement", "entitlementState", "refreshToken"]);
  assert.equal(out.entitlementState, "active");
  assert.equal(Buffer.from(out.refreshToken, "base64url").length, 32);
  const p = payload(out.entitlement);
  assert.equal(p.plan, "pro");
  assert.deepEqual(p.features, PRO);
  assert.equal(p.pcId, PC);
  assert.equal(p.exp as number - (p.iat as number), 86400);
  assert.equal(p.graceUntil as number - (p.exp as number), 604800);
  const sub = "u_" + Buffer.from(await crypto.subtle.digest("SHA-256", new TextEncoder().encode("nexus-ent-sub-v1|" + USER_A))).toString("base64url");
  assert.equal(p.sub, sub);
  assert.ok(!JSON.stringify(out).includes(USER_A));
  assert.match(verifyTokens([out.entitlement], PC), /^tokens: 1\/1 ok$/m);
  assert.deepEqual(db.names(), ["rate_limit_hit", "pc_login_code_key", "pc_login_consume", "entitlement_for_user"]);
  const [ip, look, cons, ent] = db.calls.map((c) => c.args);
  assert.deepEqual([ip.p_bucket, ip.p_limit, ip.p_window_sec], ["edge_ip_poll", 300, 600]);
  const dch = "\\x" + createHmac("sha256", secretBytes(env.CODE_HASH_PEPPER)).update(DC).digest("hex");
  assert.equal(look.p_device_code_hash, dch);
  assert.equal(cons.p_device_code_hash, dch);
  assert.equal(cons.p_signer_spki, hex(k.spki));
  assert.equal(cons.p_nonce, hex(Buffer.from(body.nonce, "base64url")));
  assert.equal(cons.p_ts, new Date(NOW * 1000).toISOString());
  assert.equal(cons.p_refresh_hash, "\\x" + createHmac("sha256", secretBytes(env.REFRESH_HASH_PEPPER)).update(out.refreshToken).digest("hex"));
  assert.equal(ent.p_user_id, USER_A);
  assertHashesOnly(db, [DC, out.refreshToken, out.entitlement], d.lines);
  assert.ok(d.lines.every((l) => !l.includes(USER_A) && !l.includes(PC)));
});

test("poll: Free and revoked owners get verifiable free tokens; unlinked -> revoked without token", async () => {
  const k = await pcKey();
  const toks: string[] = [];
  for (const [row, state] of [[{ state: "active", plan: "free", features: [] }, "active"],
    [{ state: "revoked", plan: "free", features: [] }, "revoked"]] as const) {
    const db = pollDb({ key: k, consume: () => [{ status: "ok", pc_id: PC, user_id: USER_B, rebound: false }], ent: () => [row] });
    const out = await (await pollHandler(deps(db))(post(await pollBody(k, DC)))).json();
    assert.equal(out.entitlementState, state);
    assert.equal(payload(out.entitlement).plan, "free");
    assert.deepEqual(payload(out.entitlement).features, []);
    toks.push(out.entitlement);
  }
  assert.match(verifyTokens(toks, PC), /^tokens: 2\/2 ok$/m);
  const db = pollDb({ key: k, consume: () => [{ status: "ok", pc_id: PC, user_id: USER_B, rebound: false }],
    ent: () => [{ state: "unlinked", plan: "free", features: [] }] });
  const res = await pollHandler(deps(db))(post(await pollBody(k, DC)));
  assert.equal(await res.text(), '{"error":"revoked"}');
});

test("poll: signature by another key, unknown code, code of another pcId -> no consume", async () => {
  const k = await pcKey(), other = await pcKey();
  let db = pollDb({ key: k, consume: () => assert.fail("consume called") });
  let res = await pollHandler(deps(db))(post(await pollBody(other, DC)));
  assert.deepEqual(await res.json(), { error: "pc.key_required" });
  db = pollDb({ key: k, consume: () => assert.fail("consume called") });
  res = await pollHandler(deps(db))(post({ ...(await pollBody(k, DC)), deviceCode: b64uEncode(new Uint8Array(32).fill(8)) }));
  assert.deepEqual(await res.json(), { error: "pc.key_required" });
  db = new FakeDb({ pc_login_code_key: () => [] });
  res = await pollHandler(deps(db))(post(await pollBody(k, DC)));
  assert.deepEqual(await res.json(), { error: "code.invalid" });
  db = pollDb({ key: k, codePc: "96a77e46-afba-4ab0-8f08-68ff922e9cc2", consume: () => assert.fail("consume called") });
  res = await pollHandler(deps(db))(post(await pollBody(k, DC)));
  assert.deepEqual(await res.json(), { error: "code.invalid" });
  db = new FakeDb({});
  res = await pollHandler(deps(db))(post(await pollBody(k, "short")));
  assert.equal(res.status, 400);
  assert.equal(db.calls.length, 0);
});

test("poll: pending 202, slow_down retryAfter 5, statuses pass through; new refresh hash each poll", async () => {
  const k = await pcKey();
  const hashes: string[] = [];
  for (const [st, http, body] of [["pending", 202, { error: "pending" }], ["slow_down", 429, { error: "slow_down", retryAfter: 5 }],
    ["code.invalid", 400, { error: "code.invalid" }], ["pcs.limit", 409, { error: "pcs.limit" }],
    ["pc.key_required", 403, { error: "pc.key_required" }], ["request.stale", 400, { error: "request.stale" }],
    ["nonce.replay", 409, { error: "nonce.replay" }]] as const) {
    const db = pollDb({ key: k, consume: (a) => { hashes.push(a.p_refresh_hash as string); return [{ status: st, pc_id: null, user_id: null, rebound: false }]; } });
    const res = await pollHandler(deps(db))(post(await pollBody(k, DC)));
    assert.equal(res.status, http, st);
    assert.deepEqual(await res.json(), body);
    if (st === "slow_down") assert.equal(res.headers.get("retry-after"), "5");
    assert.ok(!db.names().includes("entitlement_for_user"));
  }
  assert.equal(new Set(hashes).size, hashes.length);
  const lim = new FakeDb({ rate_limit_hit: () => false });
  const res = await pollHandler(deps(lim))(post(await pollBody(k, DC)));
  assert.deepEqual(await res.json(), { error: "rate.limited", retryAfter: 600 });
  assert.deepEqual(lim.names(), ["rate_limit_hit"]);
});

test("default logger: console.* gets only fn/status/ms", async () => {
  const k = await pcKey();
  const seen: string[] = [];
  const orig = { log: console.log, warn: console.warn, error: console.error, info: console.info, debug: console.debug };
  for (const m of Object.keys(orig) as (keyof typeof orig)[]) console[m] = (...a: unknown[]) => { seen.push(a.map(String).join(" ")); };
  try {
    const db = pollDb({ key: k, consume: () => [{ status: "ok", pc_id: PC, user_id: USER_A, rebound: false }] });
    const d = { db, config: deps(db).config, now: () => NOW };
    const out = await (await pollHandler(d)(post(await pollBody(k, DC)))).json();
    assert.equal(seen.length, 1);
    assert.deepEqual(Object.keys(JSON.parse(seen[0])).sort(), ["fn", "ms", "status"]);
    for (const s of [DC, out.refreshToken, USER_A, "198.51.100.7"]) assert.ok(!seen[0].includes(s));
  } finally {
    Object.assign(console, orig);
  }
});

test("sha256hex(deviceCode) in the signed string is checked against the sent deviceCode", async () => {
  assert.equal((await consumeString(PC, DC, "N", 1)).split("|")[3], await sha256hex(DC));
});

test("default logger (pc-login-start): console.* gets only fn/status/ms", async () => {
  const k = await pcKey();
  const db = startDb(null);
  const req = post(await startBody(k, k.spkiB64u));
  const { result: res, out } = await captureConsole(() =>
    startHandler(deps(db, testEnv(), { log: undefined, secLog: undefined }))(req));
  assert.equal(res.status, 200);
  const o = await res.json();
  assertDefaultLog(out, "pc-login-start", 200, [o.deviceCode, o.userCode, k.spkiB64u, PC, USER_A]);
});
