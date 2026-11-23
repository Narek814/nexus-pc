// POST /functions/v1/pc-login-describe (docs/API.md «pc-login-describe»).
// Signed-in user, step 1: PC name and request time for a typed userCode.
// Order: config -> method -> JWT -> body -> IP limit -> SQL (as the user).
// A malformed code answers code.invalid (format not revealed) after the IP hit.

import { bytea, firstRow, ready, serveOnDeno, type Deps } from "../_shared/db.ts";
import { endpoint, errorResponse, jsonResponse, readJsonObject, sqlStatus } from "../_shared/http.ts";
import { bearerJwt } from "../_shared/auth.ts";
import { hmac, normalizeUserCode } from "../_shared/codes.ts";
import { BUCKETS, clientIp, hit, ipKey } from "../_shared/ratelimit.ts";

// timestamptz from PostgREST -> RFC 3339 UTC without fractional seconds.
export function rfc3339(v: unknown): string {
  const t = typeof v === "string" ? Date.parse(v) : NaN;
  if (!Number.isFinite(t)) throw new Error("bad timestamp");
  return new Date(t).toISOString().replace(/\.\d{3}Z$/, "Z");
}

export function createHandler(deps: Deps): (req: Request) => Promise<Response> {
  return endpoint("pc-login-describe", async (req) => {
    const { db, cfg } = await ready(deps);
    const jwt = bearerJwt(req);
    const b = await readJsonObject(req, { userCode: "string" });
    await hit(db, BUCKETS.ipApprove, await ipKey(cfg.codePepper, clientIp(req)));
    const code = normalizeUserCode(b.userCode as string);
    if (code === null) return errorResponse("code.invalid");
    const r = firstRow(await db.rpc("pc_login_describe",
      { p_user_code_hash: bytea(await hmac(cfg.codePepper, code)) }, { userJwt: jwt }));
    if (r?.status === "ok") {
      if (typeof r.display_name !== "string") throw new Error("bad describe row");
      return jsonResponse(200, { displayName: r.display_name, requestedAt: rfc3339(r.requested_at) });
    }
    return sqlStatus(r?.status, ["code.invalid", "rate.limited"], { "rate.limited": 600 });
  }, deps.log);
}

serveOnDeno(createHandler);
