# Concurrency tests, sourced by run_sql_tests.sh (uses psql_run, $TMP, total).
# Each check runs real parallel psql sessions in the background.

race_dir="$TMP/race"
mkdir -p "$race_dir"
race_passed=0

h() { printf "sha256(convert_to('%s','UTF8'))" "$1"; }

# 1. Four concurrent consumes of one approved code: exactly one 'ok'.
psql_run >/dev/null <<SQL
insert into auth.users values ('00000000-0000-0000-0000-00000000ace1', 'race1@example.test');
select public.pc_login_start('30000000-0000-0000-0000-00000000ace1', decode(repeat('30',91),'hex'),
  'Race', $(h race-dev), $(h RACE-USER));
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-00000000ace1', approved_at = now()
 where device_code_hash = $(h race-dev);
SQL
for i in 1 2 3 4; do
  psql_run >"$race_dir/c$i" <<SQL &
begin;
set local role service_role;
select status from public.pc_login_consume($(h race-dev));
select pg_sleep(0.5) \g /dev/null
commit;
SQL
done
wait
ok="$(cat "$race_dir"/c? | grep -c '^ok$' || true)"
inv="$(cat "$race_dir"/c? | grep -c '^code.invalid$' || true)"
[ "$ok" = 1 ] && [ "$inv" = 3 ] || { echo "FAIL race_double_consume ok=$ok invalid=$inv" >&2; exit 1; }
race_passed=$((race_passed + 1))

# 2. 25 concurrent consumes for one fresh account: exactly 20 PCs bound.
psql_run >/dev/null <<SQL
insert into auth.users values ('00000000-0000-0000-0000-00000000ace2', 'race2@example.test');
select public.pc_login_start(('30000000-0000-0000-0000-0000000f' || lpad(g::text,4,'0'))::uuid,
  decode(repeat('30',91),'hex'), 'R' || g, sha256(convert_to('lim-d' || g,'UTF8')),
  sha256(convert_to('LIM-U' || g,'UTF8'))) from generate_series(1,25) g;
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-00000000ace2', approved_at = now()
 where pc_id::text like '30000000-0000-0000-0000-0000000f%';
SQL
for g in $(seq 1 25); do
  psql_run >"$race_dir/l$g" <<SQL &
begin;
set local role service_role;
select pg_sleep(0.2) \g /dev/null
select status from public.pc_login_consume($(h "lim-d$g"));
select pg_sleep(0.3) \g /dev/null
commit;
SQL
done
wait
ok="$(cat "$race_dir"/l* | grep -c '^ok$' || true)"
lim="$(cat "$race_dir"/l* | grep -c '^pcs.limit$' || true)"
rows="$(psql_run -c "select count(*) from public.my_pcs where user_id = '00000000-0000-0000-0000-00000000ace2'")"
[ "$ok" = 20 ] && [ "$lim" = 5 ] && [ "$rows" = 20 ] \
  || { echo "FAIL race_pc_limit ok=$ok limit=$lim rows=$rows" >&2; exit 1; }
race_passed=$((race_passed + 1))

echo "race_tests: $race_passed checks passed"
total=$((total + race_passed))
