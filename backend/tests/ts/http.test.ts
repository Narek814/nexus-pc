// node --test: _shared/http.ts (method, content type, body size, strict JSON, errors, logs).
import { test } from "node:test";
import assert from "node:assert/strict";
import { endpoint, errorResponse, HttpError, MAX_BODY_BYTES, parseStrictJson, readJsonObject }
  from "../../supabase/functions/_shared/http.ts";

const FIELDS = { a: "string", n: "int" } as const;

function ep(lines: string[]) {
  return endpoint("test-fn", async (req) => {
    const b = await readJsonObject(req, FIELDS);
    if (b.a === "boom") throw new Error("secret detail Authorization: Bearer xyz");
    return new Response(JSON.stringify(b), { status: 200 });
  }, (l) => lines.push(l));
}

function req(body: string | ReadableStream<Uint8Array>, init: RequestInit = {}): Request {
  return new Request("http://edge.test/fn", {
    method: "POST", headers: { "content-type": "application/json" }, body,
    // @ts-ignore Node needs duplex for stream bodies
    duplex: "half", ...init,
  });
}

async function call(r: Request) {
  const lines: string[] = [];
  const res = await ep(lines)(r);
  return { res, body: await res.json(), lines };
}

test("GET -> 405 bad.request", async () => {
  const { res, body } = await call(new Request("http://edge.test/fn", { method: "GET" }));
  assert.equal(res.status, 405);
  assert.deepEqual(body, { error: "bad.request" });
});

test("wrong content type -> 400", async () => {
  const { res } = await call(req('{"a":"x","n":1}', { headers: { "content-type": "text/plain" } }));
  assert.equal(res.status, 400);
  const ok = await call(req('{"a":"x","n":1}', { headers: { "content-type": "application/json; charset=utf-8" } }));
  assert.equal(ok.res.status, 200);
});

test("body 8192 ok, 8193 -> 413 (content-length and streamed)", async () => {
  const mk = (n: number) => { const pad = n - '{"a":"","n":1}'.length; return `{"a":"${"x".repeat(pad)}","n":1}`; };
  assert.equal(mk(8192).length, MAX_BODY_BYTES);
  assert.equal((await call(req(mk(8192)))).res.status, 200);
  const big = await call(req(mk(8193)));
  assert.equal(big.res.status, 413);
  assert.deepEqual(big.body, { error: "bad.request" });
  // Streamed without Content-Length: cut after 8192 bytes even if the stream is endless.
  let pulled = 0;
  const endless = new ReadableStream<Uint8Array>({
    pull(c) { pulled += 1024; c.enqueue(new Uint8Array(1024).fill(0x20)); },
  });
  const s = await call(req(endless));
  assert.equal(s.res.status, 413);
  assert.ok(pulled <= 16 * 1024, "stream read stops near the limit");
});

test("strict JSON: duplicates, extra/missing fields, types, non-object", async () => {
  for (const b of [
    '{"a":"x","a":"y","n":1}', '{"a":"x","n":1,"z":0}', '{"a":"x"}', '{"a":1,"n":1}', '{"a":"x","n":"1"}',
    '{"a":"x","n":1.5}', '{"a":"x","n":1e400}', '[]', '"x"', 'null', '', '{"a":"x","n":1}x', '{"a":"x",}',
    "{'a':'x','n':1}", '{"a":"x","n":01}', '{"a":"\u0001","n":1}', '{"__proto__":"x","n":1}',
  ]) {
    const { res, body } = await call(req(b));
    assert.equal(res.status, 400, b);
    assert.deepEqual(body, { error: "bad.request" });
  }
  assert.throws(() => parseStrictJson('{"k":{"x":1,"x":2}}'));
  assert.equal(JSON.stringify(parseStrictJson(' {"k":[1,true,null,"\\u00e9"]} ')), '{"k":[1,true,null,"é"]}');
});

test("invalid UTF-8 -> 400", async () => {
  const bytes = new Uint8Array([...new TextEncoder().encode('{"a":"'), 0xff, ...new TextEncoder().encode('","n":1}')]);
  const r = new Request("http://edge.test/fn", { method: "POST", headers: { "content-type": "application/json" }, body: bytes });
  assert.equal((await call(r)).res.status, 400);
});

test("exception -> 500 internal without text; log has only fn, status, ms", async () => {
  const { res, body, lines } = await call(req('{"a":"boom","n":7}'));
  assert.equal(res.status, 500);
  assert.equal(JSON.stringify(body), '{"error":"internal"}');
  assert.equal(lines.length, 1);
  const l = JSON.parse(lines[0]);
  assert.deepEqual(Object.keys(l).sort(), ["fn", "ms", "status"]);
  assert.equal(l.fn, "test-fn");
  assert.ok(!lines[0].includes("boom") && !lines[0].includes("Bearer"));
});

test("error with retryAfter sets Retry-After", async () => {
  const r = errorResponse("rate.limited", 600);
  assert.equal(r.status, 429);
  assert.equal(r.headers.get("retry-after"), "600");
  assert.deepEqual(await r.json(), { error: "rate.limited", retryAfter: 600 });
  const e = endpoint("x", async () => { throw new HttpError(429, "slow_down", 5); }, () => {});
  const res = await e(req("{}"));
  assert.deepEqual(await res.json(), { error: "slow_down", retryAfter: 5 });
  assert.equal(res.headers.get("retry-after"), "5");
});
