// Database access (PostgREST RPC over fetch, no packages), Edge configuration
// (secrets read once) and entitlement issuance by user_id.

import { b64uEncode, signEntitlement } from "./entitlement.ts";
import { hmac, importHmacKey } from "./codes.ts";
import { HttpError } from "./http.ts";

export interface RpcOptions {
  userJwt?: string; // call as the signed-in user instead of service_role
}

export interface Db {
  rpc(name: string, args: Record<string, unknown>, opts?: RpcOptions): Promise<unknown>;
}

// bytea is sent to and returned by PostgREST as "\x" + lower hex.
export function bytea(b: Uint8Array): string {
  let h = "\\x";
  for (const x of b) h += x.toString(16).padStart(2, "0");
  return h;
}

export function fromBytea(v: unknown): Uint8Array | null {
  if (typeof v !== "string" || !/^\\x([0-9a-fA-F]{2})*$/.test(v)) return null;
  const out = new Uint8Array((v.length - 2) / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(v.slice(2 + 2 * i, 4 + 2 * i), 16);
  return out;
}

export function tsArg(ts: number): string {
  return new Date(ts * 1000).toISOString();
}

export class PostgrestDb implements Db {
  private readonly url: string;
  private readonly serviceKey: string;
  private readonly anonKey: string;
  private readonly fetchFn: typeof fetch;
  // apikey: the public anon key for user calls, service_role only for service
  // calls (E7-5); auth.uid() comes from the user JWT in Authorization.
  constructor(url: string, serviceKey: string, anonKey: string, fetchFn: typeof fetch = fetch) {
    this.url = url;
    this.serviceKey = serviceKey;
    this.anonKey = anonKey;
    this.fetchFn = fetchFn;
  }

  async rpc(name: string, args: Record<string, unknown>, opts: RpcOptions = {}): Promise<unknown> {
    if (!/^[a-z_][a-z0-9_]*$/.test(name)) throw new Error("bad rpc name");
    const bearer = opts.userJwt ?? this.serviceKey;
    const res = await this.fetchFn(`${this.url.replace(/\/+$/, "")}/rest/v1/rpc/${name}`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "accept": "application/json",
        "apikey": opts.userJwt !== undefined ? this.anonKey : this.serviceKey,
        "authorization": `Bearer ${bearer}`,
      },
      body: JSON.stringify(args),
    });
    if (!res.ok) {
      await res.body?.cancel();
      // A user JWT rejected by PostgREST (expired, bad signature, anon role
      // without EXECUTE) is the client's problem: 401 auth.required, not 500.
      if (opts.userJwt !== undefined && (res.status === 401 || res.status === 403)) {
        throw new HttpError(401, "auth.required");
      }
      throw new Error(`rpc ${name} failed: ${res.status}`); // no body, no args
    }
    return await res.json();
  }
}

// First row of a set-returning function result.
export function firstRow(v: unknown): Record<string, unknown> | null {
  if (Array.isArray(v)) return (v[0] ?? null) as Record<string, unknown> | null;
  if (v && typeof v === "object") return v as Record<string, unknown>;
  return null;
}

// ---- configuration ----

export const MIN_SECRET_BYTES = 32;
const KID_RE = /^[A-Za-z0-9_-]{1,64}$/;
const PRINTABLE_RE = /^[\x21-\x7e]{1,128}$/;

export interface Config {
  codePepper: CryptoKey;
  refreshPepper: CryptoKey;
  refreshDerive: CryptoKey;
  entKid: string;
  entKeyPkcs8B64: string;
  entIss: string;
  entSubKey: CryptoKey;
}

export type EnvGet = (name: string) => string | undefined;

// Strict standard base64 (RFC 4648 sec. 4, "=" padding, no whitespace,
// canonical trailing bits), as printed by `openssl rand -base64 32`.
const B64_RE = /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/;

export function b64DecodeStrict(v: string): Uint8Array | null {
  if (v.length === 0 || !B64_RE.test(v)) return null;
  let raw: string;
  try {
    raw = atob(v);
  } catch {
    return null;
  }
  // Non-zero padding bits -> not canonical -> rejected.
  return btoa(raw) === v ? Uint8Array.from(raw, (c) => c.charCodeAt(0)) : null;
}

// HMAC secrets: strict base64, at least 32 bytes after decoding; the HMAC key
// is the decoded bytes (E7-4). Returns null (caller answers 500 internal
// without naming the variable) if anything is missing or malformed.
export async function loadConfig(env: EnvGet): Promise<Config | null> {
  const secret = (n: string): Uint8Array | null => {
    const v = env(n);
    if (typeof v !== "string") return null;
    const b = b64DecodeStrict(v);
    return b && b.length >= MIN_SECRET_BYTES ? b : null;
  };
  const cp = secret("CODE_HASH_PEPPER");
  const rp = secret("REFRESH_HASH_PEPPER");
  const rd = secret("REFRESH_DERIVE_KEY");
  const sk = secret("ENT_SUB_KEY");
  const kid = env("ENT_KID");
  const key = env("ENT_SIGNING_KEY_PKCS8_B64");
  const iss = env("ENT_ISS");
  if (!cp || !rp || !rd || !sk || !kid || !KID_RE.test(kid) || !key || !iss || !PRINTABLE_RE.test(iss)) return null;
  try {
    // Checks the signing key once, so no SQL write happens with a broken key.
    const raw = Uint8Array.from(atob(key), (c) => c.charCodeAt(0));
    await crypto.subtle.importKey("pkcs8", raw, { name: "ECDSA", namedCurve: "P-256" }, false, ["sign"]);
    return {
      codePepper: await importHmacKey(cp),
      refreshPepper: await importHmacKey(rp),
      refreshDerive: await importHmacKey(rd),
      entKid: kid, entKeyPkcs8B64: key, entIss: iss,
      entSubKey: await importHmacKey(sk),
    };
  } catch {
    return null;
  }
}

// Reads the configuration once per process; a failure is remembered too.
export function configOnce(env: EnvGet): () => Promise<Config | null> {
  let p: Promise<Config | null> | null = null;
  return () => (p ??= loadConfig(env));
}

// ---- entitlement ----

export const ENT_TTL_SEC = 86400;
export const ENT_GRACE_SEC = 604800;
const PRO_FEATURES = new Set(["profiles", "rgb.sync", "automations", "monitoring.extended", "files.unlimited"]);

// Opaque subject, keyed and per PC (E7-3): not reversible to the user id and
// not linkable between PCs of one user without ENT_SUB_KEY.
export async function entitlementSub(key: CryptoKey, userId: string, pcId: string): Promise<string> {
  return "u_" + b64uEncode(await hmac(key, "nexus-ent-sub-v1|" + userId + "|" + pcId));
}

export type Issued =
  | { ok: true; entitlement: string; entitlementState: "active" | "revoked" }
  | { ok: false }; // unlinked: answer "revoked" without a token

// Entitlement of the user returned by consume/rotate (not of the current my_pcs owner).
export async function issueEntitlement(db: Db, cfg: Config, userId: string, pcId: string,
  now: number): Promise<Issued> {
  const r = firstRow(await db.rpc("entitlement_for_user", { p_user_id: userId }));
  if (!r) throw new Error("no entitlement row");
  const state = r.state;
  if (state === "unlinked") return { ok: false };
  let plan: "free" | "pro";
  let features: string[];
  if (state === "revoked") {
    plan = "free";
    features = [];
  } else if (state === "active" && (r.plan === "pro" || r.plan === "free") && Array.isArray(r.features)) {
    plan = r.plan;
    features = (r.features as unknown[]).map(String);
    if (plan === "free" ? features.length !== 0 : !features.every((f) => PRO_FEATURES.has(f))) {
      throw new Error("unexpected features");
    }
  } else {
    throw new Error("unexpected entitlement row");
  }
  const entitlement = await signEntitlement({
    kid: cfg.entKid, privateKeyPkcs8B64: cfg.entKeyPkcs8B64, iss: cfg.entIss,
    sub: await entitlementSub(cfg.entSubKey, userId, pcId), pcId, plan, features, now,
    ttlSec: ENT_TTL_SEC, graceSec: ENT_GRACE_SEC,
  });
  return { ok: true, entitlement, entitlementState: state === "revoked" ? "revoked" : "active" };
}

// ---- dependencies of an endpoint ----

export interface Deps {
  db: Db | null; // null: SUPABASE_URL / SUPABASE_SERVICE_ROLE_KEY / SUPABASE_ANON_KEY missing -> 500
  config: () => Promise<Config | null>;
  now?: () => number; // Unix seconds
  log?: (line: string) => void; // one line per request
  secLog?: (line: string) => void; // security events
}

interface DenoLike {
  env: { get(n: string): string | undefined };
  serve(h: (req: Request) => Promise<Response>): unknown;
}

// Starts the endpoint when running on Supabase Edge (Deno); no-op in tests.
export function serveOnDeno(create: (deps: Deps) => (req: Request) => Promise<Response>): void {
  const D = (globalThis as { Deno?: DenoLike }).Deno;
  if (!D) return;
  const env = (n: string) => D.env.get(n);
  D.serve(create({ db: dbFromEnv(env), config: configOnce(env) }));
}

// PostgREST client from env; null (-> 500 internal) if any value is missing.
export function dbFromEnv(env: EnvGet, fetchFn: typeof fetch = fetch): PostgrestDb | null {
  const url = env("SUPABASE_URL");
  const key = env("SUPABASE_SERVICE_ROLE_KEY");
  const anon = env("SUPABASE_ANON_KEY");
  return url && key && anon ? new PostgrestDb(url, key, anon, fetchFn) : null;
}

// Resolves db and config or answers 500 internal.
export async function ready(deps: Deps): Promise<{ db: Db; cfg: Config; now: number }> {
  const cfg = await deps.config();
  if (!cfg || !deps.db) throw new Error("not configured");
  return { db: deps.db, cfg, now: deps.now ? deps.now() : Math.floor(Date.now() / 1000) };
}
