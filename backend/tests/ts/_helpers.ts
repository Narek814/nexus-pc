// Shared helpers of Edge tests: fake Db, PC keys, signed requests, test config.
// No secrets in the repository: peppers are random per run, the entitlement
// key A is derived at run time by verify_tokens.py from a public seed.
import { execFileSync } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { configOnce, type Db, type Deps, type RpcOptions } from "../../supabase/functions/_shared/db.ts";
import { b64uEncode } from "../../supabase/functions/_shared/entitlement.ts";

const HERE = dirname(fileURLToPath(import.meta.url));
export const VERIFY = join(HERE, "..", "py", "verify_tokens.py");
export const PKCS8 = execFileSync("python3", ["-I", VERIFY, "--test-key-pkcs8"], { encoding: "utf8" }).trim();
export const ISS = "nexus-test-issuer";
export const NOW = 1795348800;
export const PC = "86a77e46-afba-4ab0-8f08-68ff922e9cc2";
export const USER_A = "11111111-1111-4111-8111-111111111111";
export const USER_B = "22222222-2222-4222-8222-222222222222";

export function randomSecret(): string {
  return b64uEncode(crypto.getRandomValues(new Uint8Array(48)));
}

export function testEnv(over: Record<string, string | undefined> = {}): Record<string, string | undefined> {
  return {
    CODE_HASH_PEPPER: randomSecret(), REFRESH_HASH_PEPPER: randomSecret(), REFRESH_DERIVE_KEY: randomSecret(),
    ENT_KID: "test-es256-a", ENT_SIGNING_KEY_PKCS8_B64: PKCS8, ENT_ISS: ISS, ...over,
  };
}

export interface Call { name: string; args: Record<string, unknown>; opts?: RpcOptions }

export class FakeDb implements Db {
  calls: Call[] = [];
  constructor(public handlers: Record<string, (args: Record<string, unknown>) => unknown> = {}) {}
  async rpc(name: string, args: Record<string, unknown>, opts?: RpcOptions): Promise<unknown> {
    this.calls.push({ name, args, opts });
    if (name === "rate_limit_hit" && !this.handlers[name]) return true;
    const h = this.handlers[name];
    if (!h) throw new Error("unexpected rpc " + name);
    return h(args);
  }
  names(): string[] { return this.calls.map((c) => c.name); }
  writes(): Call[] { return this.calls.filter((c) => !["rate_limit_hit", "pc_key_get", "pc_login_code_key"].includes(c.name)); }
}

export function deps(db: Db | null, env = testEnv(), extra: Partial<Deps> = {}): Deps & { lines: string[] } {
  const lines: string[] = [];
  return { db, config: configOnce((n) => env[n]), now: () => NOW, log: (l) => lines.push(l),
    secLog: (l) => lines.push(l), lines, ...extra };
}

export interface PcKey { spki: Uint8Array; spkiB64u: string; priv: CryptoKey }

export async function pcKey(): Promise<PcKey> {
  const kp = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
  const spki = new Uint8Array(await crypto.subtle.exportKey("spki", kp.publicKey));
  return { spki, spkiB64u: b64uEncode(spki), priv: kp.privateKey };
}

export async function sign(k: PcKey, msg: string): Promise<string> {
  return b64uEncode(new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, k.priv,
    new TextEncoder().encode(msg))));
}

export function nonce(n = 16): string {
  return b64uEncode(crypto.getRandomValues(new Uint8Array(n)));
}

export function hex(b: Uint8Array): string {
  return "\\x" + Buffer.from(b).toString("hex");
}

export function post(body: unknown, headers: Record<string, string> = {}): Request {
  return new Request("http://edge.test/fn", {
    method: "POST",
    headers: { "content-type": "application/json", "x-forwarded-for": "198.51.100.7", ...headers },
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
}

export function verifyTokens(tokens: string[], pcId: string): string {
  return execFileSync("python3", ["-I", VERIFY, "--stdin", "--now", String(NOW), "--pc-id", pcId],
    { input: tokens.join("\n") + "\n", encoding: "utf8" });
}

export function payload(token: string): Record<string, unknown> {
  return JSON.parse(Buffer.from(token.split(".")[1], "base64url").toString());
}

// Every bytea argument that is a hash must be exactly 32 bytes.
export const HASH_ARGS = ["p_device_code_hash", "p_user_code_hash", "p_refresh_hash", "p_old_hash", "p_new_hash"];
