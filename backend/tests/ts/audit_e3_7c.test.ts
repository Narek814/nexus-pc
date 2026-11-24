// node --test: fixes of audit E3-7 (E7-1, E7-3, E7-4, E7-5, E7-6).
import { test } from "node:test";
import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import { clientIp, isIp } from "../../supabase/functions/_shared/ratelimit.ts";
import { b64DecodeStrict, dbFromEnv, entitlementSub, loadConfig } from "../../supabase/functions/_shared/db.ts";
import { importHmacKey } from "../../supabase/functions/_shared/codes.ts";
import { createHandler as tokenHandler } from "../../supabase/functions/pc-token/index.ts";
import { refreshString } from "../../supabase/functions/_shared/pop.ts";
import { newRefreshToken } from "../../supabase/functions/_shared/codes.ts";
import { deps, FakeDb, hex, nonce, NOW, payload, PC, pcKey, post, randomSecret, secretBytes, sign, testEnv,
  USER_A, USER_B, verifyTokens } from "./_helpers.ts";

const REAL = "198.51.100.7";
const req = (h: Record<string, string>) => new Request("http://edge.test/fn", { method: "POST", headers: h });

// ---- E7-1 ----

test("clientIp: forged leading XFF entries ignored, last entry used", () => {
  assert.equal(clientIp(req({ "x-forwarded-for": `1.2.3.4, ${REAL}` })), REAL);
  assert.equal(clientIp(req({ "x-forwarded-for": `1.2.3.4,5.6.7.8,  ${REAL} ` })), REAL);
  assert.equal(clientIp(req({ "x-forwarded-for": REAL })), REAL);
  assert.equal(clientIp(req({})), "unknown");
  assert.equal(clientIp(req({ "x-forwarded-for": "1.2.3.4, " })), "unknown");
});

test("clientIp: valid cf-connecting-ip has priority; invalid one falls back to XFF", () => {
  assert.equal(clientIp(req({ "cf-connecting-ip": "203.0.113.5", "x-forwarded-for": `1.2.3.4, ${REAL}` })), "203.0.113.5");
  assert.equal(clientIp(req({ "cf-connecting-ip": "2001:DB8::1", "x-forwarded-for": REAL })), "2001:db8::1");
  for (const bad of ["1.2.3.4, 5.6.7.8", "999.1.1.1", "not-an-ip", "1.2.3", "::g", "[::1]"]) {
    assert.equal(clientIp(req({ "cf-connecting-ip": bad, "x-forwarded-for": REAL })), REAL, bad);
  }
  assert.ok(isIp("::1") && isIp("::ffff:192.0.2.1") && isIp("0.0.0.0") && isIp("255.255.255.255"));
  assert.ok(!isIp("256.0.0.1") && !isIp("01.2.3.4") && !isIp("1::2::3") && !isIp(""));
});

// ---- E7-4 ----

test("b64DecodeStrict: standard padded base64 only", () => {
  const s = randomSecret(32);
  assert.deepEqual(Buffer.from(b64DecodeStrict(s)!), Buffer.from(s, "base64"));
  for (const bad of ["", s.replace(/=+$/, ""), s + "\n", " " + s, s.replace(/^./, "*"), s.replace(/^./, "-"),
    Buffer.alloc(32, 0xfb).toString("base64url"), "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB="]) {
    assert.equal(b64DecodeStrict(bad), null, JSON.stringify(bad));
  }
});

test("loadConfig: HMAC key = decoded bytes; 32 bytes ok, 31 rejected", async () => {
  const env = testEnv();
  const cfg = (await loadConfig((n) => env[n]))!;
  const mac = Buffer.from(await crypto.subtle.sign("HMAC", cfg.codePepper, new TextEncoder().encode("x")));
  assert.deepEqual(mac, createHmac("sha256", secretBytes(env.CODE_HASH_PEPPER)).update("x").digest());
  for (const n of ["CODE_HASH_PEPPER", "REFRESH_HASH_PEPPER", "REFRESH_DERIVE_KEY", "ENT_SUB_KEY"]) {
    const short = testEnv({ [n]: randomSecret(31) });
    assert.equal(await loadConfig((x) => short[x]), null, n);
  }
});

// ---- E7-3 ----

test("entitlementSub: keyed, per PC, stable; token passes verify_tokens.py", async () => {
  const env = testEnv();
  const key = await importHmacKey(secretBytes(env.ENT_SUB_KEY));
  const PC2 = "96a77e46-afba-4ab0-8f08-68ff922e9cc2";
  const a1 = await entitlementSub(key, USER_A, PC);
  assert.equal(a1, await entitlementSub(key, USER_A, PC));
  assert.notEqual(a1, await entitlementSub(key, USER_A, PC2));
  assert.notEqual(a1, await entitlementSub(key, USER_B, PC));
  const other = await importHmacKey(secretBytes(randomSecret()));
  assert.notEqual(a1, await entitlementSub(other, USER_A, PC));
  const expect = "u_" + createHmac("sha256", secretBytes(env.ENT_SUB_KEY))
    .update("nexus-ent-sub-v1|" + USER_A + "|" + PC).digest("base64url");
  assert.equal(a1, expect);
  assert.match(a1, /^u_[A-Za-z0-9_-]{43}$/);

  // Through pc-token: sub in the issued token is this value.
  const k = await pcKey();
  const db = new FakeDb({
    pc_key_get: () => hex(k.spki),
    pc_rotate_refresh: () => [{ status: "ok", user_id: USER_A }],
    entitlement_for_user: () => [{ state: "active", plan: "free", features: [] }],
  });
  const rt = newRefreshToken(), n = nonce();
  const res = await tokenHandler(deps(db, env))(post({ pcId: PC, refreshToken: rt, nonce: n, ts: NOW,
    sig: await sign(k, await refreshString(PC, rt, n, NOW)) }));
  const out = await res.json();
  assert.equal(payload(out.entitlement).sub, expect);
  assert.match(verifyTokens([out.entitlement], PC), /^tokens: 1\/1 ok$/m);
});

// ---- E7-5 ----

test("dbFromEnv: SUPABASE_ANON_KEY required; anon apikey only with a user JWT", async () => {
  const seen: { apikey: string | null; auth: string | null }[] = [];
  const fetchFn = (async (_u: string, init: RequestInit) => {
    const h = new Headers(init.headers);
    seen.push({ apikey: h.get("apikey"), auth: h.get("authorization") });
    return new Response("true", { status: 200 });
  }) as unknown as typeof fetch;
  const base: Record<string, string | undefined> = { SUPABASE_URL: "http://pg.test", SUPABASE_SERVICE_ROLE_KEY: "svc", SUPABASE_ANON_KEY: "anon" };
  for (const miss of ["SUPABASE_URL", "SUPABASE_SERVICE_ROLE_KEY", "SUPABASE_ANON_KEY"]) {
    const e = { ...base, [miss]: undefined };
    assert.equal(dbFromEnv((n) => e[n], fetchFn), null, miss);
  }
  const db = dbFromEnv((n) => base[n], fetchFn)!;
  await db.rpc("x", {}, { userJwt: "jwt" });
  await db.rpc("x", {});
  assert.deepEqual(seen, [{ apikey: "anon", auth: "Bearer jwt" }, { apikey: "svc", auth: "Bearer svc" }]);
});

// ---- E7-6 ----

test("pc-token: 500 from issueEntitlement, retry with the same old token -> same new token (60 s window)", async () => {
  const k = await pcKey();
  const env = testEnv();
  const old = newRefreshToken();
  // Stateful fake of pc_rotate_refresh: the first call commits the rotation;
  // a retry with the same old hash and the same new hash is "ok" again
  // (the SQL 60 s retry window, tests/sql/60_pc_refresh.sql); another new hash -> reuse.
  let committed: string | null = null;
  const rotate = (a: Record<string, unknown>) => {
    if (committed === null) { committed = a.p_new_hash as string; return [{ status: "ok", user_id: USER_A }]; }
    return [{ status: committed === a.p_new_hash ? "ok" : "reuse.detected", user_id: USER_A }];
  };
  let failEnt = true;
  const db = new FakeDb({
    pc_key_get: () => hex(k.spki),
    pc_rotate_refresh: rotate,
    entitlement_for_user: () => {
      if (failEnt) { failEnt = false; throw new Error("db down"); }
      return [{ state: "active", plan: "free", features: [] }];
    },
  });
  const bodyFor = async () => {
    const n = nonce();
    return { pcId: PC, refreshToken: old, nonce: n, ts: NOW, sig: await sign(k, await refreshString(PC, old, n, NOW)) };
  };
  const d = deps(db, env);
  const r1 = await tokenHandler(d)(post(await bodyFor()));
  assert.equal(r1.status, 500);
  assert.deepEqual(await r1.json(), { error: "internal" });
  const r2 = await tokenHandler(d)(post(await bodyFor()));
  assert.equal(r2.status, 200);
  const out = await r2.json();
  const expectNew = createHmac("sha256", secretBytes(env.REFRESH_DERIVE_KEY))
    .update("nexus-pc-refresh-derive-v1|" + old).digest("base64url");
  assert.equal(out.refreshToken, expectNew);
  const rot = db.calls.filter((c) => c.name === "pc_rotate_refresh");
  assert.equal(rot.length, 2);
  assert.equal(rot[0].args.p_new_hash, rot[1].args.p_new_hash);
  assert.match(verifyTokens([out.entitlement], PC), /^tokens: 1\/1 ok$/m);
});
