// Proof of possession for signed PC requests (docs/BACKEND.md «Подписи запросов ПК»).
// Strict P-256 SPKI, ECDSA P-256/SHA-256 P1363 verification, strict base64url.
// The server builds the signed strings itself from request fields.

import { b64uEncode } from "./entitlement.ts";
import { badRequest } from "./http.ts";

export { b64uEncode };

const B64U_RE = /^[A-Za-z0-9_-]*$/;
export const UUID_LOWER_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

// Strict base64url without padding; non-canonical encodings (non-zero
// trailing bits) are rejected. Returns null on any error.
export function b64uDecodeStrict(s: string): Uint8Array | null {
  if (typeof s !== "string" || !B64U_RE.test(s) || s.length % 4 === 1) return null;
  let bin: string;
  try {
    bin = atob(s.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - (s.length % 4)) % 4));
  } catch {
    return null;
  }
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  if (b64uEncode(out) !== s) return null;
  return out;
}

// DER prefix of SubjectPublicKeyInfo { id-ecPublicKey, prime256v1 } and
// BIT STRING (66 bytes, 0 unused bits); followed by 0x04 || X || Y.
const SPKI_P256_PREFIX = new Uint8Array([
  0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01,
  0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00,
]);
export const SPKI_P256_LEN = 91;

// Returns a verify key for a canonical uncompressed P-256 SPKI, or null.
// importKey rejects points that are not on the curve.
export async function importP256Spki(spki: Uint8Array): Promise<CryptoKey | null> {
  if (spki.length !== SPKI_P256_LEN) return null;
  for (let i = 0; i < SPKI_P256_PREFIX.length; i++) if (spki[i] !== SPKI_P256_PREFIX[i]) return null;
  if (spki[SPKI_P256_PREFIX.length] !== 0x04) return null;
  try {
    return await crypto.subtle.importKey("spki", spki, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
  } catch {
    return null;
  }
}

// ECDSA P-256 / SHA-256, signature IEEE P1363 r||s of exactly 64 bytes.
export async function verifyP1363(spki: Uint8Array, sig: Uint8Array, message: string): Promise<boolean> {
  if (sig.length !== 64) return false;
  const key = await importP256Spki(spki);
  if (!key) return false;
  try {
    return await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, sig,
      new TextEncoder().encode(message));
  } catch {
    return false;
  }
}

export async function sha256hex(s: string): Promise<string> {
  const d = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s)));
  let h = "";
  for (const b of d) h += b.toString(16).padStart(2, "0");
  return h;
}

export function isPcId(s: string): boolean {
  return UUID_LOWER_RE.test(s);
}

export function nonceOk(n: Uint8Array | null): n is Uint8Array {
  return n !== null && n.length >= 16 && n.length <= 32;
}

const P = "nexus-pc-login-v1";

export async function startString(pcId: string, spkiB64u: string, name: string, nonceB64u: string,
  ts: number): Promise<string> {
  return `${P}|start|${pcId}|${spkiB64u}|${await sha256hex(name)}|${nonceB64u}|${ts}`;
}

export async function consumeString(pcId: string, deviceCode: string, nonceB64u: string,
  ts: number): Promise<string> {
  return `${P}|consume|${pcId}|${await sha256hex(deviceCode)}|${nonceB64u}|${ts}`;
}

export async function refreshString(pcId: string, refreshToken: string, nonceB64u: string,
  ts: number): Promise<string> {
  return `${P}|refresh|${pcId}|${await sha256hex(refreshToken)}|${nonceB64u}|${ts}`;
}

export const TS_MIN = 1;
export const TS_MAX = 4102444800;

// Common fields of a signed PC request; malformed -> bad.request.
export function signedFields(pcId: string, nonceB64u: string, ts: number, sigB64u: string):
  { nonce: Uint8Array; sig: Uint8Array } {
  const nonce = b64uDecodeStrict(nonceB64u);
  const sig = b64uDecodeStrict(sigB64u);
  if (!isPcId(pcId) || !nonceOk(nonce) || sig === null || sig.length !== 64 || ts < TS_MIN || ts > TS_MAX) {
    throw badRequest();
  }
  return { nonce, sig };
}
