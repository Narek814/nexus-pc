// POST /functions/v1/pc-login-start (docs/API.md «pc-login-start»).
// Signer: stored pc_keys key if present (rebind), else spki (first binding).
// The signature is verified before pc_login_start; SQL gets that signer.

import { bytea, fromBytea, ready, serveOnDeno, tsArg, type Deps } from "../_shared/db.ts";
import { badRequest, endpoint, jsonResponse, readJsonObject, sqlStatus } from "../_shared/http.ts";
import { b64uDecodeStrict, importP256Spki, pcIdFromSpki, signedFields, startString, verifyP1363 } from "../_shared/pop.ts";
import { hmac, newDeviceCode, newUserCode } from "../_shared/codes.ts";
import { BUCKETS, clientIp, hit, ipKey } from "../_shared/ratelimit.ts";

const NAME_BAD_RE = /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u;

export function nameOk(name: string): boolean {
  const n = [...name].length;
  return n >= 1 && n <= 64 && name.isWellFormed() && !NAME_BAD_RE.test(name);
}

export function createHandler(deps: Deps): (req: Request) => Promise<Response> {
  return endpoint("pc-login-start", async (req) => {
    const { db, cfg } = await ready(deps);
    const b = await readJsonObject(req, {
      pcId: "string", spki: "string", name: "string", nonce: "string", ts: "int", sig: "string",
    });
    const pcId = b.pcId as string, spkiB64u = b.spki as string, name = b.name as string;
    const nonceB64u = b.nonce as string, ts = b.ts as number;
    const { nonce, sig } = signedFields(pcId, nonceB64u, ts, b.sig as string);
    const spki = b64uDecodeStrict(spkiB64u);
    if (!spki || !(await importP256Spki(spki)) || !nameOk(name)) throw badRequest();
    // E4-2: pcId must be derived from spki; same answer as a bad signature, no DB call.
    if ((await pcIdFromSpki(spki)) !== pcId) return sqlStatus("pc.key_required", ["pc.key_required"]);

    // Per-IP limit before any signature work; the global start fuse only
    // after a valid signature, so unsigned floods cannot exhaust it (E7-2).
    await hit(db, BUCKETS.ipStart, await ipKey(cfg.codePepper, clientIp(req)));

    const stored = fromBytea(await db.rpc("pc_key_get", { p_pc_id: pcId }));
    const signer = stored ?? spki;
    if (!(await verifyP1363(signer, sig, await startString(pcId, spkiB64u, name, nonceB64u, ts)))) {
      return sqlStatus("pc.key_required", ["pc.key_required"]);
    }
    await hit(db, BUCKETS.startGlobal, "all");

    const deviceCode = newDeviceCode();
    const userCode = newUserCode();
    const status = await db.rpc("pc_login_start", {
      p_pc_id: pcId,
      p_spki: bytea(spki),
      p_name: name,
      p_device_code_hash: bytea(await hmac(cfg.codePepper, deviceCode)),
      p_user_code_hash: bytea(await hmac(cfg.codePepper, userCode)),
      p_signer_spki: bytea(signer),
      p_nonce: bytea(nonce),
      p_ts: tsArg(ts),
    });
    if (status === "ok") return jsonResponse(200, { deviceCode, userCode, expiresIn: 300, interval: 5 });
    return sqlStatus(status, ["pc.key_required", "request.stale", "nonce.replay", "rate.limited"],
      { "rate.limited": 600 });
  }, deps.log);
}

serveOnDeno(createHandler);
