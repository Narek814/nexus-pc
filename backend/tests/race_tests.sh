# Concurrency tests, sourced by run_sql_tests.sh (uses psql_run, $TMP, total).
# Each check runs real parallel psql sessions in the background.

race_dir="$TMP/race"
mkdir -p "$race_dir"
race_passed=0

h() { printf "sha256(convert_to('%s','UTF8'))" "$1"; }
# E4-2: pcId = pc_id_from_spki(spki). kx BYTE: fake P-256 SPKI (prefix, 0x04,
# 64 x BYTE); pcid BYTE: its pcId (computed as postgres).
SPKI_PFX=3059301306072a8648ce3d020106082a8648ce3d03010703420004
kx() { printf "decode('%s' || repeat('%s',64),'hex')" "$SPKI_PFX" "$1"; }
kg() { printf "decode('%s' || repeat(to_hex(%s),64),'hex')" "$SPKI_PFX" "$1"; }
pcid() { psql_run -c "select public.pc_id_from_spki($(kx "$1"))"; }

# 1. Four concurrent consumes of one approved code: exactly one 'ok'.
psql_run >/dev/null <<SQL
insert into auth.users values ('00000000-0000-0000-0000-00000000ace1', 'race1@example.test');
select public.pc_login_start('$(pcid a0)', $(kx a0),
  'Race', $(h race-dev), $(h RACE-USER), $(kx a0), uuid_send(gen_random_uuid()), now());
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-00000000ace1', approved_at = now()
 where device_code_hash = $(h race-dev);
SQL
for i in 1 2 3 4; do
  psql_run >"$race_dir/c$i" <<SQL &
begin;
set local role service_role;
select status from public.pc_login_consume($(h race-dev), sha256(uuid_send(gen_random_uuid())), $(kx a0), uuid_send(gen_random_uuid()), now());
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
select public.pc_login_start(public.pc_id_from_spki($(kg '176 + g')),
  $(kg '176 + g'), 'R' || g, sha256(convert_to('lim-d' || g,'UTF8')),
  sha256(convert_to('LIM-U' || g,'UTF8')), $(kg '176 + g'), uuid_send(gen_random_uuid()), now())
  from generate_series(1,25) g;
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-00000000ace2', approved_at = now()
 where device_code_hash in (select sha256(convert_to('lim-d' || g,'UTF8')) from generate_series(1,25) g);
SQL
for g in $(seq 1 25); do
  psql_run >"$race_dir/l$g" <<SQL &
begin;
set local role service_role;
select pg_sleep(0.2) \g /dev/null
select status from public.pc_login_consume($(h "lim-d$g"), sha256(uuid_send(gen_random_uuid())), $(kg $((176 + g))), uuid_send(gen_random_uuid()), now());
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

# 3. Two different approved codes of one new pc_id (same key, E4-2), two
#    consumes each, in parallel: exactly one 'ok', the rest 'code.invalid',
#    no exception (no unhandled unique_violation on my_pcs / pc_keys).
PC3="$(pcid a3)"
psql_run >/dev/null <<SQL
insert into auth.users values ('00000000-0000-0000-0000-00000000ace3', 'race3@example.test'),
  ('00000000-0000-0000-0000-00000000ace4', 'race4@example.test');
select public.pc_login_start('$PC3', $(kx a3),
  'Twin', $(h twin-a), $(h TWIN-A), $(kx a3), uuid_send(gen_random_uuid()), now());
select public.pc_login_start('$PC3', $(kx a3),
  'Twin', $(h twin-b), $(h TWIN-B), $(kx a3), uuid_send(gen_random_uuid()), now());
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-00000000ace3', approved_at = now()
 where device_code_hash = $(h twin-a);
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-00000000ace4', approved_at = now()
 where device_code_hash = $(h twin-b);
SQL
for s in a1:twin-a:a3 a2:twin-a:a3 b1:twin-b:a3 b2:twin-b:a3; do
  IFS=: read -r tag dev key <<<"$s"
  psql_run >"$race_dir/t$tag" 2>&1 <<SQL &
begin;
set local role service_role;
select pg_sleep(0.2) \g /dev/null
select status from public.pc_login_consume($(h "$dev"), sha256(uuid_send(gen_random_uuid())), $(kx "$key"), uuid_send(gen_random_uuid()), now());
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
rows="$(psql_run -c "select count(*) from public.my_pcs where pc_id = '$PC3'")"
[ "$ok" = 1 ] && [ "$rej" = 3 ] && [ "$other" = 0 ] && [ "$err" = 0 ] && [ "$rows" = 1 ] \
  || { echo "FAIL race_two_codes_one_pc ok=$ok rejected=$rej other=$other errors=$err rows=$rows" >&2; cat "$race_dir"/t* >&2; exit 1; }
race_passed=$((race_passed + 1))

# 4. One nonce used by 4 parallel starts: exactly one 'ok', no exception.
PC4="$(pcid a5)"
for i in 1 2 3 4; do
  psql_run >"$race_dir/n$i" 2>&1 <<SQL &
begin;
set local role service_role;
select pg_sleep(0.2) \g /dev/null
select public.pc_login_start('$PC4', $(kx a5),
  'Nonce', $(h "nonce-d$i"), $(h "NONCE-U$i"), $(kx a5),
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
#    code of the same PC (16 PCs, two forced orders, see barrier below). Same lock order in both (pc_lock,
#    then code rows): no deadlock / ERROR; per PC exactly one of
#    {consume ok, release ok} wins and the final state matches the winner.
psql_run >/dev/null <<SQL
insert into auth.users values ('00000000-0000-0000-0000-00000000ace6', 'race6@example.test'),
  ('00000000-0000-0000-0000-00000000ace7', 'race7@example.test');
insert into public.my_pcs (pc_id, user_id, display_name, platform)
  select public.pc_id_from_spki($(kg '208 + g')),
         '00000000-0000-0000-0000-00000000ace6', 'Rel' || g, 'windows' from generate_series(1,16) g;
insert into public.pc_keys (pc_id, public_key_spki)
  select public.pc_id_from_spki($(kg '208 + g')), $(kg '208 + g')
    from generate_series(1,16) g;
-- E4-2: the rebind keeps the PC key (no rotation).
select public.pc_login_start(public.pc_id_from_spki($(kg '208 + g')),
  $(kg '208 + g'), 'Rel' || g, sha256(convert_to('rel-d' || g,'UTF8')),
  sha256(convert_to('REL-U' || g,'UTF8')), $(kg '208 + g'), uuid_send(gen_random_uuid()), now())
  from generate_series(1,16) g;
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-00000000ace7', approved_at = now()
 where device_code_hash in (select sha256(convert_to('rel-d' || g,'UTF8')) from generate_series(1,16) g);
-- Refresh tokens of the old binding (E3-6): must be revoked whoever wins.
insert into public.pc_refresh_tokens (pc_id, family_id, token_hash)
  select public.pc_id_from_spki($(kg '208 + g')), gen_random_uuid(),
         sha256(convert_to('rel-old' || g,'UTF8')) from generate_series(1,16) g;
SQL
PC5=()
for g in $(seq 1 16); do PC5[$g]="$(pcid "$(printf '%x' $((208 + g)))")"; done

# Deterministic barrier (test-only, restored below). A session with
# nexus.barrier = on, right after it takes the real pc_lock, waits on shared
# advisory lock 4242, held exclusively by a controller session. The controller
# releases it only when the expected number of sessions (application_name
# race_w) is waiting on a lock, so the interleaving is forced every run.
pc_lock_orig="$(psql_run -c "select pg_get_functiondef('public.pc_lock(uuid)'::regprocedure)")"
psql_run >/dev/null <<'SQL'
create or replace function public.pc_lock(p_pc_id uuid)
returns void language plpgsql security definer set search_path = '' as $$
begin
  perform pg_catalog.pg_advisory_xact_lock(
    pg_catalog.hashtextextended(p_pc_id::text, 20261124));
  if pg_catalog.current_setting('nexus.barrier', true) = 'on' then
    perform pg_catalog.pg_advisory_xact_lock_shared(4242);
  end if;
end $$;
revoke all on function public.pc_lock(uuid) from public, anon, authenticated, service_role;
SQL

# waiters: number of race_w sessions waiting on a lock.
waiters() {
  psql_run -c "select count(*) from pg_stat_activity where application_name = 'race_w' and wait_event_type = 'Lock'"
}
# wait_waiters N: poll up to 10 s until N race_w sessions wait on a lock.
wait_waiters() {
  local i=0
  while [ "$(waiters)" -lt "$1" ] && [ $i -lt 200 ]; do sleep 0.05; i=$((i + 1)); done
  [ "$(waiters)" -ge "$1" ]
}
# barrier_up: start the controller in the background and wait until it holds
# the barrier. barrier_down N: let it release once N sessions are waiting.
barrier_up() {
  rm -f "$race_dir/go"
  psql_run >"$race_dir/ctl" 2>&1 <<'SQL' &
select pg_advisory_lock(4242) \g /dev/null
create function pg_temp.hold() returns void language plpgsql as $$
declare i int := 0;
begin
  -- Released by barrier_down through the "release" row below.
  loop
    perform pg_stat_clear_snapshot();
    exit when exists (select 1 from pg_stat_activity where application_name = 'race_go');
    exit when i > 400;
    perform pg_sleep(0.05);
    i := i + 1;
  end loop;
end $$;
select pg_temp.hold() \g /dev/null
select pg_advisory_unlock(4242) \g /dev/null
SQL
  ctl_pid=$!
  local i=0
  until [ "$(psql_run -c "select count(*) from pg_locks where locktype = 'advisory' and objid = 4242 and granted")" = 1 ] \
        || [ $i -ge 200 ]; do sleep 0.05; i=$((i + 1)); done
}
barrier_down() {
  local ok=0
  wait_waiters "$1" && ok=1
  # A short session named race_go tells the controller to release.
  psql_run -c "set application_name = 'race_go'; select pg_sleep(0.5)" >/dev/null &
  wait "$ctl_pid"
  [ $ok = 1 ]
}

rel_consume() {  # $1 = g, $2 = barrier on/off
  psql_run >"$race_dir/rc$1" 2>&1 <<SQL &
set application_name = 'race_w';
begin;
set local nexus.barrier = '$2';
set local role service_role;
select 'consume:' || status from public.pc_login_consume($(h "rel-d$1"), $(h "rel-new$1"), $(kg $((208 + $1))), uuid_send(gen_random_uuid()), now());
commit;
SQL
}
rel_release() {  # $1 = g, $2 = barrier on/off
  psql_run >"$race_dir/rr$1" 2>&1 <<SQL &
set application_name = 'race_w';
begin;
set local nexus.barrier = '$2';
set local role authenticated;
select set_config('request.jwt.claims', '{"sub":"00000000-0000-0000-0000-00000000ace6","role":"authenticated"}', true) \g /dev/null
select 'release:' || public.pc_release('${PC5[$1]}');
commit;
SQL
}
# Phase A (PCs 1..8): consume holds pc_lock and stops at the barrier, then
# release starts and must wait. Phase B (PCs 9..16): the other way round.
# A consume that locked the code row before pc_lock deadlocks in phase B
# every run (release updates the code rows after the barrier).
fails=0
barrier_up
for g in 1 2 3 4 5 6 7 8; do rel_consume $g on; done
wait_waiters 8 || fails=$((fails + 1))
for g in 1 2 3 4 5 6 7 8; do rel_release $g off; done
barrier_down 16 || fails=$((fails + 1))
wait
barrier_up
for g in 9 10 11 12 13 14 15 16; do rel_release $g on; done
wait_waiters 8 || fails=$((fails + 1))
for g in 9 10 11 12 13 14 15 16; do rel_consume $g off; done
barrier_down 16 || fails=$((fails + 1))
wait
err="$(cat "$race_dir"/rc* "$race_dir"/rr* | grep -c 'ERROR' || true)"
bad=0
for g in $(seq 1 16); do
  pc="${PC5[$g]}"
  c="$(cat "$race_dir/rc$g")"; r="$(cat "$race_dir/rr$g")"
  owner="$(psql_run -c "select coalesce((select user_id::text from public.my_pcs where pc_id = '$pc'), 'none')")"
  # live refresh tokens of this PC: the new one only (consume won) or none.
  live="$(psql_run -c "select coalesce(string_agg(case when token_hash = sha256(convert_to('rel-new$g','UTF8')) then 'new' else 'old' end, ','), 'none') from public.pc_refresh_tokens where pc_id = '$pc' and revoked_at is null")"
  if [ $g -le 8 ] && [ "$c" = "consume:ok" ] && [ "$r" = "release:pc.not_found" ] \
     && [ "$owner" = "00000000-0000-0000-0000-00000000ace7" ] && [ "$live" = "new" ]; then :
  elif [ $g -gt 8 ] && [ "$c" = "consume:code.invalid" ] && [ "$r" = "release:ok" ] \
     && [ "$owner" = "none" ] && [ "$live" = "none" ]; then :
  else bad=$((bad + 1)); echo "pc $g: $c / $r / $owner / $live" >&2; fi
done
[ "$err" = 0 ] && [ "$bad" = 0 ] && [ "$fails" = 0 ] \
  || { echo "FAIL race_release_vs_consume errors=$err bad=$bad barrier_fails=$fails" >&2; cat "$race_dir"/rc* "$race_dir"/rr* >&2; exit 1; }
race_passed=$((race_passed + 1))

# 6. Four sessions rotate one refresh token at once (E3-6). Session 1 takes
#    pc_lock and stops at the barrier; sessions 2..4 must wait for it.
#    Exactly one 'ok' (session 1); the next one sees a used token
#    ('reuse.detected', family revoked), the rest 'revoked'. No ERROR.
psql_run >/dev/null <<SQL
insert into auth.users values ('00000000-0000-0000-0000-00000000ace8', 'race8@example.test');
insert into public.my_pcs (pc_id, user_id, display_name, platform)
  values ('30000000-0000-0000-0000-00000000ace8', '00000000-0000-0000-0000-00000000ace8', 'Rot', 'windows');
insert into public.pc_keys values ('30000000-0000-0000-0000-00000000ace8', decode(repeat('38',91),'hex'));
insert into public.pc_refresh_tokens (pc_id, family_id, token_hash)
  values ('30000000-0000-0000-0000-00000000ace8', gen_random_uuid(), $(h rot-a));
SQL
rot() {  # $1 = i, $2 = barrier on/off
  psql_run >"$race_dir/ro$1" 2>&1 <<SQL &
set application_name = 'race_w';
begin;
set local nexus.barrier = '$2';
set local role service_role;
select status from public.pc_rotate_refresh($(h rot-a), $(h "rot-b$1"), '30000000-0000-0000-0000-00000000ace8',
  decode(repeat('38',91),'hex'), uuid_send(gen_random_uuid()), now());
commit;
SQL
}
fails=0
barrier_up
rot 1 on
wait_waiters 1 || fails=$((fails + 1))
for i in 2 3 4; do rot $i off; done
barrier_down 4 || fails=$((fails + 1))
wait
first="$(cat "$race_dir/ro1")"
ok="$(cat "$race_dir"/ro? | grep -c '^ok$' || true)"
reuse="$(cat "$race_dir"/ro? | grep -c '^reuse.detected$' || true)"
rev="$(cat "$race_dir"/ro? | grep -c '^revoked$' || true)"
err="$(cat "$race_dir"/ro? | grep -c 'ERROR' || true)"
live="$(psql_run -c "select count(*) from public.pc_refresh_tokens where pc_id = '30000000-0000-0000-0000-00000000ace8' and revoked_at is null")"
[ "$first" = ok ] && [ "$ok" = 1 ] && [ "$reuse" = 1 ] && [ "$rev" = 2 ] && [ "$err" = 0 ] \
  && [ "$live" = 0 ] && [ "$fails" = 0 ] \
  || { echo "FAIL race_rotate_once first=$first ok=$ok reuse=$reuse revoked=$rev errors=$err live=$live barrier_fails=$fails" >&2; cat "$race_dir"/ro? >&2; exit 1; }
race_passed=$((race_passed + 1))

# 7. Retry of an old token after a lost response (R-1) races the rotation of
#    its new token. Two forced orders (two PCs): the one at the barrier goes
#    first. Retry first: retry 'ok', then rotation 'ok'. Rotation first:
#    rotation 'ok', then the retry is 'reuse.detected' and the family dies.
psql_run >/dev/null <<SQL
insert into auth.users values ('00000000-0000-0000-0000-00000000ace9', 'race9@example.test');
insert into public.my_pcs (pc_id, user_id, display_name, platform) values
  ('30000000-0000-0000-0000-0000000ace91', '00000000-0000-0000-0000-00000000ace9', 'Rt1', 'windows'),
  ('30000000-0000-0000-0000-0000000ace92', '00000000-0000-0000-0000-00000000ace9', 'Rt2', 'windows');
insert into public.pc_keys values
  ('30000000-0000-0000-0000-0000000ace91', decode(repeat('39',91),'hex')),
  ('30000000-0000-0000-0000-0000000ace92', decode(repeat('39',91),'hex'));
insert into public.pc_refresh_tokens (pc_id, family_id, token_hash)
  values ('30000000-0000-0000-0000-0000000ace91', gen_random_uuid(), $(h rr1-0)),
         ('30000000-0000-0000-0000-0000000ace92', gen_random_uuid(), $(h rr2-0));
set role service_role;
select status from public.pc_rotate_refresh($(h rr1-0), $(h rr1-1), '30000000-0000-0000-0000-0000000ace91',
  decode(repeat('39',91),'hex'), uuid_send(gen_random_uuid()), now());
select status from public.pc_rotate_refresh($(h rr2-0), $(h rr2-1), '30000000-0000-0000-0000-0000000ace92',
  decode(repeat('39',91),'hex'), uuid_send(gen_random_uuid()), now());
SQL
rr() {  # $1 = out name, $2 = pc n, $3 = old, $4 = new, $5 = barrier on/off
  psql_run >"$race_dir/$1" 2>&1 <<SQL &
set application_name = 'race_w';
begin;
set local nexus.barrier = '$5';
set local role service_role;
select status from public.pc_rotate_refresh($(h "$3"), $(h "$4"), '30000000-0000-0000-0000-0000000ace9$2',
  decode(repeat('39',91),'hex'), uuid_send(gen_random_uuid()), now());
commit;
SQL
}
fails=0
barrier_up
rr rt1 1 rr1-0 rr1-1 on
wait_waiters 1 || fails=$((fails + 1))
rr rn1 1 rr1-1 rr1-2 off
barrier_down 2 || fails=$((fails + 1))
wait
barrier_up
rr rn2 2 rr2-1 rr2-2 on
wait_waiters 1 || fails=$((fails + 1))
rr rt2 2 rr2-0 rr2-1 off
barrier_down 2 || fails=$((fails + 1))
wait
o="$(cat "$race_dir/rt1") $(cat "$race_dir/rn1") $(cat "$race_dir/rn2") $(cat "$race_dir/rt2")"
live1="$(psql_run -c "select count(*) from public.pc_refresh_tokens where pc_id = '30000000-0000-0000-0000-0000000ace91' and revoked_at is null and used_at is null")"
live2="$(psql_run -c "select count(*) from public.pc_refresh_tokens where pc_id = '30000000-0000-0000-0000-0000000ace92' and revoked_at is null")"
[ "$o" = "ok ok ok reuse.detected" ] && [ "$live1" = 1 ] && [ "$live2" = 0 ] && [ "$fails" = 0 ] \
  || { echo "FAIL race_retry_vs_rotate got='$o' live1=$live1 live2=$live2 barrier_fails=$fails" >&2; exit 1; }
race_passed=$((race_passed + 1))

# Restore the real pc_lock from its saved definition and check it.
printf '%s;\n' "$pc_lock_orig" | psql_run >/dev/null
psql_run >/dev/null <<'SQL'
revoke all on function public.pc_lock(uuid) from public, anon, authenticated, service_role;
SQL
[ "$(psql_run -c "select pg_get_functiondef('public.pc_lock(uuid)'::regprocedure)")" = "$pc_lock_orig" ] \
  || { echo "FAIL race_pc_lock_restored" >&2; exit 1; }
race_passed=$((race_passed + 1))

echo "race_tests: $race_passed checks passed"
total=$((total + race_passed))
