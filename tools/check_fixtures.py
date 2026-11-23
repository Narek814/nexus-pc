#!/usr/bin/env python3
"""Checks that shared/protocol/fixtures agree with shared/schemas.

Dev-time check only (not a runtime dependency). Requires the `jsonschema`
Python package (4.x).
Usage: python3 -I tools/check_fixtures.py
"""
import base64
import json
import pathlib
import re
import sys

from jsonschema import Draft202012Validator

# Optional argument: alternative repository root (used to self-test the exit code on a corrupted copy).
root = pathlib.Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else pathlib.Path(__file__).resolve().parent.parent
schema = json.loads((root / "shared/schemas/request.v1.schema.json").read_text(encoding="utf-8"))
Draft202012Validator.check_schema(schema)
validator = Draft202012Validator(schema, format_checker=Draft202012Validator.FORMAT_CHECKER)

# `ts` is enforced by an explicit pattern in the schema, so the check does not depend on
# optional format-checker packages being installed.

fixtures = json.loads((root / "shared/protocol/fixtures/requests.v1.json").read_text(encoding="utf-8"))
failures = 0
for case in fixtures["cases"]:
    actual = validator.is_valid(case["message"])
    if actual != case["schemaValid"]:
        failures += 1
        errors = [e.message for e in validator.iter_errors(case["message"])]
        print(f"FAIL {case['name']}: schemaValid={case['schemaValid']} but schema says {actual}; {errors[:2]}")
print(f"{len(fixtures['cases']) - failures}/{len(fixtures['cases'])} request fixtures agree with schema")

response_schema = json.loads((root / "shared/schemas/response.v1.schema.json").read_text(encoding="utf-8"))
Draft202012Validator.check_schema(response_schema)
response_validator = Draft202012Validator(response_schema)
responses = json.loads((root / "shared/protocol/fixtures/responses.v1.json").read_text(encoding="utf-8"))
response_failures = 0
for case in responses["cases"]:
    if response_validator.is_valid(case["message"]) != case["valid"]:
        response_failures += 1
        print(f"FAIL response {case['name']}: expected valid={case['valid']}")
print(f"{len(responses['cases']) - response_failures}/{len(responses['cases'])} response fixtures agree with schema")
entitlement_schema = json.loads((root / "shared/schemas/entitlement.v1.schema.json").read_text(encoding="utf-8"))
Draft202012Validator.check_schema(entitlement_schema)
entitlement_validator = Draft202012Validator(entitlement_schema)
entitlements = json.loads((root / "shared/protocol/fixtures/entitlements.v1.json").read_text(encoding="utf-8"))

# Token format: kid "." base64url(payload) "." base64url(signature), strict base64url without padding,
# 64-byte signature (86 characters), strict JSON (no duplicate keys, integers only, no -0).
B64U = re.compile(r"[A-Za-z0-9_-]+")
KID = re.compile(r"[A-Za-z0-9_-]{1,64}")


def b64u_strict(segment):
    if not B64U.fullmatch(segment) or len(segment) % 4 == 1:
        raise ValueError("base64url")
    raw = base64.urlsafe_b64decode(segment + "=" * (-len(segment) % 4))
    if base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii") != segment:  # non-zero trailing bits
        raise ValueError("base64url")
    return raw


def no_duplicate_keys(pairs):
    keys = [k for k, _ in pairs]
    if len(keys) != len(set(keys)):
        raise ValueError("duplicate key")
    return dict(pairs)


def reject_number(_literal):
    raise ValueError("non-integer number")


def strict_int(literal):
    if literal == "-0":  # valid JSON, but not allowed in tokens
        raise ValueError("-0")
    return int(literal)


def token_payload(token):
    parts = token.split(".")
    if len(parts) != 3 or not KID.fullmatch(parts[0]):
        raise ValueError("format")
    payload = b64u_strict(parts[1])
    if len(parts[2]) != 86 or len(b64u_strict(parts[2])) != 64:
        raise ValueError("format")
    return json.loads(payload.decode("utf-8"), object_pairs_hook=no_duplicate_keys,
                      parse_float=reject_number, parse_int=strict_int, parse_constant=reject_number)


entitlement_failures = 0
for case in entitlements["cases"]:
    try:
        actual = entitlement_validator.is_valid(token_payload(case["token"]))
    except ValueError:  # also covers json.JSONDecodeError, binascii.Error, UnicodeDecodeError
        actual = False
    if actual != case["schemaValid"]:
        entitlement_failures += 1
        print(f"FAIL entitlement {case['name']}: schemaValid={case['schemaValid']} but schema says {actual}")
print(f"{len(entitlements['cases']) - entitlement_failures}/{len(entitlements['cases'])} entitlement fixtures agree with schema")
sys.exit(1 if failures or response_failures or entitlement_failures else 0)
