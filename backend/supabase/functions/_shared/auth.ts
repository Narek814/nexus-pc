// User endpoints (pc-login-describe, pc-login-approve, pc-unlink): the
// Authorization: Bearer JWT is only checked for shape here and passed to
// PostgREST as is, so auth.uid() works in SQL. Edge never decodes the JWT to
// make decisions; PostgREST verifies it (rejection -> auth.required).

import { HttpError } from "./http.ts";

const BEARER_RE = /^Bearer ([A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)$/;
export const MAX_JWT_LEN = 4096;

export function bearerJwt(req: Request): string {
  const h = req.headers.get("authorization");
  const m = h !== null && h.length <= MAX_JWT_LEN + 7 ? BEARER_RE.exec(h) : null;
  if (!m) throw new HttpError(401, "auth.required");
  return m[1];
}
