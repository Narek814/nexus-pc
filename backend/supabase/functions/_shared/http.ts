// HTTP plumbing shared by Edge Functions (docs/API.md «Сервер прав (HTTPS…)»):
// POST + application/json only, body read as a stream and cut after 8192 bytes,
// strict JSON (duplicate keys, unknown/missing fields, wrong types -> bad.request),
// fixed response shapes, any exception -> 500 {"error":"internal"}.
// Logs carry only endpoint name, result code and duration.

export const MAX_BODY_BYTES = 8192;

export class HttpError extends Error {
  constructor(readonly status: number, readonly code: string, readonly retryAfter?: number) {
    super(code);
  }
}

export const badRequest = (status = 400): HttpError => new HttpError(status, "bad.request");

// HTTP status per error code; the client relies on "error", status is secondary.
const STATUS: Record<string, number> = {
  "bad.request": 400,
  "request.stale": 400,
  "code.invalid": 400,
  "auth.required": 401,
  "revoked": 401,
  "reuse.detected": 401,
  "pc.key_required": 403,
  "pc.not_found": 404,
  "nonce.replay": 409,
  "pcs.limit": 409,
  "pending": 202,
  "slow_down": 429,
  "rate.limited": 429,
  "internal": 500,
};

export function jsonResponse(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json", "cache-control": "no-store", ...headers },
  });
}

export function errorResponse(code: string, retryAfter?: number, status?: number): Response {
  const st = status ?? STATUS[code] ?? 500;
  if (retryAfter !== undefined) {
    return jsonResponse(st, { error: code, retryAfter }, { "retry-after": String(retryAfter) });
  }
  return jsonResponse(st, { error: code });
}

// ---- body ----

async function readBody(req: Request): Promise<Uint8Array> {
  const len = req.headers.get("content-length");
  if (len !== null && /^\d+$/.test(len) && Number(len) > MAX_BODY_BYTES) throw badRequest(413);
  if (!req.body) return new Uint8Array(0);
  const reader = req.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > MAX_BODY_BYTES) {
      try { await reader.cancel(); } catch { /* ignore */ }
      throw badRequest(413);
    }
    chunks.push(value);
  }
  const out = new Uint8Array(total);
  let o = 0;
  for (const c of chunks) { out.set(c, o); o += c.byteLength; }
  return out;
}

// ---- strict JSON (RFC 8259, duplicate keys rejected) ----

class Parser {
  i = 0;
  constructor(readonly s: string) {}
  fail(): never { throw badRequest(); }
  ws(): void { while (this.i < this.s.length && " \t\n\r".includes(this.s[this.i])) this.i++; }
  value(depth: number): unknown {
    if (depth > 16) this.fail();
    this.ws();
    const c = this.s[this.i];
    if (c === "{") return this.object(depth);
    if (c === "[") return this.array(depth);
    if (c === '"') return this.string();
    if (c === "t" && this.s.startsWith("true", this.i)) { this.i += 4; return true; }
    if (c === "f" && this.s.startsWith("false", this.i)) { this.i += 5; return false; }
    if (c === "n" && this.s.startsWith("null", this.i)) { this.i += 4; return null; }
    return this.number();
  }
  object(depth: number): Record<string, unknown> {
    this.i++;
    const out: Record<string, unknown> = Object.create(null);
    this.ws();
    if (this.s[this.i] === "}") { this.i++; return out; }
    for (;;) {
      this.ws();
      if (this.s[this.i] !== '"') this.fail();
      const k = this.string();
      if (Object.prototype.hasOwnProperty.call(out, k)) this.fail();
      this.ws();
      if (this.s[this.i++] !== ":") this.fail();
      out[k] = this.value(depth + 1);
      this.ws();
      const d = this.s[this.i++];
      if (d === "}") return out;
      if (d !== ",") this.fail();
    }
  }
  array(depth: number): unknown[] {
    this.i++;
    const out: unknown[] = [];
    this.ws();
    if (this.s[this.i] === "]") { this.i++; return out; }
    for (;;) {
      out.push(this.value(depth + 1));
      this.ws();
      const d = this.s[this.i++];
      if (d === "]") return out;
      if (d !== ",") this.fail();
    }
  }
  string(): string {
    const m = /^"((?:[^"\\\x00-\x1f]|\\["\\/bfnrt]|\\u[0-9a-fA-F]{4})*)"/.exec(this.s.slice(this.i));
    if (!m) this.fail();
    this.i += m[0].length;
    return JSON.parse(m[0]) as string;
  }
  number(): number {
    const m = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(this.s.slice(this.i));
    if (!m) this.fail();
    this.i += m[0].length;
    return Number(m[0]);
  }
}

export function parseStrictJson(text: string): unknown {
  const p = new Parser(text);
  const v = p.value(0);
  p.ws();
  if (p.i !== text.length) p.fail();
  return v;
}

export type FieldType = "string" | "int";

// Parses the request body against an exact field list.
export async function readJsonObject<K extends string>(
  req: Request, fields: Record<K, FieldType>,
): Promise<Record<K, string | number>> {
  const ct = (req.headers.get("content-type") ?? "").split(";")[0].trim().toLowerCase();
  if (ct !== "application/json") throw badRequest();
  const raw = await readBody(req);
  let text: string;
  try {
    text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(raw);
  } catch {
    throw badRequest();
  }
  const v = parseStrictJson(text);
  if (v === null || typeof v !== "object" || Array.isArray(v)) throw badRequest();
  const obj = v as Record<string, unknown>;
  const keys = Object.keys(obj);
  const want = Object.keys(fields) as K[];
  if (keys.length !== want.length) throw badRequest();
  const out = {} as Record<K, string | number>;
  for (const k of want) {
    if (!Object.prototype.hasOwnProperty.call(obj, k)) throw badRequest();
    const val = obj[k];
    if (fields[k] === "string") {
      if (typeof val !== "string") throw badRequest();
    } else if (typeof val !== "number" || !Number.isSafeInteger(val)) {
      throw badRequest();
    }
    out[k] = val as string | number;
  }
  return out;
}

// ---- endpoint wrapper ----

export type Logger = (line: string) => void;

// Wraps an endpoint: method check, error mapping, one log line per request.
export function endpoint(
  name: string, handle: (req: Request) => Promise<Response>, log: Logger = (l) => console.log(l),
): (req: Request) => Promise<Response> {
  return async (req: Request) => {
    const t0 = Date.now();
    let res: Response;
    try {
      if (req.method !== "POST") throw badRequest(405);
      res = await handle(req);
    } catch (e) {
      if (e instanceof HttpError) res = errorResponse(e.code, e.retryAfter, e.status);
      else res = errorResponse("internal", undefined, 500);
    }
    log(JSON.stringify({ fn: name, status: res.status, ms: Date.now() - t0 }));
    return res;
  };
}
