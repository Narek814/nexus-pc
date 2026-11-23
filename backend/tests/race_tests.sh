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
  'Race', $(h race-dev), $(h RACE-USER), decode(repeat('30',91),'hex'), uuid_send(gen_random_uuid()), now());
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-00000000ace1', approved_at = now()
 where device_code_hash = $(h race-dev);
SQL
for i in 1 2 3 4; do
  psql_run >"$race_dir/c$i" <<SQL &
begin;
set local role service_role;
select status from public.pc_login_consume($(h race-dev), decode(repeat('30',91),'hex'), uuid_send(gen_random_uuid()), now());
select pg_sleep(0.5) \g /dev/null
commit;
SQL
done
wait
ok="$(cat "$race_dir"/c? | grep -c '^ok$' || true)"
# Losers may legitimately get 'code.invalid', 'slow_down' (same code polled
# again within 5 s) or 'pending'; anything else (incl. ERROR) is a failure.
rej="$(cat "$race_dir"/c? | grep -cE '^(code\.invalid|slow_down|pending)$' || true)"
other="$(cat "$race_dir"/c? | grep -vcE '^(ok|code\.invalid|slow_down|pending)$' || true)"
[ "$ok" = 1 ] && [ "$rej" = 3 ] && [ "$other" = 0 ] \
  || { echo "FAIL race_double_consume ok=$ok rejected=$rej other=$other" >&2; cat "$race_dir"/c? >&2; exit 1; }
race_passed=$((race_passed + 1))

# 2. 25 concurrent consumes for one fresh account: exactly 20 PCs bound.
psql_run >/dev/null <<SQL
insert into auth.users values ('00000000-0000-0000-0000-00000000ace2', 'race2@example.test');
select public.pc_login_start(('30000000-0000-0000-0000-0000000f' || lpad(g::text,4,'0'))::uuid,
  decode(repeat('30',91),'hex'), 'R' || g, sha256(convert_to('lim-d' || g,'UTF8')),
  sha256(convert_to('LIM-U' || g,'UTF8')), decode(repeat('30',91),'hex'), uuid_send(gen_random_uuid()), now())
  from generate_series(1,25) g;
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-00000000ace2', approved_at = now()
 where pc_id::text like '30000000-0000-0000-0000-0000000f%';
SQL
for g in $(seq 1 25); do
  psql_run >"$race_dir/l$g" <<SQL &
begin;
set local role service_role;
select pg_sleep(0.2) \g /dev/null
select status from public.pc_login_consume($(h "lim-d$g"), decode(repeat('30',91),'hex'), uuid_send(gen_random_uuid()), now());
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

# 3. Two different approved codes of one new pc_id, two consumes each, in
#    parallel: exactly one 'ok', the rest 'code.invalid', no exception
#    (no unhandled unique_violation on my_pcs / pc_keys).
psql_run >/dev/null <<SQL
insert into auth.users values ('00000000-0000-0000-0000-00000000ace3', 'race3@example.test'),
  ('00000000-0000-0000-0000-00000000ace4', 'race4@example.test');
select public.pc_login_start('30000000-0000-0000-0000-00000000ace3', decode(repeat('34',91),'hex'),
  'Twin', $(h twin-a), $(h TWIN-A), decode(repeat('34',91),'hex'), uuid_send(gen_random_uuid()), now());
select public.pc_login_start('30000000-0000-0000-0000-00000000ace3', decode(repeat('35',91),'hex'),
  'Twin', $(h twin-b), $(h TWIN-B), decode(repeat('35',91),'hex'), uuid_send(gen_random_uuid()), now());
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-00000000ace3', approved_at = now()
 where device_code_hash = $(h twin-a);
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-00000000ace4', approved_at = now()
 where device_code_hash = $(h twin-b);
SQL
for s in a1:twin-a:34 a2:twin-a:34 b1:twin-b:35 b2:twin-b:35; do
  IFS=: read -r tag dev key <<<"$s"
  psql_run >"$race_dir/t$tag" 2>&1 <<SQL &
begin;
set local role service_role;
select pg_sleep(0.2) \g /dev/null
select status from public.pc_login_consume($(h "$dev"), decode(repeat('$key',91),'hex'), uuid_send(gen_random_uuid()), now());
select pg_sleep(0.3) \g /dev/null
commit;
SQL
done
wait
ok="$(cat "$race_dir"/t* | grep -c '^ok$' || true)"
# Two consumes share each code, so 'slow_down' is a legitimate loser answer.
rej="$(cat "$race_dir"/t* | grep -cE '^(code\.invalid|slow_down|pending)$' || true)"
other="$(cat "$race_dir"/t* | grep -vcE '^(ok|code\.invalid|slow_down|pending)$' || true)"
err="$(cat "$race_dir"/t* | grep -c 'ERROR' || true)"
rows="$(psql_run -c "select count(*) from public.my_pcs where pc_id = '30000000-0000-0000-0000-00000000ace3'")"
[ "$ok" = 1 ] && [ "$rej" = 3 ] && [ "$other" = 0 ] && [ "$err" = 0 ] && [ "$rows" = 1 ] \
  || { echo "FAIL race_two_codes_one_pc ok=$ok rejected=$rej other=$other errors=$err rows=$rows" >&2; cat "$race_dir"/t* >&2; exit 1; }
race_passed=$((race_passed + 1))

# 4. One nonce used by 4 parallel starts: exactly one 'ok', no exception.
for i in 1 2 3 4; do
  psql_run >"$race_dir/n$i" 2>&1 <<SQL &
begin;
set local role service_role;
select pg_sleep(0.2) \g /dev/null
select public.pc_login_start('30000000-0000-0000-0000-00000000ace5', decode(repeat('30',91),'hex'),
  'Nonce', $(h "nonce-d$i"), $(h "NONCE-U$i"), decode(repeat('30',91),'hex'),
  '\x00112233445566778899aabbccddeeff', now());
select pg_sleep(0.3) \g /dev/null
commit;
SQL
done
wait
ok="$(cat "$race_dir"/n? | grep -c '^ok$' || true)"
rep="$(cat "$race_dir"/n? | grep -c '^nonce.replay$' || true)"
err="$(cat "$race_dir"/n? | grep -c 'ERROR' || true)"
[ "$ok" = 1 ] && [ "$rep" = 3 ] && [ "$err" = 0 ] \
  || { echo "FAIL race_nonce_once ok=$ok replay=$rep errors=$err" >&2; exit 1; }
race_passed=$((race_passed + 1))

# 5. pc_release by the owner in parallel with consume of an approved rebind
#    code of the same PC (16 PCs at once). Same lock order in both (pc_lock,
#    then code rows): no deadlock / ERROR; per PC exactly one of
#    {consume ok, release ok} wins and the final state matches the winner.
psql_run >/dev/null <<SQL
insert into auth.users values ('00000000-0000-0000-0000-00000000ace6', 'race6@example.test'),
  ('00000000-0000-0000-0000-00000000ace7', 'race7@example.test');
insert into public.my_pcs (pc_id, user_id, display_name, platform)
  select ('30000000-0000-0000-0000-0000000d' || lpad(g::text,4,'0'))::uuid,
         '00000000-0000-0000-0000-00000000ace6', 'Rel' || g, 'windows' from generate_series(1,16) g;
insert into public.pc_keys (pc_id, public_key_spki)
  select ('30000000-0000-0000-0000-0000000d' || lpad(g::text,4,'0'))::uuid, decode(repeat('36',91),'hex')
    from generate_series(1,16) g;
select public.pc_login_start(('30000000-0000-0000-0000-0000000d' || lpad(g::text,4,'0'))::uuid,
  decode(repeat('37',91),'hex'), 'Rel' || g, sha256(convert_to('rel-d' || g,'UTF8')),
  sha256(convert_to('REL-U' || g,'UTF8')), decode(repeat('36',91),'hex'), uuid_send(gen_random_uuid()), now())
  from generate_series(1,16) g;
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-00000000ace7', approved_at = now()
 where pc_id::text like '30000000-0000-0000-0000-0000000d%';
SQL
for g in $(seq 1 16); do
  pc="30000000-0000-0000-0000-0000000d$(printf '%04d' "$g")"
  psql_run >"$race_dir/rc$g" 2>&1 <<SQL &
begin;
set local role service_role;
select pg_sleep(0.2) \g /dev/null
select 'consume:' || status from public.pc_login_consume($(h "rel-d$g"), decode(repeat('37',91),'hex'), uuid_send(gen_random_uuid()), now());
select pg_sleep(0.3) \g /dev/null
commit;
SQL
  psql_run >"$race_dir/rr$g" 2>&1 <<SQL &
begin;
set local role authenticated;
select set_config('request.jwt.claims', '{"sub":"00000000-0000-0000-0000-00000000ace6","role":"authenticated"}', true) \g /dev/null
select pg_sleep(0.2) \g /dev/null
select 'release:' || public.pc_release('$pc');
select pg_sleep(0.3) \g /dev/null
commit;
SQL
done
wait
err="$(cat "$race_dir"/rc* "$race_dir"/rr* | grep -c 'ERROR' || true)"
bad=0
for g in $(seq 1 16); do
  pc="30000000-0000-0000-0000-0000000d$(printf '%04d' "$g")"
  c="$(cat "$race_dir/rc$g")"; r="$(cat "$race_dir/rr$g")"
  owner="$(psql_run -c "select coalesce((select user_id::text from public.my_pcs where pc_id = '$pc'), 'none')")"
  if [ "$c" = "consume:ok" ] && [ "$r" = "release:pc.not_found" ] \
     && [ "$owner" = "00000000-0000-0000-0000-00000000ace7" ]; then :
  elif [ "$c" = "consume:code.invalid" ] && [ "$r" = "release:ok" ] && [ "$owner" = "none" ]; then :
  else bad=$((bad + 1)); echo "pc $g: $c / $r / $owner" >&2; fi
done
[ "$err" = 0 ] && [ "$bad" = 0 ] \
  || { echo "FAIL race_release_vs_consume errors=$err bad=$bad" >&2; cat "$race_dir"/rc* "$race_dir"/rr* >&2; exit 1; }
race_passed=$((race_passed + 1))

echo "race_tests: $race_passed checks passed"
total=$((total + race_passed))
