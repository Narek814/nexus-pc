// POST /functions/v1/delete-account (E3-8): the signed-in user deletes the
// account. Body: {} (strict JSON). Answers 200 {} | 401 auth.required |
// 401 reauth.required | 429 rate.limited | 400 bad.request | 500 internal.
// Order: config -> method -> JWT shape -> body -> GoTrue GET /auth/v1/user
// (GoTrue verifies the JWT signature and expiry and returns the user id) ->
// freshness (iat of the verified JWT and last_sign_in_at, both <= 5 min) ->
// per-user limit 3/h -> per-IP limit -> account_purge (service_role) ->
// GoTrue admin DELETE /auth/v1/admin/users/{id}.
// The user id is never taken from the request body. A failed admin delete
// after purge answers 500; a retry is safe (account_purge is idempotent, a
// 404 from the admin delete counts as already deleted).
// Logs: only fn/status/ms (no JWT, email, user id).

import { ready, type Deps, type EnvGet, dbFromEnv, configOnce } from "../_shared/db.ts";
import { b64uEncode } from "../_shared/entitlement.ts";
import { hmac } from "../_shared/codes.ts";
import { endpoint, HttpError, jsonResponse, readJsonObject } from "../_shared/http.ts";
import { bearerJwt } from "../_shared/auth.ts";
import { clientIp, hit, ipKey, type Bucket } from "../_shared/ratelimit.ts";

export const REAUTH_MAX_AGE_SEC = 300;
// Allowed clock skew for timestamps in the future.
export const CLOCK_SKEW_SEC = 60;

export const DELETE_BUCKETS = {
  user: { name: "edge_user_delete", limit: 3, windowSec: 3600 },
  ip: { name: "edge_ip_delete", limit: 10, windowSec: 3600 },
} as const satisfies Record<string, Bucket>;

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export interface AuthUser {
  id: string;
  lastSignInAt: number | null; // Unix seconds
}

export interface AuthApi {
  // Verified user of the JWT, or null if GoTrue rejects it.
  getUser(jwt: string): Promise<AuthUser | null>;
  // Deletes the auth user; resolves if deleted or already absent.
  deleteUser(id: string): Promise<void>;
}

// GoTrue over fetch (no packages).
export class GoTrueApi implements AuthApi {
  private readonly base: string;
  private readonly serviceKey: string;
  private readonly anonKey: string;
  private readonly fetchFn: typeof fetch;
  constructor(url: string, serviceKey: string, anonKey: string, fetchFn: typeof fetch = fetch) {
    this.base = url.replace(/\/+$/, "") + "/auth/v1";
    this.serviceKey = serviceKey;
    this.anonKey = anonKey;
    this.fetchFn = fetchFn;
  }

  async getUser(jwt: string): Promise<AuthUser | null> {
    const res = await this.fetchFn(`${this.base}/user`, {
      method: "GET",
      headers: { "accept": "application/json", "apikey": this.anonKey, "authorization": `Bearer ${jwt}` },
    });
    if (res.status === 401 || res.status === 403) {
      await res.body?.cancel();
      return null;
    }
    if (!res.ok) {
      await res.body?.cancel();
      throw new Error(`auth user failed: ${res.status}`);
    }
    const u = (await res.json()) as Record<string, unknown> | null;
    if (!u || typeof u !== "object" || typeof u.id !== "string" || !UUID_RE.test(u.id)) {
      throw new Error("bad auth user");
    }
    let last: number | null = null;
    if (typeof u.last_sign_in_at === "string") {
      const t = Date.parse(u.last_sign_in_at);
      if (Number.isFinite(t)) last = Math.floor(t / 1000);
    }
    return { id: u.id, lastSignInAt: last };
  }

  async deleteUser(id: string): Promise<void> {
    if (!UUID_RE.test(id)) throw new Error("bad user id");
    const res = await this.fetchFn(`${this.base}/admin/users/${id}`, {
      method: "DELETE",
      headers: { "accept": "application/json", "apikey": this.serviceKey, "authorization": `Bearer ${this.serviceKey}` },
    });
    await res.body?.cancel();
    if (res.ok || res.status === 404) return;
    throw new Error(`admin delete failed: ${res.status}`); // no body, no id
  }
}

export function authFromEnv(env: EnvGet, fetchFn: typeof fetch = fetch): GoTrueApi | null {
  const url = env("SUPABASE_URL");
  const key = env("SUPABASE_SERVICE_ROLE_KEY");
  const anon = env("SUPABASE_ANON_KEY");
  return url && key && anon ? new GoTrueApi(url, key, anon, fetchFn) : null;
}

// Claims of a JWT that GoTrue has already accepted (signature checked there).
// Only sub and iat are read; anything malformed -> reauth.required.
export function jwtClaims(jwt: string): { sub: unknown; iat: unknown } | null {
  try {
    const p = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(b64uDecode(jwt.split(".")[1])));
    return p && typeof p === "object" && !Array.isArray(p) ? { sub: p.sub, iat: p.iat } : null;
  } catch {
    return null;
  }
}

function b64uDecode(s: string): Uint8Array {
  const b = atob(s.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((s.length + 3) % 4));
  return Uint8Array.from(b, (c) => c.charCodeAt(0));
}

function fresh(ts: unknown, now: number): boolean {
  return typeof ts === "number" && Number.isSafeInteger(ts)
    && ts >= now - REAUTH_MAX_AGE_SEC && ts <= now + CLOCK_SKEW_SEC;
}

export async function userKey(pepper: CryptoKey, userId: string): Promise<string> {
  return b64uEncode(await hmac(pepper, "nexus-user-v1|" + userId));
}

export interface DeleteDeps extends Deps {
  auth: AuthApi | null; // null: env missing -> 500
}

export function createHandler(deps: DeleteDeps): (req: Request) => Promise<Response> {
  return endpoint("delete-account", async (req) => {
    const { db, cfg, now } = await ready(deps);
    if (!deps.auth) throw new Error("not configured");
    const jwt = bearerJwt(req);
    await readJsonObject(req, {});
    const user = await deps.auth.getUser(jwt);
    if (!user) throw new HttpError(401, "auth.required");
    const c = jwtClaims(jwt);
    if (!c || c.sub !== user.id || !fresh(c.iat, now) || !fresh(user.lastSignInAt, now)) {
      throw new HttpError(401, "reauth.required");
    }
    await hit(db, DELETE_BUCKETS.user, await userKey(cfg.codePepper, user.id));
    await hit(db, DELETE_BUCKETS.ip, await ipKey(cfg.codePepper, clientIp(req)));
    const st = await db.rpc("account_purge", { p_user_id: user.id });
    if (st !== "ok") throw new Error("unexpected purge status");
    await deps.auth.deleteUser(user.id);
    return jsonResponse(200, {});
  }, deps.log);
}

interface DenoLike {
  env: { get(n: string): string | undefined };
  serve(h: (req: Request) => Promise<Response>): unknown;
}

const D = (globalThis as { Deno?: DenoLike }).Deno;
if (D) {
  const env = (n: string) => D.env.get(n);
  D.serve(createHandler({ db: dbFromEnv(env), config: configOnce(env), auth: authFromEnv(env) }));
}
