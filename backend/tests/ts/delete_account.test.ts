// node --test: delete-account (E3-8)
import { test } from "node:test";
import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import {
  authFromEnv, createHandler, DELETE_BUCKETS, GoTrueApi, type AuthApi, type AuthUser,
} from "../../supabase/functions/delete-account/index.ts";
import { assertDefaultLog, captureConsole, deps, FakeDb, NOW, post, secretBytes, testEnv, USER_A, USER_B } from "./_helpers.ts";

const b64u = (o: unknown) => Buffer.from(JSON.stringify(o)).toString("base64url");
const jwtOf = (claims: Record<string, unknown>) => `${b64u({ alg: "HS256", typ: "JWT" })}.${b64u(claims)}.c2ln`;
const AMR = (method: string, ts: unknown) => [{ method, timestamp: ts }];
const JWT_A = jwtOf({ sub: USER_A, iat: NOW - 60, amr: AMR("password", NOW - 60), role: "authenticated" });
const BEARER = "Bearer " + JWT_A;

class FakeAuth implements AuthApi {
  users: Record<string, AuthUser>;
  calls: string[] = [];
  deleteFails = 0; // number of next deleteUser calls that fail
  constructor(users: Record<string, AuthUser>) { this.users = users; }
  async getUser(jwt: string): Promise<AuthUser | null> {
    this.calls.push("getUser");
    return this.users[jwt] ?? null;
  }
  async deleteUser(id: string): Promise<void> {
    this.calls.push("delete:" + id);
    if (this.deleteFails > 0) { this.deleteFails--; throw new Error("boom"); }
  }
}

function setup(over: { users?: Record<string, AuthUser>; db?: FakeDb; env?: Record<string, string | undefined> } = {}) {
  const env = over.env ?? testEnv();
  const db = over.db ?? new FakeDb({ account_purge: () => "ok", rate_limit_refund: () => null });
  const auth = new FakeAuth(over.users ?? { [JWT_A]: { id: USER_A, lastSignInAt: NOW - 30 } });
  const slept: number[] = [];
  const d = { ...deps(db, env), auth, sleep: async (ms: number) => { slept.push(ms); } };
  return { h: createHandler(d), db, auth, env, d, slept };
}

test("ok: verified user purged then admin-deleted; user id only from GoTrue; 200 {}", async () => {
  const { h, db, auth } = setup();
  // A user id in the body is never accepted (strict body {}).
  const bad = await h(post({ userId: USER_B }, { authorization: BEARER }));
  assert.equal(bad.status, 400);
  assert.equal(db.calls.length, 0);
  const res = await h(post({}, { authorization: BEARER }));
  assert.equal(res.status, 200);
  assert.equal(await res.text(), "{}");
  assert.deepEqual(db.names(), ["rate_limit_hit", "rate_limit_hit", "account_purge"]);
  assert.deepEqual(db.calls[2].args, { p_user_id: USER_A });
  assert.equal(db.calls[2].opts, undefined); // service_role, not the user JWT
  assert.deepEqual(auth.calls, ["getUser", "delete:" + USER_A]);
});

test("anon / rejected / malformed JWT -> 401 auth.required, no rpc, no delete", async () => {
  const anon = jwtOf({ role: "anon", iat: NOW });
  for (const hdr of [{}, { authorization: "Bearer " + anon }, { authorization: "Basic x" }, { authorization: "Bearer a.b" }]) {
    const { h, db, auth } = setup();
    const res = await h(post({}, hdr));
    assert.equal(res.status, 401);
    assert.equal(await res.text(), '{"error":"auth.required"}');
    assert.equal(db.calls.length, 0);
    assert.ok(!auth.calls.some((c) => c.startsWith("delete")));
  }
});

test("stale login -> 401 reauth.required before limits and purge", async () => {
  const cases: [Record<string, unknown>, number | null][] = [
    [{ sub: USER_A, iat: NOW - 301, amr: AMR("password", NOW - 10) }, NOW - 10], // JWT older than 5 min (refreshed later is not enough)
    [{ sub: USER_A, iat: NOW - 10, amr: AMR("password", NOW - 10) }, NOW - 301], // token refreshed, but no fresh password sign-in
    [{ sub: USER_A, iat: NOW - 10, amr: AMR("password", NOW - 10) }, null],
    [{ sub: USER_A, iat: NOW + 120, amr: AMR("password", NOW) }, NOW], // future iat beyond skew
    [{ sub: USER_A, iat: "x", amr: AMR("password", NOW) }, NOW],
    [{ sub: USER_A, amr: AMR("password", NOW) }, NOW],
    [{ sub: USER_B, iat: NOW, amr: AMR("password", NOW) }, NOW], // sub differs from the verified user
  ];
  for (const [claims, last] of cases) {
    const jwt = jwtOf(claims);
    const { h, db, auth } = setup({ users: { [jwt]: { id: USER_A, lastSignInAt: last } } });
    const res = await h(post({}, { authorization: "Bearer " + jwt }));
    assert.equal(res.status, 401, JSON.stringify(claims));
    assert.equal(await res.text(), '{"error":"reauth.required"}');
    assert.equal(db.calls.length, 0);
    assert.deepEqual(auth.calls, ["getUser"]);
  }
  // Boundary: exactly 300 s old is still fresh.
  const jwt = jwtOf({ sub: USER_A, iat: NOW - 300, amr: AMR("otp", NOW - 300) });
  const { h } = setup({ users: { [jwt]: { id: USER_A, lastSignInAt: NOW - 300 } } });
  assert.equal((await h(post({}, { authorization: "Bearer " + jwt }))).status, 200);
});

test("limits: user 3/h keyed by HMAC of user id, then IP; 429 before purge", async () => {
  const env = testEnv();
  const ukey = createHmac("sha256", secretBytes(env.CODE_HASH_PEPPER)).update("nexus-user-v1|" + USER_A).digest("base64url");
  assert.deepEqual(DELETE_BUCKETS.user, { name: "edge_user_delete", limit: 3, windowSec: 3600 });
  for (const deny of ["edge_user_delete", "edge_ip_delete"]) {
    const db = new FakeDb({ rate_limit_hit: (a) => a.p_bucket !== deny, account_purge: () => "ok" });
    const { h, auth } = setup({ db, env });
    const res = await h(post({}, { authorization: BEARER }));
    assert.equal(res.status, 429);
    assert.equal(res.headers.get("retry-after"), "3600");
    assert.deepEqual(JSON.parse(await res.text()), { error: "rate.limited", retryAfter: 3600 });
    assert.ok(!db.names().includes("account_purge"));
    assert.deepEqual(auth.calls, ["getUser"]);
    const u = db.calls[0];
    assert.deepEqual(u.args, { p_bucket: "edge_user_delete", p_key: ukey, p_limit: 3, p_window_sec: 3600 });
    for (const c of db.calls) {
      const s = JSON.stringify(c.args);
      assert.ok(!s.includes(USER_A) && !s.includes("198.51.100.7"));
    }
  }
});

test("S-1: session freshness by amr password/otp; iat and last_sign_in_at stay as extra checks", async () => {
  const run = async (claims: Record<string, unknown>, last: number | null) => {
    const jwt = jwtOf(claims);
    const { h, db } = setup({ users: { [jwt]: { id: USER_A, lastSignInAt: last } } });
    const res = await h(post({}, { authorization: "Bearer " + jwt }));
    return { status: res.status, body: await res.text(), rpc: db.calls.length };
  };
  const reauth = { status: 401, body: '{"error":"reauth.required"}', rpc: 0 };
  // iat fresh (refreshed session), amr old -> reauth.
  assert.deepEqual(await run({ sub: USER_A, iat: NOW - 5, amr: AMR("password", NOW - 4000) }, NOW - 5), reauth);
  // amr fresh, last_sign_in_at old (another session signed in long ago) -> reauth.
  assert.deepEqual(await run({ sub: USER_A, iat: NOW - 5, amr: AMR("password", NOW - 5) }, NOW - 4000), reauth);
  // Both fresh -> ok (password and otp).
  for (const m of ["password", "otp"]) {
    assert.equal((await run({ sub: USER_A, iat: NOW - 5, amr: AMR(m, NOW - 5) }, NOW - 5)).status, 200, m);
  }
  // Other methods are not a re-auth (oauth, magiclink, totp, recovery, token_refresh).
  for (const m of ["oauth", "magiclink", "totp", "recovery", "token_refresh", "sso/saml", "anonymous"]) {
    assert.deepEqual(await run({ sub: USER_A, iat: NOW - 5, amr: AMR(m, NOW - 5) }, NOW - 5), reauth, m);
  }
  // A fresh oauth entry next to an old password entry is still reauth;
  // a fresh password entry among others is ok.
  assert.deepEqual(await run({ sub: USER_A, iat: NOW - 5,
    amr: [{ method: "oauth", timestamp: NOW - 5 }, { method: "password", timestamp: NOW - 4000 }] }, NOW - 5), reauth);
  assert.equal((await run({ sub: USER_A, iat: NOW - 5,
    amr: [{ method: "totp", timestamp: NOW - 4000 }, { method: "password", timestamp: NOW - 5 }] }, NOW - 5)).status, 200);
  // Missing / malformed amr, future timestamp beyond skew, boundaries.
  for (const amr of [undefined, null, [], "password", [null], [{ method: "password" }],
    [{ method: "password", timestamp: "1" }], [{ method: "password", timestamp: NOW + 61 }],
    [{ method: "password", timestamp: NOW - 301 }], [{ method: ["password"], timestamp: NOW }]]) {
    assert.deepEqual(await run({ sub: USER_A, iat: NOW - 5, amr }, NOW - 5), reauth, JSON.stringify(amr));
  }
  assert.equal((await run({ sub: USER_A, iat: NOW - 5, amr: AMR("password", NOW + 60) }, NOW - 5)).status, 200);
  assert.equal((await run({ sub: USER_A, iat: NOW - 5, amr: AMR("password", NOW - 300) }, NOW - 5)).status, 200);
});

test("S-2: admin delete retried in-function (3 attempts, 1 s backoff); success on retry -> 200, no refund", async () => {
  const { h, db, auth, slept } = setup();
  auth.deleteFails = 2;
  const res = await h(post({}, { authorization: BEARER }));
  assert.equal(res.status, 200);
  assert.deepEqual(auth.calls, ["getUser", "delete:" + USER_A, "delete:" + USER_A, "delete:" + USER_A]);
  assert.deepEqual(slept, [250, 750]);
  assert.ok(slept.reduce((a, b) => a + b, 0) <= 2000);
  assert.ok(!db.names().includes("rate_limit_refund"));
});

test("S-2: admin delete fails 3 times after purge -> 500 and the user-limit hit is refunded", async () => {
  const env = testEnv();
  const ukey = createHmac("sha256", secretBytes(env.CODE_HASH_PEPPER)).update("nexus-user-v1|" + USER_A).digest("base64url");
  const { h, db, auth, slept } = setup({ env });
  auth.deleteFails = Infinity;
  const r = await h(post({}, { authorization: BEARER }));
  assert.equal(r.status, 500);
  assert.equal(await r.text(), '{"error":"internal"}');
  assert.equal(auth.calls.filter((c) => c.startsWith("delete")).length, 3);
  assert.deepEqual(slept, [250, 750]);
  assert.deepEqual(db.names(), ["rate_limit_hit", "rate_limit_hit", "account_purge", "rate_limit_refund"]);
  assert.deepEqual(db.calls[3].args, { p_bucket: "edge_user_delete", p_key: ukey, p_window_sec: 3600 });
  // A failing refund still answers 500 (never 200).
  const db2 = new FakeDb({ account_purge: () => "ok", rate_limit_refund: () => { throw new Error("x"); } });
  const s2 = setup({ db: db2 });
  s2.auth.deleteFails = Infinity;
  assert.equal((await s2.h(post({}, { authorization: BEARER }))).status, 500);
  // Purge failure: no admin delete, no refund.
  const db3 = new FakeDb({ account_purge: () => "nope", rate_limit_refund: () => null });
  const s3 = setup({ db: db3 });
  assert.equal((await s3.h(post({}, { authorization: BEARER }))).status, 500);
  assert.ok(!db3.names().includes("rate_limit_refund"));
});

test("admin delete fails after purge -> 500; retry purges again (idempotent) and succeeds", async () => {
  const { h, db, auth } = setup();
  auth.deleteFails = Infinity;
  const r1 = await h(post({}, { authorization: BEARER }));
  assert.equal(r1.status, 500);
  assert.equal(await r1.text(), '{"error":"internal"}');
  assert.ok(db.names().includes("account_purge"));
  auth.deleteFails = 0;
  const r2 = await h(post({}, { authorization: BEARER }));
  assert.equal(r2.status, 200);
  assert.equal(db.names().filter((n) => n === "account_purge").length, 2);
});

test("purge status other than ok -> 500, no admin delete; missing config/auth -> 500 before anything", async () => {
  const { h, auth } = setup({ db: new FakeDb({ account_purge: () => "nope" }) });
  assert.equal((await h(post({}, { authorization: BEARER }))).status, 500);
  assert.deepEqual(auth.calls, ["getUser"]);
  const db = new FakeDb({});
  const noAuth = createHandler({ ...deps(db), auth: null });
  assert.equal((await noAuth(post({}, { authorization: BEARER }))).status, 500);
  const noCfg = setup({ env: testEnv({ ENT_KID: undefined }) });
  assert.equal((await noCfg.h(post({}, { authorization: BEARER }))).status, 500);
  assert.equal(noCfg.auth.calls.length, 0);
  assert.equal(db.calls.length, 0);
  assert.equal(authFromEnv(() => undefined), null);
});

test("GoTrueApi: GET /auth/v1/user with user JWT + anon apikey; admin DELETE with service key; 404 = deleted", async () => {
  const seen: { url: string; method: string; h: Record<string, string> }[] = [];
  let next: Response = new Response("{}");
  const f = (async (url: string, init: RequestInit) => {
    seen.push({ url, method: init.method!, h: init.headers as Record<string, string> });
    return next;
  }) as unknown as typeof fetch;
  const g = new GoTrueApi("https://p.example/", "svc-key", "anon-key", f);
  next = new Response(JSON.stringify({ id: USER_A, email: "x@example.test", last_sign_in_at: "2026-11-23T00:00:00Z" }));
  assert.deepEqual(await g.getUser("j.w.t"), { id: USER_A, lastSignInAt: Date.parse("2026-11-23T00:00:00Z") / 1000 });
  assert.deepEqual(seen[0], { url: "https://p.example/auth/v1/user", method: "GET",
    h: { accept: "application/json", apikey: "anon-key", authorization: "Bearer j.w.t" } });
  for (const st of [401, 403]) {
    next = new Response("{}", { status: st });
    assert.equal(await g.getUser("j.w.t"), null);
  }
  next = new Response("{}", { status: 500 });
  await assert.rejects(g.getUser("j.w.t"));
  next = new Response(JSON.stringify({ id: "not-a-uuid" }));
  await assert.rejects(g.getUser("j.w.t"));
  next = new Response("{}", { status: 200 });
  await g.deleteUser(USER_A);
  assert.deepEqual(seen.at(-1), { url: `https://p.example/auth/v1/admin/users/${USER_A}`, method: "DELETE",
    h: { accept: "application/json", apikey: "svc-key", authorization: "Bearer svc-key" } });
  next = new Response("{}", { status: 404 });
  await g.deleteUser(USER_A);
  next = new Response("{}", { status: 500 });
  await assert.rejects(g.deleteUser(USER_A));
  await assert.rejects(g.deleteUser("../x"));
});

test("default logger: only fn/status/ms, no JWT, user id or IP", async () => {
  for (const [users, st] of [[undefined, 200], [{}, 401]] as const) {
    const db = new FakeDb({ account_purge: () => "ok" });
    const auth = new FakeAuth(users ?? { [JWT_A]: { id: USER_A, lastSignInAt: NOW } });
    const h = createHandler({ db, config: deps(db).config, now: () => NOW, auth });
    const { out } = await captureConsole(() => h(post({}, { authorization: BEARER })));
    assertDefaultLog(out, "delete-account", st, [JWT_A, USER_A, "x@example.test"]);
  }
});
