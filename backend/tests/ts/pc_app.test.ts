// node --test: app endpoints pc-login-describe, pc-login-approve, pc-unlink
// with a fake Db (and PostgrestDb over a fake fetch for the JWT path).
import { test } from "node:test";
import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import { createHandler as describeHandler } from "../../supabase/functions/pc-login-describe/index.ts";
import { createHandler as approveHandler } from "../../supabase/functions/pc-login-approve/index.ts";
import { createHandler as unlinkHandler } from "../../supabase/functions/pc-unlink/index.ts";
import { PostgrestDb } from "../../supabase/functions/_shared/db.ts";
import { assertDefaultLog, captureConsole, deps, FakeDb, PC, post, secretBytes, testEnv, USER_A, USER_B } from "./_helpers.ts";

const JWT = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMTExIn0.c2lnbmF0dXJl";
const AUTH = { authorization: "Bearer " + JWT };
const CODE = "ABCD2345";
const SERVICE_KEY = "service-role-key-not-a-real-one";

type H = (d: ReturnType<typeof deps>) => (req: Request) => Promise<Response>;
const CODE_EPS: [string, H, string][] = [
  ["pc-login-describe", describeHandler, "pc_login_describe"],
  ["pc-login-approve", approveHandler, "pc_login_approve"],
];

function describeDb(row: Record<string, unknown>) {
  return new FakeDb({ pc_login_describe: () => [row] });
}

test("describe ok: {displayName, requestedAt RFC 3339}, user JWT to SQL, only HMAC of normalized code", async () => {
  const env = testEnv();
  const db = describeDb({ status: "ok", display_name: "Gaming PC", requested_at: "2026-11-23T10:00:00.123456+00:00" });
  const d = deps(db, env);
  const res = await describeHandler(d)(post({ userCode: " abcd-2345 " }, AUTH));
  assert.equal(res.status, 200);
  assert.equal(await res.text(), '{"displayName":"Gaming PC","requestedAt":"2026-11-23T10:00:00Z"}');
  assert.deepEqual(db.names(), ["rate_limit_hit", "pc_login_describe"]);
  const [ip, call] = db.calls;
  assert.deepEqual([ip.args.p_bucket, ip.args.p_limit, ip.args.p_window_sec], ["edge_ip_approve", 30, 600]);
  assert.equal(ip.args.p_key, createHmac("sha256", secretBytes(env.CODE_HASH_PEPPER)).update("nexus-ip-v1|198.51.100.7").digest("base64url"));
  assert.equal(ip.opts?.userJwt, undefined); // rate_limit_hit is service_role only
  assert.deepEqual(call.opts, { userJwt: JWT });
  assert.deepEqual(call.args, { p_user_code_hash: "\\x" + createHmac("sha256", secretBytes(env.CODE_HASH_PEPPER)).update(CODE).digest("hex") });
  const all = JSON.stringify(db.calls) + d.lines.join("\n");
  for (const s of [CODE, "abcd", "198.51.100.7"]) assert.ok(!all.includes(s), s);
});

test("describe: unknown, foreign, expired, approved code -> byte-identical code.invalid", async () => {
  // SQL gives the same status for all of them; Edge must not add anything.
  const bodies: string[] = [];
  for (const row of [{ status: "code.invalid", display_name: null, requested_at: null },
    { status: "code.invalid", display_name: null, requested_at: null }]) {
    for (const uc of ["ABCD2345", "ZZZZ9999", "abcd 2345"]) {
      const res = await describeHandler(deps(describeDb(row)))(post({ userCode: uc }, AUTH));
      assert.equal(res.status, 400);
      bodies.push(res.status + JSON.stringify([...res.headers]) + (await res.text()));
    }
  }
  // Malformed codes (wrong length, letters outside the alphabet) look the same.
  for (const uc of ["ABC", "ABCD23450", "ABCD234O", "ABCD2341", "", "ÄBCD2345"]) {
    const db = describeDb({ status: "ok", display_name: "x", requested_at: "2026-11-23T10:00:00Z" });
    const res = await describeHandler(deps(db))(post({ userCode: uc }, AUTH));
    bodies.push(res.status + JSON.stringify([...res.headers]) + (await res.text()));
    assert.deepEqual(db.names(), ["rate_limit_hit"], uc); // counted by IP, no SQL
  }
  assert.equal(new Set(bodies).size, 1);
  assert.equal(bodies[0], '400[["cache-control","no-store"],["content-type","application/json"]]{"error":"code.invalid"}');
});

test("approve: ok -> {}, code.invalid, rate.limited 600; user JWT; same IP bucket as describe", async () => {
  const env = testEnv();
  for (const [st, http, out] of [["ok", 200, "{}"], ["code.invalid", 400, '{"error":"code.invalid"}'],
    ["rate.limited", 429, '{"error":"rate.limited","retryAfter":600}'], ["pending", 500, '{"error":"internal"}']] as const) {
    const db = new FakeDb({ pc_login_approve: () => st });
    const res = await approveHandler(deps(db, env))(post({ userCode: "abcd2345" }, AUTH));
    assert.equal(res.status, http, st);
    assert.equal(await res.text(), out);
    assert.equal(db.calls[0].args.p_bucket, "edge_ip_approve");
    assert.deepEqual(db.calls[1].opts, { userJwt: JWT });
    assert.deepEqual(db.calls[1].args,
      { p_user_code_hash: "\\x" + createHmac("sha256", secretBytes(env.CODE_HASH_PEPPER)).update(CODE).digest("hex") });
  }
});

test("describe: rate.limited from SQL -> 429 retryAfter 600; bad row -> 500", async () => {
  let res = await describeHandler(deps(describeDb({ status: "rate.limited" })))(post({ userCode: CODE }, AUTH));
  assert.equal(res.status, 429);
  assert.equal(res.headers.get("retry-after"), "600");
  assert.deepEqual(await res.json(), { error: "rate.limited", retryAfter: 600 });
  for (const row of [{ status: "ok", display_name: "x", requested_at: "nope" }, { status: "ok", display_name: null },
    { status: "pending" }]) {
    res = await describeHandler(deps(describeDb(row)))(post({ userCode: CODE }, AUTH));
    assert.equal(res.status, 500);
    assert.equal(await res.text(), '{"error":"internal"}');
  }
});

test("no or malformed Authorization -> 401 auth.required, no rpc at all", async () => {
  const eps: H[] = [describeHandler, approveHandler, unlinkHandler];
  const bodies = [{ userCode: CODE }, { userCode: CODE }, { pcId: PC }];
  for (const [i, h] of eps.entries()) {
    for (const hdr of [{}, { authorization: JWT }, { authorization: "Basic " + JWT }, { authorization: "Bearer " },
      { authorization: "Bearer a.b" }, { authorization: "Bearer " + JWT + " x" }, { authorization: "Bearer a.b.c=" },
      { authorization: "Bearer " + "a".repeat(4100) + ".b.c" }]) {
      const db = new FakeDb({});
      const res = await h(deps(db))(post(bodies[i], hdr));
      assert.equal(res.status, 401, JSON.stringify(hdr).slice(0, 40));
      assert.equal(await res.text(), '{"error":"auth.required"}');
      assert.equal(db.calls.length, 0);
    }
  }
});

test("IP limit (30/600) before SQL for describe/approve/unlink; IP not in rpc args", async () => {
  for (const [h, body, bucket] of [[describeHandler, { userCode: CODE }, "edge_ip_approve"],
    [approveHandler, { userCode: "bad" }, "edge_ip_approve"], [unlinkHandler, { pcId: PC }, "edge_ip_unlink"]] as const) {
    const db = new FakeDb({ rate_limit_hit: () => false });
    const res = await (h as H)(deps(db))(post(body, AUTH));
    assert.equal(res.status, 429);
    assert.equal(res.headers.get("retry-after"), "600");
    assert.deepEqual(await res.json(), { error: "rate.limited", retryAfter: 600 });
    assert.deepEqual(db.names(), ["rate_limit_hit"]);
    assert.equal(db.calls[0].args.p_bucket, bucket);
    assert.equal(db.calls[0].args.p_limit, 30);
    assert.ok(!JSON.stringify(db.calls).includes("198.51.100.7"));
  }
});

test("unlink: ok -> {}, pc.not_found (also foreign PC) 404; user JWT; no pcId/user ids in answers", async () => {
  for (const [st, http, out] of [["ok", 200, "{}"], ["pc.not_found", 404, '{"error":"pc.not_found"}'],
    ["code.invalid", 500, '{"error":"internal"}']] as const) {
    const db = new FakeDb({ pc_release: () => st });
    const res = await unlinkHandler(deps(db))(post({ pcId: PC }, AUTH));
    assert.equal(res.status, http);
    assert.equal(await res.text(), out);
    assert.deepEqual(db.names(), ["rate_limit_hit", "pc_release"]);
    assert.deepEqual(db.calls[1].args, { p_pc_id: PC });
    assert.deepEqual(db.calls[1].opts, { userJwt: JWT });
    assert.equal(db.calls[0].args.p_bucket, "edge_ip_unlink");
  }
});

test("unlink: pcId not a lower-case UUID, extra/missing fields -> 400 without rpc", async () => {
  for (const b of [{ pcId: PC.toUpperCase() }, { pcId: "x" }, { pcId: 1 }, {}, { pcId: PC, x: 1 }]) {
    const db = new FakeDb({});
    const res = await unlinkHandler(deps(db))(post(b, AUTH));
    assert.equal(res.status, 400, JSON.stringify(b));
    assert.equal(await res.text(), '{"error":"bad.request"}');
    assert.equal(db.calls.length, 0);
  }
});

test("http.ts rules hold: POST only, JSON only, 8 KB, duplicate/unknown fields, wrong type", async () => {
  for (const [name, h, rpcName] of CODE_EPS) {
    const mk = (init: RequestInit & { headers?: Record<string, string> }) => new Request("http://edge.test/fn",
      { method: "POST", ...init, headers: { "x-forwarded-for": "198.51.100.7", ...AUTH, ...init.headers } });
    const cases: [Request, number][] = [
      [new Request("http://edge.test/fn", { method: "GET", headers: AUTH }), 405],
      [mk({ headers: { "content-type": "text/plain" }, body: JSON.stringify({ userCode: CODE }) }), 400],
      [mk({ headers: { "content-type": "application/json" }, body: JSON.stringify({ userCode: CODE + " ".repeat(8200) }) }), 413],
      [mk({ headers: { "content-type": "application/json" }, body: '{"userCode":"ABCD2345","userCode":"ABCD2345"}' }), 400],
      [mk({ headers: { "content-type": "application/json" }, body: '{"userCode":"ABCD2345","pcId":"x"}' }), 400],
      [mk({ headers: { "content-type": "application/json" }, body: '{"userCode":12345678}' }), 400],
      [mk({ headers: { "content-type": "application/json" }, body: "[]" }), 400],
    ];
    for (const [req, st] of cases) {
      const db = new FakeDb({ [rpcName]: () => assert.fail("sql") });
      const res = await h(deps(db))(req);
      assert.equal(res.status, st, name);
      assert.equal(await res.text(), '{"error":"bad.request"}');
      assert.equal(db.calls.length, 0);
    }
  }
});

test("missing config -> 500 internal before JWT, body and SQL", async () => {
  for (const h of [describeHandler, approveHandler, unlinkHandler]) {
    const db = new FakeDb({});
    const res = await h(deps(db, testEnv({ CODE_HASH_PEPPER: "short" })))(post({ userCode: CODE }));
    assert.equal(res.status, 500);
    assert.equal(db.calls.length, 0);
    const res2 = await h(deps(null))(post({ userCode: CODE }, AUTH));
    assert.equal(res2.status, 500);
  }
});

test("PostgrestDb: user JWT goes as Bearer (apikey = service key); rejected JWT -> 401 auth.required", async () => {
  const seen: { url: string; auth: string | null; apikey: string | null; body: string }[] = [];
  let status = 200;
  const fetchFn = (async (url: string, init: RequestInit) => {
    const hd = new Headers(init.headers);
    seen.push({ url, auth: hd.get("authorization"), apikey: hd.get("apikey"), body: String(init.body) });
    if (url.endsWith("rate_limit_hit")) return new Response("true", { status: 200 });
    return new Response(status === 200 ? '"ok"' : '{"message":"JWT expired"}', { status });
  }) as unknown as typeof fetch;
  const db = new PostgrestDb("http://pg.test/", SERVICE_KEY, fetchFn);
  let res = await approveHandler(deps(db))(post({ userCode: CODE }, AUTH));
  assert.equal(res.status, 200);
  assert.equal(seen[0].auth, "Bearer " + SERVICE_KEY); // IP limit: service_role
  assert.equal(seen[1].url, "http://pg.test/rest/v1/rpc/pc_login_approve");
  assert.equal(seen[1].auth, "Bearer " + JWT);
  assert.notEqual(seen[1].auth, "Bearer " + SERVICE_KEY);
  assert.equal(seen[1].apikey, SERVICE_KEY);
  assert.ok(!seen[1].body.includes(CODE));
  for (const st of [401, 403]) {
    status = st;
    res = await unlinkHandler(deps(db))(post({ pcId: PC }, AUTH));
    assert.equal(res.status, 401);
    assert.equal(await res.text(), '{"error":"auth.required"}');
  }
  status = 500;
  res = await unlinkHandler(deps(db))(post({ pcId: PC }, AUTH));
  assert.equal(res.status, 500);
});

test("default logger: console.* gets only fn/status/ms for all three", async () => {
  const cases: [string, H, unknown, FakeDb, number][] = [
    ["pc-login-describe", describeHandler, { userCode: "abcd-2345" },
      describeDb({ status: "ok", display_name: "Gaming PC", requested_at: "2026-11-23T10:00:00Z" }), 200],
    ["pc-login-approve", approveHandler, { userCode: "abcd-2345" }, new FakeDb({ pc_login_approve: () => "ok" }), 200],
    ["pc-unlink", unlinkHandler, { pcId: PC }, new FakeDb({ pc_release: () => "pc.not_found" }), 404],
  ];
  for (const [name, h, body, db, st] of cases) {
    const req = post(body, AUTH);
    const { result: res, out } = await captureConsole(() => h(deps(db, testEnv(), { log: undefined, secLog: undefined }))(req));
    assert.equal(res.status, st);
    assertDefaultLog(out, name, st, [CODE, "abcd", JWT, PC, USER_A, USER_B, "Gaming PC"]);
  }
});
