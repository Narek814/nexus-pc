// node --test: _shared/codes.ts (user/device codes, HMAC, refresh derivation).
import { test } from "node:test";
import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import { deriveRefresh, hmac, importHmacKey, newDeviceCode, newRefreshToken, newUserCode, normalizeUserCode,
  USER_CODE_ALPHABET } from "../../supabase/functions/_shared/codes.ts";
import { b64uDecodeStrict } from "../../supabase/functions/_shared/pop.ts";

test("alphabet: 31 symbols, no 0/O/1/I/L, no duplicates", () => {
  assert.equal(USER_CODE_ALPHABET, "23456789ABCDEFGHJKMNPQRSTUVWXYZ");
  assert.equal(new Set(USER_CODE_ALPHABET).size, 31);
  for (const c of "0O1IL") assert.ok(!USER_CODE_ALPHABET.includes(c));
});

test("userCode: 100000 codes, only alphabet, chi-square uniform", () => {
  const counts = new Map<string, number>();
  for (let i = 0; i < 100000; i++) {
    const c = newUserCode();
    assert.equal(c.length, 8);
    for (const ch of c) counts.set(ch, (counts.get(ch) ?? 0) + 1);
  }
  for (const ch of counts.keys()) assert.ok(USER_CODE_ALPHABET.includes(ch), ch);
  assert.equal(counts.size, 31);
  const exp = 800000 / 31;
  let chi = 0;
  for (const ch of USER_CODE_ALPHABET) chi += ((counts.get(ch) ?? 0) - exp) ** 2 / exp;
  // df = 30: p = 0.0001 critical value is about 66.6.
  assert.ok(chi < 66.6, "chi2 " + chi);
});

test("userCode: rejection sampling drops bytes >= 248", () => {
  let k = 0;
  const seq = [255, 248, 249, 0, 30, 31, 247, 250, 62, 93, 1];
  const code = newUserCode((b) => { for (let i = 0; i < b.length; i++) b[i] = seq[k++ % seq.length]; return b; });
  // 0->'2', 30->'Z', 31->'2', 247->247%31=30 'Z', 62->'2', 93->'2', 1->'3', then 0
  assert.equal(code, "2Z2Z2232");
});

test("normalizeUserCode", () => {
  assert.equal(normalizeUserCode("abcd-efgh"), "ABCDEFGH");
  assert.equal(normalizeUserCode(" ab cd\tef-gh "), "ABCDEFGH");
  assert.equal(normalizeUserCode("ABCDEFG"), null);
  assert.equal(normalizeUserCode("ABCDEFG0"), null);
  assert.equal(normalizeUserCode("ABCDEFGHJ"), null);
});

test("deviceCode and refresh: 32 CSPRNG bytes in b64u, unique", () => {
  const seen = new Set<string>();
  for (let i = 0; i < 1000; i++) {
    for (const v of [newDeviceCode(), newRefreshToken()]) {
      assert.equal(b64uDecodeStrict(v)?.length, 32);
      assert.ok(!seen.has(v));
      seen.add(v);
    }
  }
});

test("hmac and derive match HMAC-SHA-256 byte for byte (docs/BACKEND.md)", async () => {
  const secret = "s".repeat(40);
  const key = await importHmacKey(new TextEncoder().encode(secret));
  const h = await hmac(key, "data");
  assert.equal(h.length, 32);
  assert.equal(Buffer.from(h).toString("hex"), createHmac("sha256", secret).update("data").digest("hex"));
  const old = newRefreshToken();
  const nw = await deriveRefresh(key, old);
  assert.equal(nw, createHmac("sha256", secret).update("nexus-pc-refresh-derive-v1|" + old).digest("base64url"));
  assert.equal(await deriveRefresh(key, old), nw);
  assert.notEqual(await deriveRefresh(key, newRefreshToken()), nw);
  assert.equal(b64uDecodeStrict(nw)?.length, 32);
});
