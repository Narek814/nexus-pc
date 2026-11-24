#!/usr/bin/env python3
# TEST KEYS ONLY. Test key A is derived from a fixed public string, exactly like
# tools/gen_entitlement_fixtures.py; it is not a secret and is never used outside tests.
"""Reference verifier of entitlement tokens (docs/API.md «Токен прав»). Dev-only: needs `cryptography`.

Usage (python3 -I):
  verify_tokens.py --stdin --now N --pc-id UUID   tokens from stdin (one per line), prints "ok" or
                                                  "fail <reason>" per line; exit 0 only if all ok
  verify_tokens.py --fixtures PATH                checks every case of entitlements.v1.json against
                                                  its expect.ok/reason; exit 0 only if all match
  verify_tokens.py --test-key-pkcs8               prints base64 PKCS#8 of test key A (for the TS test)
"""
import base64, json, re, sys
from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import encode_dss_signature

ISSUER = "nexus-test-issuer"
KID_A = "test-es256-a"
KEY_A = ec.derive_private_key(int.from_bytes(b"nexus-ent-test-key-A-TEST-ONLY!!", "big"), ec.SECP256R1())
# Pinned key table, as in the fixtures: kid -> (alg, public key or None).
PINNED = {KID_A: ("ES256", KEY_A.public_key()), "test-ed25519-reserved": ("Ed25519", None)}
REVOKED = {"test-es256-revoked"}

KID_RE = re.compile(r"[A-Za-z0-9_-]{1,64}")
B64U_RE = re.compile(r"[A-Za-z0-9_-]+")
PRINTABLE_RE = re.compile(r"[\x21-\x7e]{1,128}")
UUID_RE = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
FEATURE_RE = re.compile(r"[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*){0,3}")
FIELDS = {"v", "typ", "iss", "aud", "sub", "pcId", "plan", "features", "iat", "exp", "graceUntil", "jti"}


class Reject(Exception):
    pass


def b64u_strict(s):
    if not B64U_RE.fullmatch(s) or len(s) % 4 == 1:
        raise Reject("format")
    raw = base64.urlsafe_b64decode(s + "=" * (-len(s) % 4))
    if base64.urlsafe_b64encode(raw).rstrip(b"=").decode() != s:
        raise Reject("format")  # non-zero trailing bits
    return raw


def _pairs(pairs):
    d = {}
    for k, v in pairs:
        if k in d:
            raise Reject("json")
        d[k] = v
    return d


def _int(s):
    if s == "-0":
        raise Reject("json")
    return int(s)


def _no(_):
    raise Reject("json")


def strict_json(b):
    try:
        return json.loads(b.decode("utf-8"), object_pairs_hook=_pairs, parse_float=_no, parse_int=_int, parse_constant=_no)
    except Reject:
        raise
    except Exception:
        raise Reject("json")


def is_int(x):
    return type(x) is int


def schema_ok(p):
    if not isinstance(p, dict) or set(p) != FIELDS:
        return False
    s = lambda x, r: isinstance(x, str) and r.fullmatch(x) is not None
    f = p["features"]
    return (is_int(p["v"]) and p["v"] == 1 and p["typ"] == "nexus-ent+v1" and p["aud"] == "nexus-agent"
            and s(p["iss"], PRINTABLE_RE) and s(p["sub"], PRINTABLE_RE)
            and s(p["pcId"], UUID_RE) and s(p["jti"], UUID_RE) and p["plan"] in ("free", "pro")
            and isinstance(f, list) and len(f) <= 64 and len(set(map(repr, f))) == len(f)
            and all(isinstance(x, str) and len(x) <= 64 and FEATURE_RE.fullmatch(x) for x in f)
            and all(is_int(p[k]) and 1700000000 <= p[k] <= 4102444800 for k in ("iat", "exp", "graceUntil")))


def verify(token, now, pc_id):
    """Returns the payload or raises Reject(reason); order of checks as in docs/API.md."""
    if len(token) > 4096:
        raise Reject("format")
    parts = token.split(".")
    if len(parts) != 3 or not all(parts):
        raise Reject("format")
    kid, seg, sig_s = parts
    if not KID_RE.fullmatch(kid):
        raise Reject("format")
    payload_raw = b64u_strict(seg)
    sig = b64u_strict(sig_s)
    if len(sig) != 64:
        raise Reject("format")
    if kid in REVOKED:
        raise Reject("revokedKid")
    if kid not in PINNED:
        raise Reject("unknownKid")
    alg, pub = PINNED[kid]
    if alg != "ES256":
        raise Reject("algorithm")
    der = encode_dss_signature(int.from_bytes(sig[:32], "big"), int.from_bytes(sig[32:], "big"))
    try:
        pub.verify(der, (kid + "." + seg).encode("ascii"), ec.ECDSA(hashes.SHA256()))
    except InvalidSignature:
        raise Reject("signature")
    p = strict_json(payload_raw)
    if not schema_ok(p):
        raise Reject("schema")
    if p["iss"] != ISSUER:
        raise Reject("issuer")
    if p["pcId"] != pc_id:
        raise Reject("pcId")
    if not 0 < p["exp"] - p["iat"] <= 86400:
        raise Reject("lifetime")
    if not 0 <= p["graceUntil"] - p["exp"] <= 604800:
        raise Reject("grace")
    if p["iat"] > now + 300:
        raise Reject("notYetValid")
    if now > p["graceUntil"]:
        raise Reject("expired")
    return p


def check(token, now, pc_id):
    try:
        verify(token, now, pc_id)
        return None
    except Reject as e:
        return str(e)


def main(argv):
    if argv == ["--test-key-pkcs8"]:
        der = KEY_A.private_bytes(serialization.Encoding.DER, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
        print(base64.b64encode(der).decode())
        return 0
    if len(argv) == 2 and argv[0] == "--fixtures":
        with open(argv[1], encoding="utf-8") as f:
            fx = json.load(f)
        spki = base64.b64encode(KEY_A.public_key().public_bytes(
            serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)).decode()
        key_a = [k for k in fx["keys"] if k["kid"] == KID_A]
        if len(key_a) != 1 or key_a[0]["publicKey"] != spki:
            print("FAIL derived test key A does not match fixtures")
            return 1
        ctx, bad = fx["context"], 0
        for c in fx["cases"]:
            got = check(c["token"], ctx["now"], ctx["pcId"])
            want = None if c["expect"]["ok"] else c["expect"]["reason"]
            if got != want:
                bad += 1
                print("FAIL %s: got %s, want %s" % (c["name"], got or "ok", want or "ok"))
        print("fixtures: %d/%d cases match" % (len(fx["cases"]) - bad, len(fx["cases"])))
        return 1 if bad else 0
    if len(argv) == 5 and argv[0] == "--stdin" and argv[1] == "--now" and argv[3] == "--pc-id":
        now, pc_id, n, bad = int(argv[2]), argv[4], 0, 0
        for line in sys.stdin.read().splitlines():
            if not line:
                continue
            n += 1
            r = check(line, now, pc_id)
            bad += r is not None
            print("ok" if r is None else "fail " + r)
        print("tokens: %d/%d ok" % (n - bad, n))
        return 1 if bad or n == 0 else 0
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
