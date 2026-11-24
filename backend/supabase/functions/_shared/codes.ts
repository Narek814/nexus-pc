// Login codes and PC refresh tokens (docs/BACKEND.md «Коды и refresh в Edge»).
// Only HMAC-SHA-256 values (32 bytes) of codes and tokens reach the database.

import { b64uEncode } from "./entitlement.ts";

// 31 symbols: digits 2-9 and A-Z without I, L, O (no 0/O, 1/I/L look-alikes).
export const USER_CODE_ALPHABET = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
export const USER_CODE_LEN = 8;
export const DEVICE_CODE_BYTES = 32;
export const REFRESH_BYTES = 32;
const REFRESH_DERIVE_CTX = "nexus-pc-refresh-derive-v1|";

const enc = new TextEncoder();

export function importHmacKey(secret: Uint8Array): Promise<CryptoKey> {
  return crypto.subtle.importKey("raw", secret, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
}

export async function hmac(key: CryptoKey, data: string | Uint8Array): Promise<Uint8Array> {
  const bytes = typeof data === "string" ? enc.encode(data) : data;
  return new Uint8Array(await crypto.subtle.sign("HMAC", key, bytes));
}

export function randomB64u(n: number): string {
  return b64uEncode(crypto.getRandomValues(new Uint8Array(n)));
}

export function newDeviceCode(): string {
  return randomB64u(DEVICE_CODE_BYTES);
}

export function newRefreshToken(): string {
  return randomB64u(REFRESH_BYTES);
}

// Rejection sampling: bytes >= floor(256 / n) * n are dropped, so every
// symbol has the same probability.
export function newUserCode(rand: (b: Uint8Array) => Uint8Array = (b) => crypto.getRandomValues(b)): string {
  const n = USER_CODE_ALPHABET.length;
  const limit = Math.floor(256 / n) * n;
  let out = "";
  const buf = new Uint8Array(16);
  while (out.length < USER_CODE_LEN) {
    rand(buf);
    for (const b of buf) {
      if (b < limit && out.length < USER_CODE_LEN) out += USER_CODE_ALPHABET[b % n];
    }
  }
  return out;
}

// User input: upper case, spaces and hyphens removed. Null if the result is
// not exactly 8 alphabet symbols.
export function normalizeUserCode(input: string): string | null {
  const s = input.toUpperCase().replace(/[\s-]/g, "");
  if (s.length !== USER_CODE_LEN) return null;
  for (const ch of s) if (!USER_CODE_ALPHABET.includes(ch)) return null;
  return s;
}

// new = b64u(HMAC(REFRESH_DERIVE_KEY, "nexus-pc-refresh-derive-v1|" || old)).
export async function deriveRefresh(deriveKey: CryptoKey, old: string): Promise<string> {
  return b64uEncode(await hmac(deriveKey, REFRESH_DERIVE_CTX + old));
}
