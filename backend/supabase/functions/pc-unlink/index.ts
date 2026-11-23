// POST /functions/v1/pc-unlink (docs/API.md «pc-unlink»): the signed-in owner
// releases a PC (pc_release revokes refresh tokens and live codes).
// Order: config -> method -> JWT -> body -> IP limit -> SQL (as the user).
// The per-user limit stays in SQL.

import { ready, serveOnDeno, type Deps } from "../_shared/db.ts";
import { badRequest, endpoint, jsonResponse, readJsonObject, sqlStatus } from "../_shared/http.ts";
import { bearerJwt } from "../_shared/auth.ts";
import { isPcId } from "../_shared/pop.ts";
import { BUCKETS, clientIp, hit, ipKey } from "../_shared/ratelimit.ts";

export function createHandler(deps: Deps): (req: Request) => Promise<Response> {
  return endpoint("pc-unlink", async (req) => {
    const { db, cfg } = await ready(deps);
    const jwt = bearerJwt(req);
    const b = await readJsonObject(req, { pcId: "string" });
    const pcId = b.pcId as string;
    if (!isPcId(pcId)) throw badRequest();
    await hit(db, BUCKETS.ipUnlink, await ipKey(cfg.codePepper, clientIp(req)));
    const status = await db.rpc("pc_release", { p_pc_id: pcId }, { userJwt: jwt });
    if (status === "ok") return jsonResponse(200, {});
    return sqlStatus(status, ["pc.not_found", "rate.limited"], { "rate.limited": 600 });
  }, deps.log);
}

serveOnDeno(createHandler);
