#!/usr/bin/env bash
# Runs SQL/RLS tests against a throwaway local PostgreSQL cluster.
# No network, no Docker. Cluster lives in mktemp -d and is removed on exit.
set -euo pipefail

PG_BIN="${PG_BIN:-/usr/lib/postgresql/16/bin}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND="$(dirname "$HERE")"
TMP="$(mktemp -d)"
RUN=()
if [ "$(id -u)" = "0" ]; then
  # initdb refuses to run as root; use the postgres system user.
  chown postgres "$TMP"
  RUN=(runuser -u postgres --)
fi

cleanup() {
  "${RUN[@]}" "$PG_BIN/pg_ctl" -D "$TMP/data" -m immediate stop >/dev/null 2>&1 || true
  rm -rf "$TMP"
}
trap cleanup EXIT

PORT="$(python3 -I -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1])')"

"${RUN[@]}" "$PG_BIN/initdb" -D "$TMP/data" -U postgres -A trust --no-sync >/dev/null
"${RUN[@]}" "$PG_BIN/pg_ctl" -D "$TMP/data" -l "$TMP/log" -w \
  -o "-k $TMP -p $PORT -c listen_addresses=''" start >/dev/null

psql_run() {
  "${RUN[@]}" "$PG_BIN/psql" -h "$TMP" -p "$PORT" -U postgres -d postgres \
    -X -q -v ON_ERROR_STOP=1 -At "$@"
}

psql_run -f "$HERE/sql/00_supabase_stub.sql" >/dev/null
for f in "$BACKEND"/supabase/migrations/*.sql; do
  psql_run -f "$f" >/dev/null
done

total=0
for f in "$HERE"/sql/[1-9]*.sql; do
  out="$(psql_run -f "$f")"
  n="$(printf '%s\n' "$out" | sed -n 's/^PASSED \([0-9]*\)$/\1/p' | tail -n1)"
  [ -n "$n" ] || { echo "no PASSED line in $(basename "$f")" >&2; exit 1; }
  echo "$(basename "$f"): $n checks passed"
  total=$((total + n))
done
# Concurrency checks with parallel psql sessions.
. "$HERE/race_tests.sh"
echo "TOTAL: $total checks passed"
