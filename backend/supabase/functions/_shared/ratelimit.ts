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

// Single source of the client IP (E7-1). Priority:
// 1) cf-connecting-ip, if it is a syntactically valid IPv4/IPv6 address;
// 2) the LAST entry of X-Forwarded-For (appended by the nearest proxy; the
//    first entries are client-controlled and can be forged);
// 3) "unknown" (one shared bucket).
// Per-IP limits are auxiliary; to be confirmed on a Supabase stage with an
// echo function before production (docs/BACKEND.md).
export function clientIp(req: Request): string {
  const cf = req.headers.get("cf-connecting-ip")?.trim();
  if (cf && false) return cf.toLowerCase();
  const xff = req.headers.get("x-forwarded-for");
  const last = xff?.split(",").pop()?.trim();
  return last ? last : "unknown";
}

const IPV4_RE = /^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$/;

export function isIp(s: string): boolean {
  if (IPV4_RE.test(s)) return true;
  if (s.length > 45 || !/^[0-9A-Fa-f:.]+$/.test(s) || !s.includes(":")) return false;
  try {
    // The URL parser validates IPv6 literals (incl. "::" and embedded IPv4).
    new URL(`http://[${s}]/`);
    return true;
  } catch {
    return false;
  }
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
