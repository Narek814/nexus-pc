#!/usr/bin/env bash
# Runs TS tests of backend/supabase/functions/_shared (node --test, Node 22, no packages)
# and the Python reference verifier against shared fixtures (needs `cryptography`).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
PY="${PYTHON:-python3}"

"$PY" -I "$HERE/py/verify_tokens.py" --fixtures "$ROOT/shared/protocol/fixtures/entitlements.v1.json"
node --experimental-strip-types --no-warnings --test "$HERE"/ts/*.test.ts
