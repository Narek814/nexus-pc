// node --test for backend/supabase/functions/_shared/entitlement.ts.
// Test key A is not stored here: its PKCS#8 is derived at run time by verify_tokens.py
// from the same public seed string as tools/gen_entitlement_fixtures.py.
import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { signEntitlement, type SignEntitlementInput } from "../../supabase/functions/_shared/entitlement.ts";

const HERE = dirname(fileURLToPath(import.meta.url));
const VERIFY = join(HERE, "..", "py", "verify_tokens.py");
const FIXTURES = join(HERE, "..", "..", "..", "shared", "protocol", "fixtures", "entitlements.v1.json");
const PKCS8 = execFileSync("python3", ["-I", VERIFY, "--test-key-pkcs8"], { encoding: "utf8" }).trim();
const NOW = 1795348800; // 2026-11-22T12:00:00Z, fixtures context.now
const PC = "86a77e46-afba-4ab0-8f08-68ff922e9cc2";
const B64U = /^[A-Za-z0-9_-]+$/;

function base(over: Partial<SignEntitlementInput> = {}): SignEntitlementInput {
  return {
    kid: "test-es256-a", privateKeyPkcs8B64: PKCS8, iss: "nexus-test-issuer", sub: "test-subject-0001",
    pcId: PC, plan: "pro", features: ["automations", "files.unlimited", "rgb.sync"],
    now: NOW - 60, ttlSec: 86400, graceSec: 604800, ...over,
  };
}

function b64uDecode(s: string): Uint8Array {
  assert.match(s, B64U);
  assert.notEqual(s.length % 4, 1);
  return new Uint8Array(Buffer.from(s, "base64url"));
}

async function publicKey(): Promise<CryptoKey> {
  const priv = await crypto.subtle.importKey("pkcs8", Buffer.from(PKCS8, "base64"),
    { name: "ECDSA", namedCurve: "P-256" }, true, ["sign"]);
  const jwk = await crypto.subtle.exportKey("jwk", priv);
  const pub = await crypto.subtle.importKey("jwk", { kty: "EC", crv: "P-256", x: jwk.x, y: jwk.y },
    { name: "ECDSA", namedCurve: "P-256" }, true, ["verify"]);
  // The derived key must be the pinned fixture key A.
  const fx = JSON.parse(readFileSync(FIXTURES, "utf8"));
  const spki = Buffer.from(await crypto.subtle.exportKey("spki", pub)).toString("base64");
  assert.equal(spki, fx.keys.find((k: { kid: string }) => k.kid === "test-es256-a").publicKey);
  return pub;
}

// Minimal hand-written check of shared/schemas/entitlement.v1.schema.json.
function assertSchema(p: Record<string, unknown>): void {
  const fields = ["v", "typ", "iss", "aud", "sub", "pcId", "plan", "features", "iat", "exp", "graceUntil", "jti"];
  assert.deepEqual(Object.keys(p).sort(), [...fields].sort());
  assert.equal(p.v, 1);
  assert.equal(p.typ, "nexus-ent+v1");
  assert.equal(p.aud, "nexus-agent");
  for (const k of ["iss", "sub"]) assert.match(p[k] as string, /^[\x21-\x7e]{1,128}$/);
  const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
  assert.match(p.pcId as string, uuid);
  assert.match(p.jti as string, uuid);
  assert.ok(p.plan === "free" || p.plan === "pro");
  const f = p.features as string[];
  assert.ok(Array.isArray(f) && f.length <= 64 && new Set(f).size === f.length);
  for (const x of f) assert.match(x, /^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*){0,3}$/);
  for (const k of ["iat", "exp", "graceUntil"]) {
    const n = p[k] as number;
    assert.ok(Number.isInteger(n) && n >= 1700000000 && n <= 4102444800, k);
  }
}

test("token structure, signature and payload schema", async () => {
  const pub = await publicKey();
  const tok = await signEntitlement(base());
  assert.ok(tok.length <= 4096);
  const parts = tok.split(".");
  assert.equal(parts.length, 3);
  assert.ok(!tok.includes("="));
  const [kid, seg, sigS] = parts;
  assert.equal(kid, "test-es256-a");
  const sig = b64uDecode(sigS);
  assert.equal(sig.length, 64);
  assert.equal(Buffer.from(sig).toString("base64url"), sigS);
  assert.ok(await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, pub, sig,
    new TextEncoder().encode(kid + "." + seg)));
  const raw = Buffer.from(b64uDecode(seg)).toString("utf8");
  assert.ok(!/[.eE]/.test(raw.replace(/"[^"]*"/g, "")), "numbers are plain integers");
  const p = JSON.parse(raw);
  assertSchema(p);
  assert.equal(p.iat, NOW - 60);
  assert.equal(p.exp - p.iat, 86400);
  assert.equal(p.graceUntil - p.exp, 604800);
});

test("signature does not verify if kid is swapped", async () => {
  const pub = await publicKey();
  const [, seg, sigS] = (await signEntitlement(base())).split(".");
  assert.equal(await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, pub, b64uDecode(sigS),
    new TextEncoder().encode("test-es256-b." + seg)), false);
});

test("pro token with screen.video keeps feature order and matches schema (BE-1)", async () => {
  const features = ["profiles", "rgb.sync", "automations", "monitoring.extended", "files.unlimited", "screen.video"];
  const p = JSON.parse(Buffer.from((await signEntitlement(base({ features }))).split(".")[1],
    "base64url").toString("utf8"));
  assertSchema(p);
  assert.equal(p.plan, "pro");
  assert.deepEqual(p.features, features);
});

test("free plan token with empty features", async () => {
  const p = JSON.parse(Buffer.from((await signEntitlement(base({ plan: "free", features: [] }))).split(".")[1],
    "base64url").toString("utf8"));
  assertSchema(p);
  assert.equal(p.plan, "free");
  assert.deepEqual(p.features, []);
});

test("rejects ttl, grace, kid and other bad input", async () => {
  const bad: Partial<SignEntitlementInput>[] = [
    { ttlSec: 86401 }, { ttlSec: 0 }, { ttlSec: 1.5 }, { graceSec: 604801 }, { graceSec: -1 },
    { kid: "" }, { kid: "test+es256" }, { kid: "k".repeat(65) }, { kid: "a.b" },
    { now: 1.5 }, { pcId: "86A77E46-AFBA-4AB0-8F08-68FF922E9CC2" }, { iss: "" }, { sub: "has space" },
    { plan: "gold" as "pro" }, { features: ["Bad"] }, { features: ["a", "a"] },
    { privateKeyPkcs8B64: "AAAA" },
    // 64 features of 64 chars: payload alone exceeds 4096 characters.
    { features: Array.from({ length: 64 }, (_, i) => "f" + String(i).padStart(2, "0") + "a".repeat(61)) },
  ];
  for (const over of bad) {
    await assert.rejects(signEntitlement(base(over)), /signEntitlement:/, JSON.stringify(over).slice(0, 80));
  }
  // Boundaries are accepted.
  await signEntitlement(base({ ttlSec: 1, graceSec: 0 }));
  await signEntitlement(base({ kid: "k".repeat(64) }));
});

test("12 generated tokens pass the Python reference verifier", async () => {
  const toks: string[] = [];
  for (let i = 0; i < 12; i++) {
    toks.push(await signEntitlement(base({
      plan: i % 2 ? "free" : "pro", features: i % 2 ? [] : ["automations"],
      now: NOW - i * 3600, ttlSec: 3600 + i * 600, graceSec: i * 50000,
    })));
  }
  const jtis = new Set(toks.map((t) => JSON.parse(Buffer.from(t.split(".")[1], "base64url").toString()).jti));
  assert.equal(jtis.size, 12);
  const out = execFileSync("python3", ["-I", VERIFY, "--stdin", "--now", String(NOW), "--pc-id", PC],
    { input: toks.join("\n") + "\n", encoding: "utf8" });
  assert.match(out, /^tokens: 12\/12 ok$/m);
});
