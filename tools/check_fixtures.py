#!/usr/bin/env python3
"""Checks that shared/protocol/fixtures agree with shared/schemas.

Dev-time check only (not a runtime dependency). Requires the `jsonschema`
Python package (4.x).
Usage: python3 -I tools/check_fixtures.py
"""
import json
import pathlib
import sys

from jsonschema import Draft202012Validator

root = pathlib.Path(__file__).resolve().parent.parent
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
print(f"{len(fixtures['cases']) - failures}/{len(fixtures['cases'])} fixtures agree with schema")
sys.exit(1 if failures else 0)
