// Entitlement token signing (docs/API.md «Токен прав», shared/schemas/entitlement.v1.schema.json).
// Token: kid "." b64u(payload JSON) "." b64u(ES256 signature, IEEE P1363 r||s, 64 bytes)
// over the ASCII bytes of kid "." payload_b64.
// WebCrypto only (crypto.subtle), no package imports: runs on Deno and Node 22.

export const MAX_TTL_SEC = 86400;
export const MAX_GRACE_SEC = 604800;
export const MAX_TOKEN_LENGTH = 4096;

const KID_RE = /^[A-Za-z0-9_-]{1,64}$/;
const PRINTABLE_RE = /^[\x21-\x7e]{1,128}$/;
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const FEATURE_RE = /^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*){0,3}$/;
const UNIX_MIN = 1700000000;
const UNIX_MAX = 4102444800;

export type Plan = "free" | "pro";

export interface SignEntitlementInput {
  kid: string;
  privateKeyPkcs8B64: string; // standard base64 of a PKCS#8 DER P-256 private key
  iss: string;
  sub: string;
  pcId: string;
  plan: Plan;
  features: string[];
  now: number; // Unix seconds (integer)
  ttlSec: number; // 1..86400
  graceSec: number; // 0..604800
}

export interface EntitlementPayload {
  v: 1;
  typ: "nexus-ent+v1";
  iss: string;
  aud: "nexus-agent";
  sub: string;
  pcId: string;
  plan: Plan;
  features: string[];
  iat: number;
  exp: number;
  graceUntil: number;
  jti: string;
}

export function b64uEncode(bytes: Uint8Array): string {
  let bin = "";
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function b64Decode(s: string): Uint8Array {
  const bin = atob(s);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function fail(msg: string): never {
  throw new Error("signEntitlement: " + msg);
}

function isUnix(n: number): boolean {
  return Number.isSafeInteger(n) && n >= UNIX_MIN && n <= UNIX_MAX;
}

export async function signEntitlement(input: SignEntitlementInput): Promise<string> {
  const { kid, iss, sub, pcId, plan, features, now, ttlSec, graceSec } = input;
  if (typeof kid !== "string" || !KID_RE.test(kid)) fail("bad kid");
  if (typeof iss !== "string" || !PRINTABLE_RE.test(iss)) fail("bad iss");
  if (typeof sub !== "string" || !PRINTABLE_RE.test(sub)) fail("bad sub");
  if (typeof pcId !== "string" || !UUID_RE.test(pcId)) fail("bad pcId");
  if (plan !== "free" && plan !== "pro") fail("bad plan");
  if (!Array.isArray(features) || features.length > 64) fail("bad features");
  for (const f of features) {
    if (typeof f !== "string" || f.length > 64 || !FEATURE_RE.test(f)) fail("bad feature");
  }
  if (new Set(features).size !== features.length) fail("duplicate feature");
  if (!Number.isSafeInteger(ttlSec) || ttlSec <= 0 || ttlSec > MAX_TTL_SEC) fail("ttlSec out of range");
  if (!Number.isSafeInteger(graceSec) || graceSec < 0 || graceSec > MAX_GRACE_SEC) {
    fail("graceSec out of range");
  }
  const iat = now;
  const exp = iat + ttlSec;
  const graceUntil = exp + graceSec;
  if (!isUnix(iat) || !isUnix(exp) || !isUnix(graceUntil)) fail("time out of range");

  const payload: EntitlementPayload = {
    v: 1,
    typ: "nexus-ent+v1",
    iss,
    aud: "nexus-agent",
    sub,
    pcId,
    plan,
    features: [...features],
    iat,
    exp,
    graceUntil,
    jti: crypto.randomUUID(),
  };
  const payloadB64 = b64uEncode(new TextEncoder().encode(JSON.stringify(payload)));
  const signingInput = kid + "." + payloadB64;

  let key: CryptoKey;
  try {
    key = await crypto.subtle.importKey(
      "pkcs8",
      b64Decode(input.privateKeyPkcs8B64),
      { name: "ECDSA", namedCurve: "P-256" },
      false,
      ["sign"],
    );
  } catch {
    fail("bad private key"); // never include key material in errors
  }
  const sig = new Uint8Array(
    await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, new TextEncoder().encode(signingInput)),
  );
  if (sig.length !== 64) fail("unexpected signature length");
  const token = signingInput + "." + b64uEncode(sig);
  if (token.length > MAX_TOKEN_LENGTH) fail("token too long");
  return token;
}
