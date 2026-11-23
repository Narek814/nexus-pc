#!/usr/bin/env python3
# TEST KEYS ONLY. Keys are derived from fixed public strings; never pin, ship or use them outside fixtures.
"""Regenerates shared/protocol/fixtures/entitlements.v1.json, format kid "." b64u(payload) "." b64u(sig).
Manual use only, NOT run in CI. Requires the dev-only `cryptography` package; no network access.
ECDSA signatures use a random nonce, so each run produces different signature segments.
Writes only the file given as the argument.
Usage: python3 -I tools/gen_entitlement_fixtures.py <output.json>"""
import base64, datetime, json, sys
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature

def b64u(b): return base64.urlsafe_b64encode(b).rstrip(b"=").decode()
def key(seed, curve=ec.SECP256R1()): return ec.derive_private_key(int.from_bytes(seed, "big"), curve)
def spki(k): return base64.b64encode(k.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)).decode()
def pkcs8(k): return base64.b64encode(k.private_bytes(serialization.Encoding.DER, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())).decode()
def sig(k, data, h=hashes.SHA256(), size=32):
    r, s = decode_dss_signature(k.sign(data, ec.ECDSA(h)))
    return r.to_bytes(size, "big") + s.to_bytes(size, "big")

now = int(datetime.datetime(2026, 11, 22, 12, 0, tzinfo=datetime.timezone.utc).timestamp())
keyA = key(b"nexus-ent-test-key-A-TEST-ONLY!!")
keyX = key(b"nexus-ent-test-key-X-not-pinned")
key384 = key(b"nexus-ent-test-key-384-TEST-ONLY-not-pinned-xx", ec.SECP384R1())
PCID = "86a77e46-afba-4ab0-8f08-68ff922e9cc2"
KID = "test-es256-a"
base = {"v": 1, "typ": "nexus-ent+v1", "iss": "nexus-test-issuer", "aud": "nexus-agent", "sub": "test-subject-0001",
        "pcId": PCID, "plan": "pro", "features": ["automations", "files.unlimited", "rgb.sync"],
        "iat": now - 3600, "exp": now - 3600 + 86400, "graceUntil": now - 3600 + 86400 + 604800,
        "jti": "5b1f6c2e-9d3a-4e7b-8f10-2a3b4c5d6e7f"}
J = lambda i: "5b1f6c2e-9d3a-4e7b-8f10-2a3b4c5d6e%02x" % i

def raw(p): return json.dumps(p, separators=(",", ":")) if isinstance(p, dict) else p

def token(p, kid=KID, k=keyA, mode="es256"):
    seg = b64u(raw(p).encode("utf-8"))
    signed = (kid + "." + seg).encode("ascii")
    if mode == "es256": s = sig(k, signed)
    elif mode == "es384": s = sig(k, signed, hashes.SHA384(), 48)
    elif mode == "flip": s = bytearray(sig(k, signed)); s[10] ^= 1; s = bytes(s)
    elif mode == "payloadOnly": s = sig(k, seg.encode("ascii"))  # old ENT-1 format: kid not covered
    return kid + "." + seg + "." + b64u(s)

cases = []
def add(name, tok, schema_valid, reason=None):
    c = {"name": name, "token": tok, "schemaValid": schema_valid, "expect": {"ok": reason is None}}
    if reason: c["expect"]["reason"] = reason
    cases.append(c)
def case(name, p, schema_valid=True, reason=None, **kw): add(name, token(p, **kw), schema_valid, reason)

# positive
case("valid pro token", base)
case("valid free token without features", {**base, "plan": "free", "features": [], "jti": J(1)})
case("valid token in grace (exp passed, graceUntil ahead)", {**base, "iat": now - 2 * 86400, "exp": now - 86400, "graceUntil": now - 86400 + 604800, "jti": J(2)})
case("iat in the future within 5 min is accepted", {**base, "iat": now + 299, "exp": now + 299 + 3600, "graceUntil": now + 299 + 3600, "jti": J(3)})
case("valid token with fields in another order", dict(reversed(list({**base, "jti": J(4)}.items()))))

good = token({**base, "jti": J(5)})
kid_, seg_, sig_ = good.split(".")

# format (E4): segments, kid charset/length, strict base64url, signature length
add("two segments (no kid) are rejected", seg_ + "." + sig_, False, "format")
add("four segments are rejected", good + "." + sig_, False, "format")
add("empty kid segment", "." + seg_ + "." + sig_, False, "format")
add("empty payload segment", kid_ + ".." + sig_, False, "format")
add("empty signature segment", kid_ + "." + seg_ + ".", False, "format")
add("kid with a character outside [A-Za-z0-9_-]", "test+es256." + seg_ + "." + sig_, False, "format")
add("kid longer than 64 characters", "k" * 65 + "." + seg_ + "." + sig_, False, "format")
add("padding '=' in base64url", kid_ + "." + seg_ + "." + sig_ + "==", False, "format")
add("standard base64 '+' or '/' instead of base64url", kid_ + "." + seg_ + "." + sig_[:-3] + "+/" + sig_[-1], False, "format")
add("space inside a segment", kid_ + "." + seg_[:5] + " " + seg_[5:] + "." + sig_, False, "format")
add("line break inside a segment", kid_ + "." + seg_ + "." + sig_[:40] + "\n" + sig_[40:], False, "format")
# 64 bytes -> 86 chars, last char carries 2 data bits + 4 padding bits; set a padding bit.
alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
last = alphabet.index(sig_[-1])
add("non-zero trailing bits in signature base64url", kid_ + "." + seg_ + "." + sig_[:-1] + alphabet[last ^ 1], False, "format")
signed5 = (KID + "." + seg_).encode("ascii")
add("signature 63 bytes", kid_ + "." + seg_ + "." + b64u(sig(keyA, signed5)[:63]), False, "format")
add("signature 65 bytes", kid_ + "." + seg_ + "." + b64u(sig(keyA, signed5) + b"\x00"), False, "format")
case("ES384 signature (96 bytes) for an ES256 kid", {**base, "jti": J(6)}, False, "format", k=key384, mode="es384")

# key selection
case("revoked kid", {**base, "jti": J(7)}, True, "revokedKid", kid="test-es256-revoked", k=keyX)
case("unknown kid", {**base, "jti": J(8)}, True, "unknownKid", kid="test-es256-z", k=keyX)
case("alg/kid mismatch: kid pinned for Ed25519, ES256 signature", {**base, "jti": J(9)}, True, "algorithm", kid="test-ed25519-reserved")

# signature (E1: covers kid too)
case("wrong signature", {**base, "jti": J(10)}, True, "signature", mode="flip")
case("signed by a key that is not pinned for kid", {**base, "jti": J(11)}, True, "signature", k=keyX)
case("signature over payload only (kid not covered)", {**base, "jti": J(12)}, True, "signature", mode="payloadOnly")

# json (E2 duplicates, E3 numbers) — genuinely signed, rejected by the strict parser before the schema
B = json.dumps({**base, "jti": J(14)}, separators=(",", ":"))
case("duplicate top-level key (plan)", B[:-1] + ',"plan":"pro"}', False, "json")
case("duplicate top-level key with different value (exp)", B[:-1] + ',"exp":%d}' % (now + 10 ** 6), False, "json")
case("duplicate key in a nested object", B.replace('"features":["automations","files.unlimited","rgb.sync"]', '"features":[{"a":1,"a":2}]'), False, "json")
case("duplicate key in a deeply nested object", B.replace('"features":["automations","files.unlimited","rgb.sync"]', '"features":[{"x":{"b":1,"b":1}}]'), False, "json")
case("number with exponent", B.replace('"iat":%d' % base["iat"], '"iat":%de0' % base["iat"]), False, "json")
case("number with fraction", B.replace('"exp":%d' % base["exp"], '"exp":%d.5' % base["exp"]), False, "json")
case("integer written as x.0", B.replace('"v":1', '"v":1.0'), False, "json")
case("negative zero", B.replace('"v":1', '"v":-0'), False, "json")

# schema
case("time field as a string", {**base, "iat": str(base["iat"]), "jti": J(15)}, False, "schema")
case("negative time field", {**base, "iat": -1, "jti": J(16)}, False, "schema")
case("foreign aud", {**base, "aud": "nexus-ios", "jti": J(17)}, False, "schema")
case("extra field (alg)", {**base, "alg": "none", "jti": J(18)}, False, "schema")
case("extra field (kid inside payload)", {**base, "kid": KID, "jti": J(19)}, False, "schema")
case("extra field (email)", {**base, "email": "a@example.com", "jti": J(20)}, False, "schema")
case("missing required field graceUntil", {k: v for k, v in {**base, "jti": J(21)}.items() if k != "graceUntil"}, False, "schema")
case("missing required field jti", {k: v for k, v in base.items() if k != "jti"}, False, "schema")
case("wrong typ", {**base, "typ": "JWT", "jti": J(22)}, False, "schema")
case("wrong v", {**base, "v": 2, "jti": J(23)}, False, "schema")
case("payload is not JSON", "not json", False, "json")

# semantic
case("foreign iss", {**base, "iss": "evil-issuer", "jti": J(24)}, True, "issuer")
case("foreign pcId", {**base, "pcId": "11111111-2222-4333-8444-555555555555", "jti": J(25)}, True, "pcId")
case("exp - iat > 24 h", {**base, "exp": base["iat"] + 86401, "graceUntil": base["iat"] + 86401 + 604800, "jti": J(26)}, True, "lifetime")
case("exp <= iat", {**base, "exp": base["iat"], "graceUntil": base["iat"] + 604800, "jti": J(27)}, True, "lifetime")
case("graceUntil - exp > 7 d", {**base, "graceUntil": base["exp"] + 604801, "jti": J(28)}, True, "grace")
case("graceUntil < exp", {**base, "graceUntil": base["exp"] - 1, "jti": J(29)}, True, "grace")
case("iat in the future > 5 min", {**base, "iat": now + 301, "exp": now + 301 + 3600, "graceUntil": now + 301 + 3600 + 604800, "jti": J(30)}, True, "notYetValid")
case("expired past graceUntil", {**base, "iat": now - 9 * 86400, "exp": now - 8 * 86400, "graceUntil": now - 86400, "jti": J(31)}, True, "expired")

fx = {
    "description": "Entitlement token cases (docs/API.md «Токен прав»). Token: kid \".\" base64url(payload) \".\" base64url(ES256 r||s); the signature covers the ASCII bytes of kid \".\" payload-segment. `schemaValid` — the payload segment decodes, parses strictly (no duplicate keys, integers only) and matches shared/schemas/entitlement.v1.schema.json. `expect.reason` — first failing check (order in docs/API.md).",
    "WARNING": "TEST KEYS ONLY. The private key below is public test data for fixtures. Never pin, ship or use it outside tests.",
    "version": 2,
    "context": {"now": now, "nowIso": "2026-11-22T12:00:00Z", "pcId": PCID, "issuer": "nexus-test-issuer", "audience": "nexus-agent", "maxClockSkewSeconds": 300},
    "keys": [
        {"kid": KID, "alg": "ES256", "publicKey": spki(keyA), "privateKeyTestOnly": pkcs8(keyA)},
        {"kid": "test-ed25519-reserved", "alg": "Ed25519", "publicKey": None, "note": "Pinned for an algorithm the agent does not verify: any token with this kid is rejected (algorithm)."},
    ],
    "revokedKids": ["test-es256-revoked"],
    "unpinnedTestKeys": {"note": "NOT pinned; used to produce wrong signatures.", "p256": spki(keyX), "p384": spki(key384)},
    "cases": cases,
}
with open(sys.argv[1], "w", encoding="utf-8") as f:
    f.write(json.dumps(fx, indent=2, ensure_ascii=False) + "\n")
