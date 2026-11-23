// Per-IP limits of Edge endpoints via public.rate_limit_hit (service_role).
// Key = b64u(HMAC(CODE_HASH_PEPPER, "nexus-ip-v1|" + ip)); the IP itself is
// never stored, logged or sent to the database.

import { b64uEncode } from "./entitlement.ts";
import { hmac } from "./codes.ts";
import type { Db } from "./db.ts";
import { HttpError } from "./http.ts";

export interface Bucket {
  name: string;
  limit: number;
  windowSec: number;
}

export const BUCKETS = {
  ipStart: { name: "edge_ip_start", limit: 20, windowSec: 600 },
  startGlobal: { name: "edge_start_global", limit: 2000, windowSec: 600 },
  ipPoll: { name: "edge_ip_poll", limit: 300, windowSec: 600 },
  ipToken: { name: "edge_ip_token", limit: 60, windowSec: 3600 },
  // Shared by pc-login-describe and pc-login-approve (E3-7b).
  ipApprove: { name: "edge_ip_approve", limit: 30, windowSec: 600 },
  ipUnlink: { name: "edge_ip_unlink", limit: 30, windowSec: 600 },
} as const satisfies Record<string, Bucket>;

// Client IP as set by the Supabase Edge platform: first entry of
// X-Forwarded-For (see docs/BACKEND.md; to be confirmed by security-auditor).
// Missing header -> one shared bucket "unknown".
export function clientIp(req: Request): string {
  const xff = req.headers.get("x-forwarded-for");
  const first = xff?.split(",")[0]?.trim();
  return first ? first : "unknown";
}

export async function ipKey(pepper: CryptoKey, ip: string): Promise<string> {
  return b64uEncode(await hmac(pepper, "nexus-ip-v1|" + ip));
}

export async function hit(db: Db, b: Bucket, key: string): Promise<void> {
  const ok = await db.rpc("rate_limit_hit", {
    p_bucket: b.name, p_key: key, p_limit: b.limit, p_window_sec: b.windowSec,
  });
  if (ok === false) throw new HttpError(429, "rate.limited", b.windowSec);
  if (ok !== true) throw new Error("bad rate_limit_hit result");
}
