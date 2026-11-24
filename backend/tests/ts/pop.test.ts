// node --test: _shared/pop.ts (strict SPKI, P1363 signatures, b64u, signed strings).
import { test } from "node:test";
import assert from "node:assert/strict";
import { b64uDecodeStrict, b64uEncode, consumeString, importP256Spki, isPcId, refreshString, sha256hex,
  signedFields, startString, verifyP1363 } from "../../supabase/functions/_shared/pop.ts";
import { pcKey, sign, PC } from "./_helpers.ts";

test("strict b64u", () => {
  assert.deepEqual(b64uDecodeStrict("AQID"), new Uint8Array([1, 2, 3]));
  assert.deepEqual(b64uDecodeStrict(""), new Uint8Array(0));
  for (const s of ["AQ==", "AQ=", "A", "AQI+", "AQI/", "AQ I", "AR", "AQJ"]) assert.equal(b64uDecodeStrict(s), null, s);
  assert.deepEqual(b64uDecodeStrict("AQ"), new Uint8Array([1]));
});

test("SPKI: only canonical uncompressed P-256", async () => {
  const k = await pcKey();
  assert.equal(k.spki.length, 91);
  assert.ok(await importP256Spki(k.spki));
  // trailing byte, truncated
  assert.equal(await importP256Spki(new Uint8Array([...k.spki, 0])), null);
  assert.equal(await importP256Spki(k.spki.slice(0, 90)), null);
  // compressed point: SEQ 0x39, BIT STRING 0x22, 0x02||X
  const comp = new Uint8Array([0x30, 0x39, ...k.spki.slice(2, 24), 0x22, 0x00, 0x02 | (k.spki[90] & 1), ...k.spki.slice(27, 59)]);
  assert.ok(await crypto.subtle.importKey("spki", comp, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"])
    .then(() => true, () => true));
  assert.equal(await importP256Spki(comp), null);
  // hybrid/other prefix byte 0x06 with same length
  const hybrid = k.spki.slice(); hybrid[26] = 0x06;
  assert.equal(await importP256Spki(hybrid), null);
  // other curve (P-384)
  const p384 = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-384" }, true, ["sign"]);
  assert.equal(await importP256Spki(new Uint8Array(await crypto.subtle.exportKey("spki", p384.publicKey))), null);
  // OID of curve altered (secp256k1 1.3.132.0.10 has other length; flip one OID byte)
  const oid = k.spki.slice(); oid[22] = 0x08;
  assert.equal(await importP256Spki(oid), null);
  // point not on the curve
  const off = k.spki.slice(); off[90] ^= 1;
  assert.equal(await importP256Spki(off), null);
});

test("P1363 verification: 64 bytes only, other key and message fail", async () => {
  const a = await pcKey(), b = await pcKey();
  const sig = Buffer.from(await sign(a, "m"), "base64url");
  assert.equal(sig.length, 64);
  assert.equal(await verifyP1363(a.spki, sig, "m"), true);
  assert.equal(await verifyP1363(b.spki, sig, "m"), false);
  assert.equal(await verifyP1363(a.spki, sig, "m2"), false);
  assert.equal(await verifyP1363(a.spki, sig.subarray(0, 63), "m"), false);
  assert.equal(await verifyP1363(a.spki, new Uint8Array([...sig, 0]), "m"), false);
  // DER-encoded signature of the same r,s
  const der = (x: Uint8Array) => { let i = 0; while (i < 31 && x[i] === 0) i++; let v = [...x.slice(i)]; if (v[0] & 0x80) v = [0, ...v]; return [0x02, v.length, ...v]; };
  const r = der(sig.subarray(0, 32)), s = der(sig.subarray(32));
  const d = new Uint8Array([0x30, r.length + s.length, ...r, ...s]);
  assert.equal(await verifyP1363(a.spki, d, "m"), false);
});

test("sha256hex lower hex; pcId lower-case UUID only", async () => {
  assert.equal(await sha256hex("abc"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  assert.ok(isPcId(PC));
  assert.ok(!isPcId(PC.toUpperCase()));
  assert.ok(!isPcId("86a77e46afba4ab08f0868ff922e9cc2"));
});

test("signed strings are exactly as in docs/BACKEND.md", async () => {
  const h = (s: string) => sha256hex(s);
  assert.equal(await startString(PC, "SPKI", "Мой ПК", "NONCE", 1795348800),
    `nexus-pc-login-v1|start|${PC}|SPKI|${await h("Мой ПК")}|NONCE|1795348800`);
  assert.equal(await consumeString(PC, "DC", "N", 5), `nexus-pc-login-v1|consume|${PC}|${await h("DC")}|N|5`);
  assert.equal(await refreshString(PC, "RT", "N", 5), `nexus-pc-login-v1|refresh|${PC}|${await h("RT")}|N|5`);
});

test("signedFields: nonce 16..32 bytes, sig 64 bytes, ts range", () => {
  const n = (k: number) => b64uEncode(new Uint8Array(k));
  const sig = n(64);
  assert.equal(signedFields(PC, n(16), 1795348800, sig).nonce.length, 16);
  assert.equal(signedFields(PC, n(32), 1795348800, sig).nonce.length, 32);
  for (const [nn, s, ts, pc] of [[n(15), sig, 1, PC], [n(33), sig, 1, PC], [n(16), n(63), 1, PC], [n(16), n(65), 1, PC],
    [n(16), sig, 0, PC], [n(16), sig, 4102444801, PC], [n(16), sig, 1, PC.toUpperCase()], [n(16) + "=", sig, 1, PC]] as const) {
    assert.throws(() => signedFields(pc, nn, ts, s));
  }
});
