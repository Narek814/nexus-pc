// Database access (PostgREST RPC over fetch, no packages), Edge configuration
// (secrets read once) and entitlement issuance by user_id.

import { b64uEncode, signEntitlement } from "./entitlement.ts";
import { importHmacKey } from "./codes.ts";

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
  constructor(private readonly url: string, private readonly serviceKey: string,
    private readonly fetchFn: typeof fetch = fetch) {}

  async rpc(name: string, args: Record<string, unknown>, opts: RpcOptions = {}): Promise<unknown> {
    if (!/^[a-z_][a-z0-9_]*$/.test(name)) throw new Error("bad rpc name");
    const bearer = opts.userJwt ?? this.serviceKey;
    const res = await this.fetchFn(`${this.url.replace(/\/+$/, "")}/rest/v1/rpc/${name}`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "accept": "application/json",
        "apikey": this.serviceKey,
        "authorization": `Bearer ${bearer}`,
      },
      body: JSON.stringify(args),
    });
    if (!res.ok) {
      await res.body?.cancel();
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
}

export type EnvGet = (name: string) => string | undefined;

// Secrets are UTF-8 strings of at least 32 bytes. Returns null (caller
// answers 500 internal without naming the variable) if anything is missing.
export async function loadConfig(env: EnvGet): Promise<Config | null> {
  const secret = (n: string): Uint8Array | null => {
    const v = env(n);
    if (typeof v !== "string") return null;
    const b = new TextEncoder().encode(v);
    return b.length >= MIN_SECRET_BYTES ? b : null;
  };
  const cp = secret("CODE_HASH_PEPPER");
  const rp = secret("REFRESH_HASH_PEPPER");
  const rd = secret("REFRESH_DERIVE_KEY");
  const kid = env("ENT_KID");
  const key = env("ENT_SIGNING_KEY_PKCS8_B64");
  const iss = env("ENT_ISS");
  if (!cp || !rp || !rd || !kid || !KID_RE.test(kid) || !key || !iss || !PRINTABLE_RE.test(iss)) return null;
  try {
    // Checks the signing key once, so no SQL write happens with a broken key.
    const raw = Uint8Array.from(atob(key), (c) => c.charCodeAt(0));
    await crypto.subtle.importKey("pkcs8", raw, { name: "ECDSA", namedCurve: "P-256" }, false, ["sign"]);
    return {
      codePepper: await importHmacKey(cp),
      refreshPepper: await importHmacKey(rp),
      refreshDerive: await importHmacKey(rd),
      entKid: kid, entKeyPkcs8B64: key, entIss: iss,
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

// Opaque subject: not reversible to the user id or email.
export async function entitlementSub(userId: string): Promise<string> {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode("nexus-ent-sub-v1|" + userId));
  return "u_" + b64uEncode(new Uint8Array(d));
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
    sub: await entitlementSub(userId), pcId, plan, features, now,
    ttlSec: ENT_TTL_SEC, graceSec: ENT_GRACE_SEC,
  });
  return { ok: true, entitlement, entitlementState: state === "revoked" ? "revoked" : "active" };
}

// ---- dependencies of an endpoint ----

export interface Deps {
  db: Db | null; // null: SUPABASE_URL / SUPABASE_SERVICE_ROLE_KEY missing -> 500
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
  const url = env("SUPABASE_URL");
  const key = env("SUPABASE_SERVICE_ROLE_KEY");
  D.serve(create({ db: url && key ? new PostgrestDb(url, key) : null, config: configOnce(env) }));
}

// Resolves db and config or answers 500 internal.
export async function ready(deps: Deps): Promise<{ db: Db; cfg: Config; now: number }> {
  const cfg = await deps.config();
  if (!cfg || !deps.db) throw new Error("not configured");
  return { db: deps.db, cfg, now: deps.now ? deps.now() : Math.floor(Date.now() / 1000) };
}
