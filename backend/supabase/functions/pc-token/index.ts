// POST /functions/v1/pc-token (docs/API.md «pc-token»): refresh rotation and
// a new entitlement. The signature is verified with the pc_keys key before
// SQL; new refresh = derive(old), so a retry after a lost response gets the
// same new token. Entitlement is issued by the user_id from pc_rotate_refresh.

import { bytea, firstRow, fromBytea, issueEntitlement, ready, serveOnDeno, tsArg, type Deps } from "../_shared/db.ts";
import { badRequest, endpoint, errorResponse, jsonResponse, readJsonObject, sqlStatus } from "../_shared/http.ts";
import { b64uDecodeStrict, refreshString, signedFields, verifyP1363 } from "../_shared/pop.ts";
import { deriveRefresh, hmac, REFRESH_BYTES } from "../_shared/codes.ts";
import { BUCKETS, clientIp, hit, ipKey } from "../_shared/ratelimit.ts";

export function createHandler(deps: Deps): (req: Request) => Promise<Response> {
  const secLog = deps.secLog ?? ((l: string) => console.warn(l));
  return endpoint("pc-token", async (req) => {
    const { db, cfg, now } = await ready(deps);
    const b = await readJsonObject(req, {
      pcId: "string", refreshToken: "string", nonce: "string", ts: "int", sig: "string",
    });
    const pcId = b.pcId as string, old = b.refreshToken as string;
    const nonceB64u = b.nonce as string, ts = b.ts as number;
    const { nonce, sig } = signedFields(pcId, nonceB64u, ts, b.sig as string);
    if (b64uDecodeStrict(old)?.length !== REFRESH_BYTES) throw badRequest();

    await hit(db, BUCKETS.ipToken, await ipKey(cfg.codePepper, clientIp(req)));

    const key = fromBytea(await db.rpc("pc_key_get", { p_pc_id: pcId }));
    if (!key || !(await verifyP1363(key, sig, await refreshString(pcId, old, nonceB64u, ts)))) {
      return errorResponse("revoked");
    }

    const next = await deriveRefresh(cfg.refreshDerive, old);
    const r = firstRow(await db.rpc("pc_rotate_refresh", {
      p_old_hash: bytea(await hmac(cfg.refreshPepper, old)),
      p_new_hash: bytea(await hmac(cfg.refreshPepper, next)),
      p_pc_id: pcId,
      p_signer_spki: bytea(key),
      p_nonce: bytea(nonce),
      p_ts: tsArg(ts),
    }));
    const status = r?.status;
    if (status === "ok") {
      if (typeof r!.user_id !== "string") throw new Error("bad rotate row");
      const ent = await issueEntitlement(db, cfg, r!.user_id, pcId, now);
      if (!ent.ok) return errorResponse("revoked");
      return jsonResponse(200, { refreshToken: next, entitlement: ent.entitlement, entitlementState: ent.entitlementState });
    }
    if (status === "reuse.detected") {
      secLog(JSON.stringify({ security: "pc.refresh_reuse", pcId }));
    }
    return sqlStatus(status, ["revoked", "reuse.detected", "rate.limited", "request.stale", "nonce.replay"],
      { "rate.limited": 3600 });
  }, deps.log);
}

serveOnDeno(createHandler);
