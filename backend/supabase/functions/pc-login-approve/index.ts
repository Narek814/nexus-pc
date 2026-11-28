// POST /functions/v1/pc-login-approve (docs/API.md «pc-login-approve»).
// Signed-in user, step 2: approve a code described by the same user.
// Same order, IP bucket and code handling as pc-login-describe.

import { bytea, ready, serveOnDeno, type Deps } from "../_shared/db.ts";
import { endpoint, errorResponse, jsonResponse, readJsonObject, sqlStatus } from "../_shared/http.ts";
import { bearerJwt } from "../_shared/auth.ts";
import { hmac, normalizeUserCode } from "../_shared/codes.ts";
import { BUCKETS, clientIp, hit, ipKey } from "../_shared/ratelimit.ts";

export function createHandler(deps: Deps): (req: Request) => Promise<Response> {
  return endpoint("pc-login-approve", async (req) => {
    const { db, cfg } = await ready(deps);
    const jwt = bearerJwt(req);
    const b = await readJsonObject(req, { userCode: "string" });
    await hit(db, BUCKETS.ipApprove, await ipKey(cfg.codePepper, clientIp(req)));
    const code = normalizeUserCode(b.userCode as string);
    if (code === null) return errorResponse("code.invalid");
    const status = await db.rpc("pc_login_approve",
      { p_user_code_hash: bytea(await hmac(cfg.codePepper, code)) }, { userJwt: jwt });
    if (status === "ok") return jsonResponse(200, {});
    return sqlStatus(status, ["code.invalid", "rate.limited", "pcs.limit"], { "rate.limited": 600 });
  }, deps.log);
}

serveOnDeno(createHandler);
