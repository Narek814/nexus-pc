// POST /functions/v1/pc-login-poll (docs/API.md «pc-login-poll»).
// The consume signature is verified only with the new key of the code.
// A fresh refresh token is generated per poll; it and the entitlement are
// returned only when pc_login_consume says ok.

import { bytea, firstRow, fromBytea, issueEntitlement, ready, serveOnDeno, tsArg, type Deps } from "../_shared/db.ts";
import { badRequest, endpoint, errorResponse, jsonResponse, readJsonObject, sqlStatus } from "../_shared/http.ts";
import { b64uDecodeStrict, consumeString, signedFields, verifyP1363 } from "../_shared/pop.ts";
import { DEVICE_CODE_BYTES, hmac, newRefreshToken } from "../_shared/codes.ts";
import { BUCKETS, clientIp, hit, ipKey } from "../_shared/ratelimit.ts";

export function createHandler(deps: Deps): (req: Request) => Promise<Response> {
  return endpoint("pc-login-poll", async (req) => {
    const { db, cfg, now } = await ready(deps);
    const b = await readJsonObject(req, {
      pcId: "string", deviceCode: "string", nonce: "string", ts: "int", sig: "string",
    });
    const pcId = b.pcId as string, deviceCode = b.deviceCode as string;
    const nonceB64u = b.nonce as string, ts = b.ts as number;
    const { nonce, sig } = signedFields(pcId, nonceB64u, ts, b.sig as string);
    if (b64uDecodeStrict(deviceCode)?.length !== DEVICE_CODE_BYTES) throw badRequest();

    await hit(db, BUCKETS.ipPoll, await ipKey(cfg.codePepper, clientIp(req)));

    const dcHash = bytea(await hmac(cfg.codePepper, deviceCode));
    const code = firstRow(await db.rpc("pc_login_code_key", { p_device_code_hash: dcHash }));
    const key = code ? fromBytea(code.public_key_spki) : null;
    if (!code || code.pc_id !== pcId || !key) return sqlStatus("code.invalid", ["code.invalid"]);
    if (!(await verifyP1363(key, sig, await consumeString(pcId, deviceCode, nonceB64u, ts)))) {
      return sqlStatus("pc.key_required", ["pc.key_required"]);
    }

    const refreshToken = newRefreshToken();
    const r = firstRow(await db.rpc("pc_login_consume", {
      p_device_code_hash: dcHash,
      p_refresh_hash: bytea(await hmac(cfg.refreshPepper, refreshToken)),
      p_signer_spki: bytea(key),
      p_nonce: bytea(nonce),
      p_ts: tsArg(ts),
    }));
    const status = r?.status;
    if (status === "ok") {
      if (r!.pc_id !== pcId || typeof r!.user_id !== "string") throw new Error("bad consume row");
      const ent = await issueEntitlement(db, cfg, r!.user_id, pcId, now);
      if (!ent.ok) return errorResponse("revoked");
      return jsonResponse(200, { refreshToken, entitlement: ent.entitlement, entitlementState: ent.entitlementState });
    }
    return sqlStatus(status, ["pending", "slow_down", "code.invalid", "pcs.limit", "pc.key_required",
      "request.stale", "nonce.replay"], { "slow_down": 5 });
  }, deps.log);
}

serveOnDeno(createHandler);
