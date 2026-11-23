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
const JWT_A = jwtOf({ sub: USER_A, iat: NOW - 60, role: "authenticated" });
const BEARER = "Bearer " + JWT_A;

class FakeAuth implements AuthApi {
  users: Record<string, AuthUser>;
  calls: string[] = [];
  deleteFails = false;
  constructor(users: Record<string, AuthUser>) { this.users = users; }
  async getUser(jwt: string): Promise<AuthUser | null> {
    this.calls.push("getUser");
    return this.users[jwt] ?? null;
  }
  async deleteUser(id: string): Promise<void> {
    this.calls.push("delete:" + id);
    if (this.deleteFails) throw new Error("boom");
  }
}

function setup(over: { users?: Record<string, AuthUser>; db?: FakeDb; env?: Record<string, string | undefined> } = {}) {
  const env = over.env ?? testEnv();
  const db = over.db ?? new FakeDb({ account_purge: () => "ok" });
  const auth = new FakeAuth(over.users ?? { [JWT_A]: { id: USER_A, lastSignInAt: NOW - 30 } });
  const d = { ...deps(db, env), auth };
  return { h: createHandler(d), db, auth, env, d };
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
    [{ sub: USER_A, iat: NOW - 301 }, NOW - 10], // JWT older than 5 min (refreshed later is not enough)
    [{ sub: USER_A, iat: NOW - 10 }, NOW - 301], // token refreshed, but no fresh password sign-in
    [{ sub: USER_A, iat: NOW - 10 }, null],
    [{ sub: USER_A, iat: NOW + 120 }, NOW], // future iat beyond skew
    [{ sub: USER_A, iat: "x" }, NOW],
    [{ sub: USER_A }, NOW],
    [{ sub: USER_B, iat: NOW }, NOW], // sub differs from the verified user
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
  const jwt = jwtOf({ sub: USER_A, iat: NOW - 300 });
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

test("admin delete fails after purge -> 500; retry purges again (idempotent) and succeeds", async () => {
  const { h, db, auth } = setup();
  auth.deleteFails = true;
  const r1 = await h(post({}, { authorization: BEARER }));
  assert.equal(r1.status, 500);
  assert.equal(await r1.text(), '{"error":"internal"}');
  assert.ok(db.names().includes("account_purge"));
  auth.deleteFails = false;
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
