// node --test: pc-token (refresh rotation + entitlement) with a fake Db.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import { createHandler } from "../../supabase/functions/pc-token/index.ts";
import { refreshString } from "../../supabase/functions/_shared/pop.ts";
import { newRefreshToken } from "../../supabase/functions/_shared/codes.ts";
import { deps, FakeDb, HASH_ARGS, hex, nonce, NOW, payload, PC, pcKey, post, sign, testEnv, USER_A, USER_B,
  verifyTokens, type PcKey } from "./_helpers.ts";

const PRO = ["profiles", "rgb.sync", "automations", "monitoring.extended", "files.unlimited"];

async function body(k: PcKey, rt: string, over: Record<string, unknown> = {}) {
  const n = nonce(), ts = NOW;
  return { pcId: PC, refreshToken: rt, nonce: n, ts, sig: await sign(k, await refreshString(PC, rt, n, ts)), ...over };
}

function tokenDb(key: PcKey | null, rotate: (a: Record<string, unknown>) => unknown,
  ent: (u: string) => unknown = (u) => [u === USER_A ? { state: "active", plan: "pro", features: PRO } : { state: "active", plan: "free", features: [] }]) {
  return new FakeDb({
    pc_key_get: () => (key ? hex(key.spki) : null),
    pc_rotate_refresh: rotate,
    entitlement_for_user: (a) => ent(a.p_user_id as string),
    // Current my_pcs owner would be USER_B: must never be asked.
    entitlement_for_pc: () => assert.fail("entitlement_for_pc must not be used"),
  });
}

test("ok: retry with the same old token gives the same new; only HMACs; Pro by rotate user_id", async () => {
  const k = await pcKey();
  const env = testEnv();
  const old = newRefreshToken();
  const outs: Record<string, string>[] = [];
  const allCalls: FakeDb[] = [];
  const lines: string[] = [];
  for (let i = 0; i < 2; i++) {
    const db = tokenDb(k, () => [{ status: "ok", user_id: USER_A }]);
    const d = deps(db, env);
    const res = await createHandler(d)(post(await body(k, old)));
    assert.equal(res.status, 200);
    outs.push(await res.json());
    allCalls.push(db);
    lines.push(...d.lines);
  }
  assert.equal(outs[0].refreshToken, outs[1].refreshToken);
  assert.notEqual(outs[0].refreshToken, old);
  const expectNew = createHmac("sha256", env.REFRESH_DERIVE_KEY!).update("nexus-pc-refresh-derive-v1|" + old).digest("base64url");
  assert.equal(outs[0].refreshToken, expectNew);
  for (const db of allCalls) {
    assert.deepEqual(db.names(), ["rate_limit_hit", "pc_key_get", "pc_rotate_refresh", "entitlement_for_user"]);
    const [ip, , rot, ent] = db.calls.map((c) => c.args);
    assert.deepEqual([ip.p_bucket, ip.p_limit, ip.p_window_sec], ["edge_ip_token", 60, 3600]);
    const hp = (s: string) => "\\x" + createHmac("sha256", env.REFRESH_HASH_PEPPER!).update(s).digest("hex");
    assert.equal(rot.p_old_hash, hp(old));
    assert.equal(rot.p_new_hash, hp(expectNew));
    assert.equal(rot.p_signer_spki, hex(k.spki));
    assert.equal(rot.p_pc_id, PC);
    assert.equal(ent.p_user_id, USER_A);
    for (const c of db.calls) {
      for (const a of HASH_ARGS) if (a in c.args) assert.match(c.args[a] as string, /^\\x[0-9a-f]{64}$/);
      const s = JSON.stringify(c.args);
      assert.ok(!s.includes(old) && !s.includes(expectNew) && !s.includes("198.51.100.7"));
    }
  }
  const p = payload(outs[0].entitlement);
  assert.equal(p.plan, "pro");
  assert.equal(outs[0].entitlementState, "active");
  assert.ok(!JSON.stringify(outs[0]).includes(USER_A));
  assert.match(verifyTokens([outs[0].entitlement, outs[1].entitlement], PC), /^tokens: 2\/2 ok$/m);
  for (const l of lines) assert.ok(!l.includes(old) && !l.includes(expectNew) && !l.includes(USER_A));
});

test("rotate user_id decides the plan (B: free), revoked -> free + revoked; unlinked -> revoked, no token", async () => {
  const k = await pcKey();
  const toks: string[] = [];
  let db = tokenDb(k, () => [{ status: "ok", user_id: USER_B }]);
  let out = await (await createHandler(deps(db))(post(await body(k, newRefreshToken())))).json();
  assert.equal(payload(out.entitlement).plan, "free");
  toks.push(out.entitlement);
  db = tokenDb(k, () => [{ status: "ok", user_id: USER_A }], () => [{ state: "revoked", plan: "free", features: [] }]);
  out = await (await createHandler(deps(db))(post(await body(k, newRefreshToken())))).json();
  assert.equal(out.entitlementState, "revoked");
  assert.equal(payload(out.entitlement).plan, "free");
  toks.push(out.entitlement);
  assert.match(verifyTokens(toks, PC), /^tokens: 2\/2 ok$/m);
  db = tokenDb(k, () => [{ status: "ok", user_id: USER_A }], () => [{ state: "unlinked", plan: "free", features: [] }]);
  const res = await createHandler(deps(db))(post(await body(k, newRefreshToken())));
  assert.equal(await res.text(), '{"error":"revoked"}');
});

test("no pc_keys row or wrong signer -> revoked without pc_rotate_refresh", async () => {
  const k = await pcKey(), other = await pcKey();
  for (const [stored, signer] of [[null, k], [k, other]] as const) {
    const db = tokenDb(stored, () => assert.fail("rotate called"));
    const res = await createHandler(deps(db))(post(await body(signer, newRefreshToken())));
    assert.equal(res.status, 401);
    assert.deepEqual(await res.json(), { error: "revoked" });
  }
  // tampered refresh token (signature over another one)
  const db = tokenDb(k, () => assert.fail("rotate called"));
  const res = await createHandler(deps(db))(post({ ...(await body(k, newRefreshToken())), refreshToken: newRefreshToken() }));
  assert.deepEqual(await res.json(), { error: "revoked" });
});

test("SQL statuses: revoked, reuse.detected (security log with pcId only), rate.limited 3600, stale, replay", async () => {
  const k = await pcKey();
  for (const [st, http, out] of [["revoked", 401, { error: "revoked" }], ["reuse.detected", 401, { error: "reuse.detected" }],
    ["rate.limited", 429, { error: "rate.limited", retryAfter: 3600 }], ["request.stale", 400, { error: "request.stale" }],
    ["nonce.replay", 409, { error: "nonce.replay" }], ["pending", 500, { error: "internal" }]] as const) {
    const db = tokenDb(k, () => [{ status: st, user_id: null }]);
    const d = deps(db);
    const old = newRefreshToken();
    const res = await createHandler(d)(post(await body(k, old)));
    assert.equal(res.status, http, st);
    assert.deepEqual(await res.json(), out);
    assert.ok(!db.names().includes("entitlement_for_user"));
    const sec = d.lines.filter((l) => l.includes("security"));
    if (st === "reuse.detected") {
      assert.equal(sec.length, 1);
      assert.deepEqual(JSON.parse(sec[0]), { security: "pc.refresh_reuse", pcId: PC });
    } else {
      assert.equal(sec.length, 0);
    }
    for (const l of d.lines) assert.ok(!l.includes(old));
  }
});

test("IP limit 60/3600 before signature, retryAfter; IP not in rpc args", async () => {
  const k = await pcKey();
  const db = new FakeDb({ rate_limit_hit: () => false });
  const res = await createHandler(deps(db))(post(await body(k, newRefreshToken())));
  assert.equal(res.status, 429);
  assert.equal(res.headers.get("retry-after"), "3600");
  assert.deepEqual(await res.json(), { error: "rate.limited", retryAfter: 3600 });
  assert.deepEqual(db.names(), ["rate_limit_hit"]);
  assert.ok(!JSON.stringify(db.calls).includes("198.51.100.7"));
});

test("malformed: refresh not 32 bytes, DER/short signature, extra field -> 400 without rpc", async () => {
  const k = await pcKey();
  const good = await body(k, newRefreshToken());
  const sig = Buffer.from(good.sig, "base64url");
  for (const over of [{ refreshToken: "abc" }, { sig: sig.subarray(0, 63).toString("base64url") },
    { sig: Buffer.concat([sig, Buffer.from([1])]).toString("base64url") }, { x: 1 }, { ts: -1 }]) {
    const db = new FakeDb({});
    const res = await createHandler(deps(db))(post({ ...good, ...over }));
    assert.equal(res.status, 400);
    assert.equal(db.calls.length, 0);
  }
});

test("rpc failure -> 500 internal without details", async () => {
  const k = await pcKey();
  const db = tokenDb(k, () => { throw new Error("db down: token xyz"); });
  const d = deps(db);
  const res = await createHandler(d)(post(await body(k, newRefreshToken())));
  assert.equal(res.status, 500);
  assert.equal(await res.text(), '{"error":"internal"}');
  assert.ok(d.lines.every((l) => !l.includes("xyz")));
});
