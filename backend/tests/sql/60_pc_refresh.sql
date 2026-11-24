-- Tests for PC refresh tokens: rotation, reuse detection, revocation (E3-6).
\set ON_ERROR_STOP on

insert into auth.users (id, email) values
  ('00000000-0000-0000-0000-0000000006a1', 'ra@example.test'),
  ('00000000-0000-0000-0000-0000000006b1', 'rb@example.test');

create temp table _results (name text);
grant insert on _results to authenticated, anon, service_role;

create function pg_temp.h(t text) returns bytea language sql immutable
  as $$ select sha256(convert_to(t, 'UTF8')) $$;
create function pg_temp.k(n int) returns bytea language sql immutable
  as $$ select decode('3059301306072a8648ce3d020106082a8648ce3d03010703420004'
          || repeat(to_hex(48 + n), 64), 'hex') $$;
-- pcId derived from key n (E4-2, docs/API.md «Идентификатор ПК»).
create function pg_temp.pc(n int) returns uuid language sql immutable security definer
  as $$ select public.pc_id_from_spki(pg_temp.k(n)) $$;
create function pg_temp.nn() returns bytea language sql volatile
  as $$ select uuid_send(gen_random_uuid()) $$;
create function pg_temp.as_user(u text) returns void language sql
  as $$ select set_config('request.jwt.claims',
          json_build_object('sub', u, 'role', 'authenticated')::text, false) $$;
-- Signed rotation with a fresh nonce and ts = now().
create function pg_temp.rot(pc uuid, old text, new text, signer bytea) returns text
  language sql volatile
  as $$ select status from public.pc_rotate_refresh(pg_temp.h(old), pg_temp.h(new), pc,
          signer, pg_temp.nn(), now()) $$;
-- Full sign-in of a PC by a user: start, describe, approve, consume (refresh r0).
-- Runs as postgres and switches roles itself.
create function pg_temp.bind(pc uuid, key bytea, signer bytea, usr uuid, tag text, r0 text)
  returns text language plpgsql volatile as $$
declare s text;
begin
  set local role service_role;
  s := public.pc_login_start(pc, key, 'PC ' || tag, pg_temp.h('d-' || tag), pg_temp.h('U-' || tag),
         signer, pg_temp.nn(), now());
  if s <> 'ok' then return 'start:' || s; end if;
  perform set_config('request.jwt.claims',
    json_build_object('sub', usr, 'role', 'authenticated')::text, true);
  set local role authenticated;
  perform public.pc_login_describe(pg_temp.h('U-' || tag));
  s := public.pc_login_approve(pg_temp.h('U-' || tag));
  if s <> 'ok' then return 'approve:' || s; end if;
  set local role service_role;
  select c.status into s from public.pc_login_consume(pg_temp.h('d-' || tag), pg_temp.h(r0), key,
    pg_temp.nn(), now()) c;
  reset role;
  return s;
end $$;
grant execute on all functions in schema pg_temp to authenticated, anon, service_role;

-- Catalog: table is service_role only, RLS forced, internal functions closed.
do $$ declare r text; f text; begin
  foreach r in array array['anon','authenticated'] loop
    if has_table_privilege(r, 'public.pc_refresh_tokens', 'select,insert,update,delete') then
      raise exception 'FAIL refresh_table_no_api_access %', r;
    end if;
  end loop;
  if not has_table_privilege('service_role', 'public.pc_refresh_tokens', 'select') then
    raise exception 'FAIL refresh_table_service_role';
  end if;
  if not (select relrowsecurity and relforcerowsecurity from pg_class
           where oid = 'public.pc_refresh_tokens'::regclass) then
    raise exception 'FAIL refresh_rls_forced';
  end if;
  foreach f in array array['public.pc_revoke_all(uuid)', 'public.pc_issue_refresh(uuid,bytea)'] loop
    foreach r in array array['anon','authenticated','service_role'] loop
      if has_function_privilege(r, f, 'execute') then
        raise exception 'FAIL refresh_internal_functions % %', r, f;
      end if;
    end loop;
  end loop;
  f := 'public.pc_rotate_refresh(bytea,bytea,uuid,bytea,bytea,timestamptz)';
  if has_function_privilege('anon', f, 'execute') or has_function_privilege('authenticated', f, 'execute')
     or not has_function_privilege('service_role', f, 'execute') then
    raise exception 'FAIL rotate_service_only';
  end if;
  if (select count(*) from pg_proc p join pg_namespace s on s.oid = p.pronamespace
       where s.nspname = 'public' and p.proname in ('pc_revoke_all','pc_issue_refresh','pc_rotate_refresh')
         and p.prosecdef and p.proconfig @> array['search_path=""']) <> 3 then
    raise exception 'FAIL refresh_definer_empty_search_path';
  end if;
  -- The E3-5 hook is no longer a stub.
  if (select prosrc from pg_proc where proname = 'pc_revoke_refresh_tokens') !~ 'pc_revoke_all' then
    raise exception 'FAIL revoke_hook_implemented';
  end if;
  insert into _results values ('refresh_table_no_api_access'), ('refresh_table_service_role'),
    ('refresh_rls_forced'), ('refresh_internal_functions'), ('rotate_service_only'),
    ('refresh_definer_empty_search_path'), ('revoke_hook_implemented');
end $$;

-- First refresh comes with consume 'ok', exactly once.
do $$ declare s text; n int; begin
  s := pg_temp.bind(pg_temp.pc(31), pg_temp.k(31), pg_temp.k(31),
    '00000000-0000-0000-0000-0000000006a1', 'a', 'r-a');
  if s <> 'ok' then raise exception 'FAIL consume_issues_refresh (%)', s; end if;
  select count(*) into n from public.pc_refresh_tokens
   where pc_id = pg_temp.pc(31);
  if n <> 1 or not exists (select 1 from public.pc_refresh_tokens
      where token_hash = pg_temp.h('r-a') and used_at is null and revoked_at is null
        and expires_at between now() + interval '90 days' - interval '1 minute'
                           and now() + interval '90 days' + interval '1 minute') then
    raise exception 'FAIL consume_issues_refresh (n=%)', n;
  end if;
  insert into _results values ('consume_issues_refresh'), ('refresh_expires_90_days');
end $$;

-- Polls that do not return 'ok' store nothing; a second consume of the same
-- code stores nothing either.
set role service_role;
do $$ declare s text; begin
  s := public.pc_login_start(pg_temp.pc(32), pg_temp.k(32), 'P',
    pg_temp.h('d-pend'), pg_temp.h('U-pend'), pg_temp.k(32), pg_temp.nn(), now());
  select status into s from public.pc_login_consume(pg_temp.h('d-pend'), pg_temp.h('r-pend'),
    pg_temp.k(32), pg_temp.nn(), now());
  if s <> 'pending' or exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('r-pend')) then
    raise exception 'FAIL pending_poll_no_refresh (%)', s;
  end if;
  select status into s from public.pc_login_consume(pg_temp.h('d-a'), pg_temp.h('r-a2'),
    pg_temp.k(31), pg_temp.nn(), now());
  if s <> 'code.invalid' or exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('r-a2')) then
    raise exception 'FAIL second_consume_no_refresh (%)', s;
  end if;
  begin
    perform public.pc_login_consume(pg_temp.h('d-pend'), '\x00'::bytea, pg_temp.k(32), pg_temp.nn(), now());
    raise exception 'FAIL consume_bad_refresh_hash';
  exception when invalid_parameter_value then null;
  end;
  insert into _results values ('pending_poll_no_refresh'), ('second_consume_no_refresh'),
    ('consume_bad_refresh_hash');
end $$;
reset role;

-- authenticated cannot read refresh tokens, not even of its own PC.
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000006a1') \g /dev/null
do $$ begin
  begin
    perform 1 from public.pc_refresh_tokens;
    raise exception 'FAIL authenticated_cannot_read_refresh';
  exception when insufficient_privilege then null;
  end;
  begin
    perform public.pc_rotate_refresh(pg_temp.h('r-a'), pg_temp.h('x'),
      pg_temp.pc(31), pg_temp.k(31), pg_temp.nn(), now());
    raise exception 'FAIL authenticated_cannot_rotate';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('authenticated_cannot_read_refresh'), ('authenticated_cannot_rotate');
end $$;
reset role;
set role anon;
do $$ begin
  begin
    perform 1 from public.pc_refresh_tokens;
    raise exception 'FAIL anon_cannot_read_refresh';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('anon_cannot_read_refresh');
end $$;
reset role;

-- Normal rotation a -> b -> c, then reuse of a.
set role service_role;
do $$ declare r record; fam int; begin
  select * into r from public.pc_rotate_refresh(pg_temp.h('r-a'), pg_temp.h('r-b'),
    pg_temp.pc(31), pg_temp.k(31), pg_temp.nn(), now());
  if r.status <> 'ok' or r.user_id <> '00000000-0000-0000-0000-0000000006a1' then
    raise exception 'FAIL rotate_a_b (%)', r;
  end if;
  if pg_temp.rot(pg_temp.pc(31), 'r-b', 'r-c', pg_temp.k(31)) <> 'ok' then
    raise exception 'FAIL rotate_b_c';
  end if;
  select count(distinct family_id) into fam from public.pc_refresh_tokens
   where token_hash in (pg_temp.h('r-a'), pg_temp.h('r-b'), pg_temp.h('r-c'));
  if fam <> 1 or (select count(*) from public.pc_refresh_tokens
                   where token_hash in (pg_temp.h('r-a'), pg_temp.h('r-b')) and used_at is not null) <> 2
     or not exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('r-c')
                     and used_at is null and expires_at > now() + interval '89 days') then
    raise exception 'FAIL rotation_same_family_sliding';
  end if;
  insert into _results values ('rotate_a_b'), ('rotate_returns_owner'), ('rotate_b_c'),
    ('rotation_same_family_sliding');

  if pg_temp.rot(pg_temp.pc(31), 'r-a', 'r-x', pg_temp.k(31)) <> 'reuse.detected' then
    raise exception 'FAIL reuse_detected';
  end if;
  if exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('r-x')) then
    raise exception 'FAIL reuse_issues_nothing';
  end if;
  if pg_temp.rot(pg_temp.pc(31), 'r-b', 'r-y', pg_temp.k(31)) <> 'revoked'
     or pg_temp.rot(pg_temp.pc(31), 'r-c', 'r-z', pg_temp.k(31)) <> 'revoked' then
    raise exception 'FAIL family_revoked_after_reuse';
  end if;
  if exists (select 1 from public.pc_refresh_tokens
              where pc_id = pg_temp.pc(31) and revoked_at is null) then
    raise exception 'FAIL family_revoked_rows';
  end if;
  insert into _results values ('reuse_detected'), ('reuse_issues_nothing'),
    ('family_revoked_after_reuse'), ('family_revoked_rows');
end $$;
reset role;

-- Second PC of user A with a fresh family, for the request checks.
insert into public.my_pcs (pc_id, user_id, display_name, platform)
  values (pg_temp.pc(33), '00000000-0000-0000-0000-0000000006a1', 'Two', 'windows');
insert into public.pc_keys values (pg_temp.pc(33), pg_temp.k(33));
insert into public.pc_refresh_tokens (pc_id, family_id, token_hash)
  values (pg_temp.pc(33), gen_random_uuid(), pg_temp.h('t-1'));

set role service_role;
do $$ declare n bytea := pg_temp.nn(); s text; cnt bigint; begin
  -- Same nonce twice: second refused, token not spent by it.
  select status into s from public.pc_rotate_refresh(pg_temp.h('t-1'), pg_temp.h('t-2'),
    pg_temp.pc(33), pg_temp.k(33), n, now());
  if s <> 'ok' then raise exception 'FAIL rotate_with_nonce_ok (%)', s; end if;
  select status into s from public.pc_rotate_refresh(pg_temp.h('t-2'), pg_temp.h('t-3'),
    pg_temp.pc(33), pg_temp.k(33), n, now());
  if s <> 'nonce.replay' then raise exception 'FAIL rotate_nonce_replay (%)', s; end if;
  -- ts window +-300 s.
  select status into s from public.pc_rotate_refresh(pg_temp.h('t-2'), pg_temp.h('t-3'),
    pg_temp.pc(33), pg_temp.k(33), pg_temp.nn(), now() - interval '301 seconds');
  if s <> 'request.stale' then raise exception 'FAIL rotate_ts_old_stale (%)', s; end if;
  select status into s from public.pc_rotate_refresh(pg_temp.h('t-2'), pg_temp.h('t-3'),
    pg_temp.pc(33), pg_temp.k(33), pg_temp.nn(), now() + interval '301 seconds');
  if s <> 'request.stale' then raise exception 'FAIL rotate_ts_future_stale (%)', s; end if;
  select status into s from public.pc_rotate_refresh(pg_temp.h('t-2'), pg_temp.h('t-3'),
    pg_temp.pc(33), pg_temp.k(33), '\x0011'::bytea, now());
  if s <> 'request.stale' then raise exception 'FAIL rotate_short_nonce (%)', s; end if;
  if exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('t-3'))
     or exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('t-2') and used_at is not null) then
    raise exception 'FAIL refused_requests_spend_nothing';
  end if;
  -- Signer must be the PC key.
  -- Wrong key for a bound pc_id: 'revoked', the same as for an unbound one (R-4).
  select coalesce(sum(count), 0) into cnt from public.rate_limits
   where bucket = 'pc_refresh' and key = pg_temp.pc(33)::text;
  if pg_temp.rot(pg_temp.pc(33), 't-2', 't-3', pg_temp.k(34)) <> 'revoked'
     or pg_temp.rot(pg_temp.pc(33), 't-2', 't-3', null) <> 'revoked'
     or pg_temp.rot('20000000-0000-0000-0000-0000000006af', 't-2', 't-3', pg_temp.k(34)) <> 'revoked' then
    raise exception 'FAIL rotate_needs_pc_key';
  end if;
  if exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('t-3'))
     or exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('t-2') and used_at is not null)
     or (select coalesce(sum(count), 0) from public.rate_limits
          where bucket = 'pc_refresh' and key = pg_temp.pc(33)::text) <> cnt then
    raise exception 'FAIL wrong_key_spends_nothing';
  end if;
  insert into _results values ('wrong_key_spends_nothing');
  -- Unknown token, token of another PC: the same 'revoked'.
  if pg_temp.rot(pg_temp.pc(33), 'nope', 't-3', pg_temp.k(33)) <> 'revoked' then
    raise exception 'FAIL rotate_unknown_revoked';
  end if;
  insert into _results values ('rotate_with_nonce_ok'), ('rotate_nonce_replay'), ('rotate_ts_old_stale'),
    ('rotate_ts_future_stale'), ('rotate_short_nonce'), ('refused_requests_spend_nothing'),
    ('rotate_needs_pc_key'), ('rotate_unknown_revoked');
  -- ts within the window is accepted.
  select status into s from public.pc_rotate_refresh(pg_temp.h('t-2'), pg_temp.h('t-3'),
    pg_temp.pc(33), pg_temp.k(33), pg_temp.nn(), now() - interval '299 seconds');
  if s <> 'ok' then raise exception 'FAIL rotate_ts_within_window (%)', s; end if;
  insert into _results values ('rotate_ts_within_window');
  begin
    perform public.pc_rotate_refresh(pg_temp.h('t-3'), null, pg_temp.pc(33),
      pg_temp.k(33), pg_temp.nn(), now());
    raise exception 'FAIL rotate_bad_new_hash';
  exception when invalid_parameter_value then null;
  end;
  insert into _results values ('rotate_bad_new_hash');
end $$;
reset role;

-- Token of PC a3 presented for PC a1 (own key of a1): 'revoked', a3 untouched.
insert into public.my_pcs (pc_id, user_id, display_name, platform)
  values (pg_temp.pc(35), '00000000-0000-0000-0000-0000000006b1', 'B', 'windows');
insert into public.pc_keys values (pg_temp.pc(35), pg_temp.k(35));
set role service_role;
do $$ begin
  if pg_temp.rot(pg_temp.pc(35), 't-3', 't-4', pg_temp.k(35)) <> 'revoked'
     or exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('t-3') and used_at is not null) then
    raise exception 'FAIL rotate_other_pc_token_revoked';
  end if;
  insert into _results values ('rotate_other_pc_token_revoked');
end $$;
reset role;

-- Expired token: 'revoked', nothing issued.
update public.pc_refresh_tokens set expires_at = now() - interval '1 second' where token_hash = pg_temp.h('t-3');
set role service_role;
do $$ begin
  if pg_temp.rot(pg_temp.pc(33), 't-3', 't-4', pg_temp.k(33)) <> 'revoked'
     or exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('t-4')) then
    raise exception 'FAIL rotate_expired_revoked';
  end if;
  insert into _results values ('rotate_expired_revoked');
end $$;
reset role;

-- Retry after a lost response (R-1): the same old token with the same new
-- hash (Edge derives new from old) within 60 s gets 'ok' again, no change.
insert into public.my_pcs (pc_id, user_id, display_name, platform)
  values (pg_temp.pc(40), '00000000-0000-0000-0000-0000000006a1', 'Retry', 'windows');
insert into public.pc_keys values (pg_temp.pc(40), pg_temp.k(40));
insert into public.pc_refresh_tokens (pc_id, family_id, token_hash)
  values (pg_temp.pc(40), gen_random_uuid(), pg_temp.h('i-0'));
set role service_role;
do $$ declare r record; s text; begin
  if pg_temp.rot(pg_temp.pc(40), 'i-0', 'i-1', pg_temp.k(40)) <> 'ok' then
    raise exception 'FAIL retry_setup';
  end if;
  -- Lost response: the PC sends the same old token again (new nonce).
  select * into r from public.pc_rotate_refresh(pg_temp.h('i-0'), pg_temp.h('i-1'),
    pg_temp.pc(40), pg_temp.k(40), pg_temp.nn(), now());
  if r.status <> 'ok' or r.user_id <> '00000000-0000-0000-0000-0000000006a1' then
    raise exception 'FAIL retry_in_window_ok (%)', r;
  end if;
  if pg_temp.rot(pg_temp.pc(40), 'i-0', 'i-1', pg_temp.k(40)) <> 'ok' then
    raise exception 'FAIL retry_twice_ok';
  end if;
  if (select count(*) from public.pc_refresh_tokens where pc_id = pg_temp.pc(40)) <> 2
     or exists (select 1 from public.pc_refresh_tokens
                 where pc_id = pg_temp.pc(40) and revoked_at is not null)
     or not exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('i-1')
                     and used_at is null and revoked_at is null) then
    raise exception 'FAIL retry_changes_nothing';
  end if;
  -- Retry with another new hash is a clone, not a retry.
  s := pg_temp.rot(pg_temp.pc(40), 'i-0', 'i-x', pg_temp.k(40));
  if s <> 'reuse.detected' then raise exception 'FAIL retry_other_new_hash_reuse (%)', s; end if;
  if exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('i-x'))
     or exists (select 1 from public.pc_refresh_tokens
                 where pc_id = pg_temp.pc(40) and revoked_at is null) then
    raise exception 'FAIL retry_other_new_hash_reuse';
  end if;
  insert into _results values ('retry_in_window_ok'), ('retry_twice_ok'), ('retry_changes_nothing'),
    ('retry_other_new_hash_reuse');
end $$;
reset role;

-- Retry after the new token was already used: 'reuse.detected'.
insert into public.pc_refresh_tokens (pc_id, family_id, token_hash)
  values (pg_temp.pc(40), gen_random_uuid(), pg_temp.h('j-0'));
set role service_role;
do $$ declare s text; begin
  if pg_temp.rot(pg_temp.pc(40), 'j-0', 'j-1', pg_temp.k(40)) <> 'ok'
     or pg_temp.rot(pg_temp.pc(40), 'j-1', 'j-2', pg_temp.k(40)) <> 'ok' then
    raise exception 'FAIL retry_after_new_setup';
  end if;
  s := pg_temp.rot(pg_temp.pc(40), 'j-0', 'j-1', pg_temp.k(40));
  if s <> 'reuse.detected' then raise exception 'FAIL retry_after_new_used_reuse (%)', s; end if;
  if exists (select 1 from public.pc_refresh_tokens
                 where pc_id = pg_temp.pc(40) and revoked_at is null) then
    raise exception 'FAIL retry_after_new_used_reuse';
  end if;
  -- Retry of the old token after the family is revoked: 'revoked'.
  if pg_temp.rot(pg_temp.pc(40), 'j-1', 'j-2', pg_temp.k(40)) <> 'revoked' then
    raise exception 'FAIL retry_after_family_revoked';
  end if;
  insert into _results values ('retry_after_new_used_reuse'), ('retry_after_family_revoked');
end $$;
reset role;

-- Window edge: 59 s after rotation still a retry, 61 s is reuse.
insert into public.pc_refresh_tokens (pc_id, family_id, token_hash)
  values (pg_temp.pc(40), gen_random_uuid(), pg_temp.h('w-0'));
set role service_role;
select pg_temp.rot(pg_temp.pc(40), 'w-0', 'w-1', pg_temp.k(40)) \g /dev/null
reset role;
update public.pc_refresh_tokens set used_at = now() - interval '59 seconds' where token_hash = pg_temp.h('w-0');
set role service_role;
do $$ begin
  if pg_temp.rot(pg_temp.pc(40), 'w-0', 'w-1', pg_temp.k(40)) <> 'ok' then
    raise exception 'FAIL retry_59s_ok';
  end if;
  insert into _results values ('retry_59s_ok');
end $$;
reset role;
update public.pc_refresh_tokens set used_at = now() - interval '61 seconds' where token_hash = pg_temp.h('w-0');
set role service_role;
do $$ declare s text; begin
  -- Rotation and the row check in separate statements (one snapshot each).
  s := pg_temp.rot(pg_temp.pc(40), 'w-0', 'w-1', pg_temp.k(40));
  if s <> 'reuse.detected' then raise exception 'FAIL retry_61s_reuse (%)', s; end if;
  if exists (select 1 from public.pc_refresh_tokens
                 where pc_id = pg_temp.pc(40) and revoked_at is null) then
    raise exception 'FAIL retry_61s_reuse';
  end if;
  -- The retry hash stays a hash: no plaintext column exists.
  if exists (select 1 from information_schema.columns where table_schema = 'public'
              and table_name = 'pc_refresh_tokens' and data_type <> 'bytea'
              and column_name not in ('id','pc_id','family_id','created_at','used_at','revoked_at','expires_at')) then
    raise exception 'FAIL refresh_only_hashes';
  end if;
  insert into _results values ('retry_61s_reuse'), ('refresh_only_hashes');
end $$;
reset role;
truncate public.rate_limits;

-- Rate limit: 30 attempts per hour per pc_id, 31st refused.
truncate public.rate_limits;
insert into public.pc_refresh_tokens (pc_id, family_id, token_hash)
  values (pg_temp.pc(33), gen_random_uuid(), pg_temp.h('rl-0'));
set role service_role;
do $$ declare i int; s text; begin
  for i in 1..30 loop
    s := pg_temp.rot(pg_temp.pc(33), 'rl-' || (i - 1), 'rl-' || i, pg_temp.k(33));
    if s <> 'ok' then raise exception 'FAIL rotate_rate_limit_30_ok (% at %)', s, i; end if;
  end loop;
  s := pg_temp.rot(pg_temp.pc(33), 'rl-30', 'rl-31', pg_temp.k(33));
  if s <> 'rate.limited' then raise exception 'FAIL rotate_rate_limited (%)', s; end if;
  -- Limit is per pc_id: PC b2 unaffected (its unknown token -> 'revoked').
  if pg_temp.rot(pg_temp.pc(35), 'zz', 'zz2', pg_temp.k(35)) <> 'revoked' then
    raise exception 'FAIL rotate_rate_limit_per_pc';
  end if;
  insert into _results values ('rotate_rate_limit_30_ok'), ('rotate_rate_limited'), ('rotate_rate_limit_per_pc');
end $$;
reset role;
truncate public.rate_limits;

-- Deleting the PC from my_pcs (service_role, e.g. account deletion) removes
-- its refresh tokens; rotation then gives 'revoked'.
set role service_role;
do $$ begin
  delete from public.my_pcs where pc_id = pg_temp.pc(33);
  if exists (select 1 from public.pc_refresh_tokens where pc_id = pg_temp.pc(33)) then
    raise exception 'FAIL delete_pc_removes_refresh';
  end if;
  if pg_temp.rot(pg_temp.pc(33), 'rl-30', 'rl-31', pg_temp.k(33)) <> 'revoked' then
    raise exception 'FAIL rotate_after_delete_revoked';
  end if;
  insert into _results values ('delete_pc_removes_refresh'), ('rotate_after_delete_revoked');
end $$;
reset role;

-- pc_release by the owner: refresh of this PC no longer works.
do $$ declare s text; begin
  s := pg_temp.bind(pg_temp.pc(36), pg_temp.k(36), pg_temp.k(36),
    '00000000-0000-0000-0000-0000000006a1', 'rel', 'rel-0');
  if s <> 'ok' then raise exception 'FAIL release_setup (%)', s; end if;
end $$;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000006a1') \g /dev/null
select public.pc_release(pg_temp.pc(36)) \g /dev/null
reset role;
set role service_role;
do $$ begin
  if exists (select 1 from public.pc_refresh_tokens where pc_id = pg_temp.pc(36)
              and revoked_at is null)
     or pg_temp.rot(pg_temp.pc(36), 'rel-0', 'rel-1', pg_temp.k(36)) <> 'revoked' then
    raise exception 'FAIL release_revokes_refresh';
  end if;
  insert into _results values ('release_revokes_refresh');
end $$;
reset role;

-- Rebind to another account (same key, E4-2): the old family is
-- revoked, the new binding gets its own refresh.
do $$ declare s text; begin
  s := pg_temp.bind(pg_temp.pc(37), pg_temp.k(37), pg_temp.k(37),
    '00000000-0000-0000-0000-0000000006a1', 'old', 'rb-old');
  if s <> 'ok' then raise exception 'FAIL rebind_setup (%)', s; end if;
  s := pg_temp.bind(pg_temp.pc(37), pg_temp.k(37), pg_temp.k(37),
    '00000000-0000-0000-0000-0000000006b1', 'new', 'rb-new');
  if s <> 'ok' then raise exception 'FAIL rebind_setup2 (%)', s; end if;
  if (select revoked_at from public.pc_refresh_tokens where token_hash = pg_temp.h('rb-old')) is null
     or (select revoked_at from public.pc_refresh_tokens where token_hash = pg_temp.h('rb-new')) is not null then
    raise exception 'FAIL rebind_revokes_old_refresh';
  end if;
end $$;
set role service_role;
do $$ declare r record; begin
  if pg_temp.rot(pg_temp.pc(37), 'rb-old', 'rb-x', pg_temp.k(37)) <> 'revoked' then
    raise exception 'FAIL rebind_old_refresh_rejected';
  end if;
  select * into r from public.pc_rotate_refresh(pg_temp.h('rb-new'), pg_temp.h('rb-new2'),
    pg_temp.pc(37), pg_temp.k(37), pg_temp.nn(), now());
  if r.status <> 'ok' or r.user_id <> '00000000-0000-0000-0000-0000000006b1' then
    raise exception 'FAIL rebind_new_refresh_ok (%)', r;
  end if;
  insert into _results values ('rebind_revokes_old_refresh'), ('rebind_old_refresh_rejected'),
    ('rebind_new_refresh_ok');
end $$;
reset role;

-- Re-login of the same account (same key, E4-2: no rotation) also starts a new family.
do $$ declare s text; begin
  s := pg_temp.bind(pg_temp.pc(37), pg_temp.k(37), pg_temp.k(37),
    '00000000-0000-0000-0000-0000000006b1', 'same', 'rb-same');
  if s <> 'ok' then raise exception 'FAIL relogin_setup (%)', s; end if;
  if (select count(*) from public.pc_refresh_tokens
       where pc_id = pg_temp.pc(37) and revoked_at is null) <> 1
     or (select revoked_at from public.pc_refresh_tokens where token_hash = pg_temp.h('rb-same')) is not null then
    raise exception 'FAIL relogin_one_live_family';
  end if;
  insert into _results values ('relogin_one_live_family');
end $$;

-- cleanup(): expired tokens go, live ones stay.
update public.pc_refresh_tokens set expires_at = now() - interval '1 second' where token_hash = pg_temp.h('rb-old');
set role service_role;
select public.cleanup() \g /dev/null
reset role;
do $$ begin
  if exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('rb-old')) then
    raise exception 'FAIL cleanup_removes_expired_refresh';
  end if;
  if not exists (select 1 from public.pc_refresh_tokens where token_hash = pg_temp.h('rb-same')) then
    raise exception 'FAIL cleanup_keeps_live_refresh';
  end if;
  insert into _results values ('cleanup_removes_expired_refresh'), ('cleanup_keeps_live_refresh');
end $$;

select 'PASSED ' || count(*) from _results;
