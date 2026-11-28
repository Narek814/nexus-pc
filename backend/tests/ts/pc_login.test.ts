// node --test: pc-login-start and pc-login-poll with a fake Db.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import { createHandler as startHandler } from "../../supabase/functions/pc-login-start/index.ts";
import { createHandler as pollHandler } from "../../supabase/functions/pc-login-poll/index.ts";
import { createHandler as approveHandler } from "../../supabase/functions/pc-login-approve/index.ts";
import { readFileSync } from "node:fs";
import { consumeString, pcIdFromSpki, sha256hex, startString } from "../../supabase/functions/_shared/pop.ts";
import { b64uEncode } from "../../supabase/functions/_shared/entitlement.ts";
import { deps, FakeDb, HASH_ARGS, hex, nonce, NOW, payload, PC, pcKey, post, sign, testEnv, USER_A, USER_B,
  verifyTokens, captureConsole, assertDefaultLog, randomSecret, secretBytes, type PcKey } from "./_helpers.ts";

const PRO = ["profiles", "rgb.sync", "automations", "monitoring.extended", "files.unlimited", "screen.video"];

async function startBody(signer: PcKey, spkiB64u: string, over: Record<string, unknown> = {}, signedName?: string) {
  const n = nonce(), ts = NOW, name = (over.name as string) ?? "Gaming PC";
  // E4-2: pcId is derived from the spki in the body.
  const pcId = (await pcIdFromSpki(Buffer.from(spkiB64u, "base64url"))) ?? PC;
  const sig = await sign(signer, await startString(pcId, spkiB64u, signedName ?? name, n, ts));
  return { pcId, spki: spkiB64u, name, nonce: n, ts, sig, ...over };
}

const DERIVE = JSON.parse(readFileSync(new URL("../../../shared/fixtures/pc-id/derive.v1.json", import.meta.url), "utf8")) as {
  valid: { name: string; spkiB64u: string; pcId: string }[]; invalid: { name: string; spkiB64u: string }[];
};

test("pcIdFromSpki: all fixture vectors (valid -> pcId, invalid -> null)", async () => {
  assert.ok(DERIVE.valid.length >= 5 && DERIVE.invalid.length >= 5);
  for (const v of DERIVE.valid) {
    assert.equal(await pcIdFromSpki(new Uint8Array(Buffer.from(v.spkiB64u, "base64url"))), v.pcId, v.name);
  }
  for (const v of DERIVE.invalid) {
    assert.equal(await pcIdFromSpki(new Uint8Array(Buffer.from(v.spkiB64u, "base64url"))), null, v.name);
  }
});

test("start (E4-2): pcId not derived from spki -> pc.key_required before any rpc, also for rebind", async () => {
  const k = await pcKey(), other = await pcKey();
  const otherId = (await pcIdFromSpki(other.spki))!;
  for (const stored of [null, k.spki]) {
    // Correctly signed over the foreign pcId: still refused.
    const n = nonce();
    const sig = await sign(k, await startString(otherId, k.spkiB64u, "Gaming PC", n, NOW));
    for (const body of [{ pcId: otherId, spki: k.spkiB64u, name: "Gaming PC", nonce: n, ts: NOW, sig },
      { ...(await startBody(k, k.spkiB64u)), pcId: PC }]) {
      const db = startDb(stored);
      const res = await startHandler(deps(db))(post(body));
      assert.equal(res.status, 403);
      assert.deepEqual(await res.json(), { error: "pc.key_required" });
      assert.equal(db.calls.length, 0);
    }
  }
});

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
  assert.deepEqual(db.names(), ["rate_limit_hit", "pc_key_get", "rate_limit_hit", "pc_login_start"]);
  const a = db.calls[3].args;
  assert.equal(a.p_signer_spki, hex(k.spki));
  assert.equal(a.p_spki, hex(k.spki));
  assert.equal(a.p_pc_id, await pcIdFromSpki(k.spki));
  assert.equal(a.p_name, "Gaming PC");
  assert.equal(a.p_nonce, hex(Buffer.from(body.nonce, "base64url")));
  assert.equal(a.p_ts, new Date(NOW * 1000).toISOString());
  const pep = secretBytes(env.CODE_HASH_PEPPER);
  assert.equal(a.p_device_code_hash, "\\x" + createHmac("sha256", pep).update(out.deviceCode).digest("hex"));
  assert.equal(a.p_user_code_hash, "\\x" + createHmac("sha256", pep).update(out.userCode).digest("hex"));
  assertHashesOnly(db, [out.deviceCode, out.userCode], d.lines);
  // IP limits: hashed key, buckets and limits as specified.
  const [ip, , glob] = db.calls.map((c) => c.args);
  assert.deepEqual([ip.p_bucket, ip.p_limit, ip.p_window_sec], ["edge_ip_start", 20, 600]);
  assert.equal(ip.p_key, createHmac("sha256", pep).update("nexus-ip-v1|198.51.100.7").digest("base64url"));
  assert.deepEqual([glob.p_bucket, glob.p_limit, glob.p_window_sec], ["edge_start_global", 2000, 600]);
});

test("start (E7-2): bad or foreign signatures spend the IP bucket but never edge_start_global", async () => {
  const a = await pcKey(), b = await pcKey(), stored = await pcKey();
  const good = await startBody(a, a.spkiB64u);
  const zeroSig = b64uEncode(new Uint8Array(64));
  const cases: [Uint8Array | null, Record<string, unknown>][] = [
    [null, { ...good, sig: zeroSig }], // garbage signature
    [null, await startBody(a, b.spkiB64u)], // signed by another key
    [stored.spki, good], // rebind not signed by the stored key
    [null, { ...good, ts: NOW + 1 }], // tampered signed field
  ];
  for (const [st, body] of cases) {
    const db = startDb(st);
    const res = await startHandler(deps(db))(post(body));
    assert.deepEqual(await res.json(), { error: "pc.key_required" });
    const buckets = db.calls.filter((c) => c.name === "rate_limit_hit").map((c) => c.args.p_bucket);
    assert.deepEqual(buckets, ["edge_ip_start"]);
  }
  // Valid signature: the global fuse is spent exactly once, after pc_key_get.
  const db = startDb(null);
  assert.equal((await startHandler(deps(db))(post(good))).status, 200);
  assert.deepEqual(db.calls.filter((c) => c.name === "rate_limit_hit").map((c) => c.args.p_bucket),
    ["edge_ip_start", "edge_start_global"]);
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
  const glob = new FakeDb({ rate_limit_hit: (a) => a.p_bucket !== "edge_start_global", pc_key_get: () => null });
  res = await startHandler(deps(glob))(post(await startBody(k, k.spkiB64u)));
  assert.deepEqual(await res.json(), { error: "rate.limited", retryAfter: 600 });
  assert.ok(!glob.names().includes("pc_login_start"));
  // A different client IP gets a different key.
  const db1 = startDb(null), db2 = startDb(null);
  await startHandler(deps(db1))(post(await startBody(k, k.spkiB64u), { "x-forwarded-for": "203.0.113.9, 10.0.0.1, 192.0.2.44" }));
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
    { ENT_SUB_KEY: undefined }, { ENT_SUB_KEY: randomSecret(31) },
    // E7-4: not strict base64 (b64u alphabet, no padding, whitespace, raw text) -> rejected even if long.
    { CODE_HASH_PEPPER: Buffer.alloc(33, 0xfb).toString("base64url") },
    { REFRESH_HASH_PEPPER: randomSecret(32).replace(/=+$/, "") },
    { REFRESH_DERIVE_KEY: randomSecret(48) + "\n" },
    { ENT_SUB_KEY: "this-is-a-long-utf8-passphrase-not-base64!!" },
    { CODE_HASH_PEPPER: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB=" }, // non-canonical padding bits
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
  const sub = "u_" + createHmac("sha256", secretBytes(env.ENT_SUB_KEY)).update("nexus-ent-sub-v1|" + USER_A + "|" + PC).digest("base64url");
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

// ---- approve: 20-PC limit (BE-1) ----

test("approve: pcs.limit from SQL -> 409 {error: pcs.limit}, same HTTP code as poll", async () => {
  const auth = { authorization: "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMTExIn0.c2lnbmF0dXJl" };
  const db = new FakeDb({ pc_login_approve: () => "pcs.limit" });
  const res = await approveHandler(deps(db))(post({ userCode: "abcd2345" }, auth));
  assert.equal(res.status, 409);
  assert.equal(res.headers.get("retry-after"), null);
  assert.equal(await res.text(), '{"error":"pcs.limit"}');
  assert.deepEqual(db.names(), ["rate_limit_hit", "pc_login_approve"]);
  const k = await pcKey();
  const poll = await pollHandler(deps(pollDb({ key: k,
    consume: () => [{ status: "pcs.limit", pc_id: null, user_id: null, rebound: false }] })))(post(await pollBody(k, DC)));
  assert.equal(poll.status, res.status);
});

test("poll ok: Pro token carries screen.video, Free token does not (BE-1)", async () => {
  const k = await pcKey();
  for (const [user, has] of [[USER_A, true], [USER_B, false]] as const) {
    const db = pollDb({ key: k, consume: () => [{ status: "ok", pc_id: PC, user_id: user, rebound: false }] });
    const res = await pollHandler(deps(db))(post(await pollBody(k, DC)));
    assert.equal(res.status, 200);
    const p = payload((await res.json()).entitlement);
    assert.equal((p.features as string[]).includes("screen.video"), has, user);
  }
});
