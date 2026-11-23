-- Tests for PC sign-in codes, pc_keys and rate limits (E3-5).
\set ON_ERROR_STOP on

insert into auth.users (id, email) values
  ('00000000-0000-0000-0000-0000000005a1', 'la@example.test'),
  ('00000000-0000-0000-0000-0000000005b1', 'lb@example.test');

create temp table _results (name text);
grant insert on _results to authenticated, anon, service_role;

-- Test helpers (temp schema, dropped with the session).
create function pg_temp.h(t text) returns bytea language sql immutable
  as $$ select sha256(convert_to(t, 'UTF8')) $$;
create function pg_temp.spki() returns bytea language sql immutable
  as $$ select decode(repeat('30', 91), 'hex') $$;
create function pg_temp.as_user(u text) returns void language sql
  as $$ select set_config('request.jwt.claims',
          json_build_object('sub', u, 'role', 'authenticated')::text, false) $$;
grant execute on all functions in schema pg_temp to authenticated, anon, service_role;

-- Catalog: deny by default for API roles.
do $$ declare t text; r text; begin
  foreach t in array array['pc_keys','pc_login_codes','rate_limits'] loop
    foreach r in array array['anon','authenticated'] loop
      if has_table_privilege(r, 'public.' || t, 'select,insert,update,delete') then
        raise exception 'FAIL api_roles_no_table_access % %', r, t;
      end if;
    end loop;
    if not (select relrowsecurity and relforcerowsecurity from pg_class where oid = ('public.' || t)::regclass) then
      raise exception 'FAIL rls_forced %', t;
    end if;
  end loop;
  insert into _results values ('api_roles_no_table_access'), ('rls_forced');
end $$;

do $$ declare f text; r text; begin
  foreach f in array array['public.rate_limit_hit(text,text,int,int)',
      'public.pc_login_start(uuid,bytea,text,bytea,bytea)',
      'public.pc_login_consume(bytea)', 'public.cleanup()',
      'public.pc_revoke_refresh_tokens(uuid,uuid)'] loop
    foreach r in array array['anon','authenticated'] loop
      if has_function_privilege(r, f, 'execute') then
        raise exception 'FAIL service_only_functions % %', r, f;
      end if;
    end loop;
  end loop;
  if has_function_privilege('anon', 'public.pc_login_approve(bytea)', 'execute')
     or not has_function_privilege('authenticated', 'public.pc_login_approve(bytea)', 'execute') then
    raise exception 'FAIL approve_grants';
  end if;
  insert into _results values ('service_only_functions'), ('approve_grants');
end $$;

do $$ declare n int; begin
  select count(*) into n from pg_proc p join pg_namespace s on s.oid = p.pronamespace
   where s.nspname = 'public'
     and p.proname in ('rate_limit_hit','pc_login_start','pc_login_approve','pc_login_consume','cleanup','pc_revoke_refresh_tokens')
     and p.prosecdef and p.proconfig @> array['search_path=""'];
  if n <> 6 then raise exception 'FAIL definer_empty_search_path (%)', n; end if;
  insert into _results values ('definer_empty_search_path');
end $$;

-- Behaviour as authenticated: tables unreadable even with a valid session.
set role service_role;
select public.pc_login_start('20000000-0000-0000-0000-0000000005a1', pg_temp.spki(), 'Home PC',
  pg_temp.h('dev-1'), pg_temp.h('USER-1')) \g /dev/null
reset role;

set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ declare t text; begin
  foreach t in array array['pc_keys','pc_login_codes','rate_limits'] loop
    begin
      execute 'select count(*) from public.' || t;
      raise exception 'FAIL authenticated_cannot_read %', t;
    exception when insufficient_privilege then null;
    end;
  end loop;
  begin
    perform * from public.pc_login_consume(pg_temp.h('dev-1'));
    raise exception 'FAIL authenticated_cannot_consume';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('authenticated_cannot_read'), ('authenticated_cannot_consume');
end $$;
reset role;

set role anon;
select set_config('request.jwt.claims', '{"role":"anon"}', false) \g /dev/null
do $$ declare t text; begin
  foreach t in array array['pc_keys','pc_login_codes','rate_limits'] loop
    begin
      execute 'select count(*) from public.' || t;
      raise exception 'FAIL anon_cannot_read %', t;
    exception when insufficient_privilege then null;
    end;
  end loop;
  begin
    perform public.pc_login_approve(pg_temp.h('USER-1'));
    raise exception 'FAIL anon_cannot_approve';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('anon_cannot_read'), ('anon_cannot_approve');
end $$;
reset role;

-- consume before approve -> pending.
set role service_role;
do $$ declare s text; begin
  select status into s from public.pc_login_consume(pg_temp.h('dev-1'));
  if s <> 'pending' then raise exception 'FAIL consume_before_approve_pending (%)', s; end if;
  if (select attempts from public.pc_login_codes where device_code_hash = pg_temp.h('dev-1')) <> 1 then
    raise exception 'FAIL consume_pending_counts_attempt';
  end if;
  insert into _results values ('consume_before_approve_pending'), ('consume_pending_counts_attempt');
end $$;
reset role;

-- approve by A, then consume -> PC in A's my_pcs; second consume -> invalid.
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ begin
  if public.pc_login_approve(pg_temp.h('USER-1')) <> 'ok' then raise exception 'FAIL approve_ok'; end if;
  if public.pc_login_approve(pg_temp.h('USER-1')) <> 'code.invalid' then
    raise exception 'FAIL approve_twice_invalid';
  end if;
  insert into _results values ('approve_ok'), ('approve_twice_invalid');
end $$;
reset role;

set role service_role;
do $$ declare r record; begin
  select * into r from public.pc_login_consume(pg_temp.h('dev-1'));
  if r.status <> 'ok' or r.user_id <> '00000000-0000-0000-0000-0000000005a1' or r.rebound then
    raise exception 'FAIL consume_ok (%)', r;
  end if;
  if not exists (select 1 from public.my_pcs where pc_id = '20000000-0000-0000-0000-0000000005a1'
                 and user_id = '00000000-0000-0000-0000-0000000005a1' and display_name = 'Home PC') then
    raise exception 'FAIL pc_in_approver_my_pcs';
  end if;
  if (select public_key_spki from public.pc_keys where pc_id = '20000000-0000-0000-0000-0000000005a1') <> pg_temp.spki() then
    raise exception 'FAIL pc_key_stored';
  end if;
  select * into r from public.pc_login_consume(pg_temp.h('dev-1'));
  if r.status <> 'code.invalid' then raise exception 'FAIL consume_twice_invalid'; end if;
  select * into r from public.pc_login_consume(pg_temp.h('no-such'));
  if r.status <> 'code.invalid' then raise exception 'FAIL consume_unknown_invalid'; end if;
  insert into _results values ('consume_ok'), ('pc_in_approver_my_pcs'), ('pc_key_stored'),
    ('consume_twice_invalid'), ('consume_unknown_invalid');
end $$;
reset role;

-- A sees own PC through RLS, B does not.
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005b1') \g /dev/null
do $$ begin
  if exists (select 1 from public.my_pcs where pc_id = '20000000-0000-0000-0000-0000000005a1') then
    raise exception 'FAIL other_user_does_not_see_pc';
  end if;
  insert into _results values ('other_user_does_not_see_pc');
end $$;
reset role;

-- Rebind: B approves a new sign-in of the same PC -> PC moves to B, revoke hook called.
create table public._revoke_log (pc_id uuid, old_user uuid);
grant insert on public._revoke_log to service_role;
create or replace function public.pc_revoke_refresh_tokens(p_pc_id uuid, p_old_user uuid)
returns void language plpgsql security definer set search_path = '' as $$
begin insert into public._revoke_log values (p_pc_id, p_old_user); end $$;
revoke all on function public.pc_revoke_refresh_tokens(uuid, uuid) from public, anon, authenticated, service_role;

set role service_role;
select public.pc_login_start('20000000-0000-0000-0000-0000000005a1', decode(repeat('31', 91), 'hex'),
  'Moved PC', pg_temp.h('dev-2'), pg_temp.h('USER-2')) \g /dev/null
reset role;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005b1') \g /dev/null
select public.pc_login_approve(pg_temp.h('USER-2')) \g /dev/null
reset role;
set role service_role;
do $$ declare r record; begin
  select * into r from public.pc_login_consume(pg_temp.h('dev-2'));
  if r.status <> 'ok' or not r.rebound or r.user_id <> '00000000-0000-0000-0000-0000000005b1' then
    raise exception 'FAIL rebind_ok (%)', r;
  end if;
  insert into _results values ('rebind_ok');
end $$;
reset role;
do $$ begin
  if (select user_id from public.my_pcs where pc_id = '20000000-0000-0000-0000-0000000005a1')
     <> '00000000-0000-0000-0000-0000000005b1' then
    raise exception 'FAIL rebind_moves_my_pcs';
  end if;
  if (select count(*) from public._revoke_log where pc_id = '20000000-0000-0000-0000-0000000005a1'
      and old_user = '00000000-0000-0000-0000-0000000005a1') <> 1 then
    raise exception 'FAIL rebind_calls_revoke_hook';
  end if;
  if (select public_key_spki from public.pc_keys where pc_id = '20000000-0000-0000-0000-0000000005a1')
     <> decode(repeat('31', 91), 'hex') then
    raise exception 'FAIL rebind_replaces_key';
  end if;
  insert into _results values ('rebind_moves_my_pcs'), ('rebind_calls_revoke_hook'), ('rebind_replaces_key');
end $$;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ begin
  if exists (select 1 from public.my_pcs where pc_id = '20000000-0000-0000-0000-0000000005a1') then
    raise exception 'FAIL old_owner_loses_pc';
  end if;
  insert into _results values ('old_owner_loses_pc');
end $$;
reset role;
drop table public._revoke_log;
create or replace function public.pc_revoke_refresh_tokens(p_pc_id uuid, p_old_user uuid)
returns void language plpgsql security definer set search_path = '' as $$ begin return; end $$;
revoke all on function public.pc_revoke_refresh_tokens(uuid, uuid) from public, anon, authenticated, service_role;

-- Expired code -> code.invalid on approve and consume.
set role service_role;
select public.pc_login_start('20000000-0000-0000-0000-0000000005a2', pg_temp.spki(), 'Old',
  pg_temp.h('dev-3'), pg_temp.h('USER-3')) \g /dev/null
select public.pc_login_start('20000000-0000-0000-0000-0000000005a2', pg_temp.spki(), 'Old',
  pg_temp.h('dev-4'), pg_temp.h('USER-4')) \g /dev/null
reset role;
do $$ begin
  if (select max(expires_at - created_at) from public.pc_login_codes) <> interval '10 minutes' then
    raise exception 'FAIL code_ttl_10_min';
  end if;
  insert into _results values ('code_ttl_10_min');
end $$;
update public.pc_login_codes set expires_at = now() - interval '1 second'
 where device_code_hash in (pg_temp.h('dev-3'), pg_temp.h('dev-4'));
-- dev-4 approved before expiry.
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-0000000005a1', approved_at = now()
 where device_code_hash = pg_temp.h('dev-4');
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ begin
  if public.pc_login_approve(pg_temp.h('USER-3')) <> 'code.invalid' then raise exception 'FAIL approve_expired_invalid'; end if;
  insert into _results values ('approve_expired_invalid');
end $$;
reset role;
set role service_role;
do $$ begin
  if (select status from public.pc_login_consume(pg_temp.h('dev-3'))) <> 'code.invalid'
     or (select status from public.pc_login_consume(pg_temp.h('dev-4'))) <> 'code.invalid' then
    raise exception 'FAIL consume_expired_invalid';
  end if;
  insert into _results values ('consume_expired_invalid');
end $$;
reset role;

-- No plaintext codes: every stored code value is a 32-byte hash.
do $$ begin
  if exists (select 1 from public.pc_login_codes
             where octet_length(device_code_hash) <> 32 or octet_length(user_code_hash) <> 32) then
    raise exception 'FAIL only_hashes_stored';
  end if;
  if (select count(*) from information_schema.columns where table_schema = 'public'
      and table_name = 'pc_login_codes' and data_type in ('text','character varying')) <> 1 then
    raise exception 'FAIL no_text_code_columns';
  end if;
  begin
    set local role service_role;
    perform public.pc_login_start(gen_random_uuid(), pg_temp.spki(), 'x', convert_to('ABCD-1234','UTF8'), pg_temp.h('z'));
    raise exception 'FAIL plaintext_code_rejected';
  exception when check_violation then null;
  end;
  insert into _results values ('only_hashes_stored'), ('no_text_code_columns'), ('plaintext_code_rejected');
end $$;
reset role;

-- Sixth start for one pc_id within 10 minutes -> rate.limited.
set role service_role;
do $$ declare i int; s text; begin
  for i in 1..5 loop
    s := public.pc_login_start('20000000-0000-0000-0000-0000000005c1', pg_temp.spki(), 'R',
      pg_temp.h('rs-d' || i), pg_temp.h('rs-u' || i));
    if s <> 'ok' then raise exception 'FAIL start_5_ok (%: %)', i, s; end if;
  end loop;
  s := public.pc_login_start('20000000-0000-0000-0000-0000000005c1', pg_temp.spki(), 'R',
    pg_temp.h('rs-d6'), pg_temp.h('rs-u6'));
  if s <> 'rate.limited' then raise exception 'FAIL start_6th_rate_limited (%)', s; end if;
  if (select count(*) from public.pc_login_codes where pc_id = '20000000-0000-0000-0000-0000000005c1') <> 5 then
    raise exception 'FAIL start_limited_no_row';
  end if;
  insert into _results values ('start_5_ok'), ('start_6th_rate_limited'), ('start_limited_no_row');
end $$;
reset role;

-- Approve rate limit: failures count; 11th call is rate.limited even for a valid code.
-- Each statement is its own transaction, like separate API calls.
truncate public.rate_limits;
set role service_role;
select public.pc_login_start('20000000-0000-0000-0000-0000000005d1', pg_temp.spki(), 'Valid',
  pg_temp.h('rl-dev'), pg_temp.h('RL-VALID')) \g /dev/null
reset role;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005b1') \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-' || g)) from generate_series(1, 1) g \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-2')) \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-3')) \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-4')) \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-5')) \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-6')) \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-7')) \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-8')) \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-9')) \g /dev/null
do $$ begin
  if public.pc_login_approve(pg_temp.h('guess-10')) <> 'code.invalid' then raise exception 'FAIL approve_10th_still_checked'; end if;
  insert into _results values ('approve_10th_still_checked');
end $$;
do $$ begin
  if public.pc_login_approve(pg_temp.h('RL-VALID')) <> 'rate.limited' then raise exception 'FAIL approve_11th_rate_limited'; end if;
  insert into _results values ('approve_11th_rate_limited');
end $$;
-- Another user is not affected by B's limit.
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ begin
  if public.pc_login_approve(pg_temp.h('RL-VALID')) <> 'ok' then raise exception 'FAIL rate_limit_per_user'; end if;
  insert into _results values ('rate_limit_per_user');
end $$;
reset role;

-- Existence not revealed: unknown / expired / approved / consumed codes give
-- identical results and identical side effects (one counter hit each).
truncate public.rate_limits;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005b1') \g /dev/null
do $$ declare a text; b text; c text; d text; begin
  a := public.pc_login_approve(pg_temp.h('never-issued'));
  b := public.pc_login_approve(pg_temp.h('USER-3'));     -- expired
  c := public.pc_login_approve(pg_temp.h('RL-VALID'));   -- already approved
  d := public.pc_login_approve(pg_temp.h('USER-1'));     -- consumed
  if a <> 'code.invalid' or b <> a or c <> a or d <> a then
    raise exception 'FAIL approve_no_existence_oracle (% % % %)', a, b, c, d;
  end if;
  insert into _results values ('approve_no_existence_oracle');
end $$;
reset role;
do $$ begin
  if (select sum(count) from public.rate_limits where bucket = 'pc_login_approve') <> 4 then
    raise exception 'FAIL approve_counts_every_attempt';
  end if;
  -- The lookup path is one unique-index probe whatever the outcome.
  if not exists (select 1 from pg_indexes where schemaname = 'public' and tablename = 'pc_login_codes'
                 and indexdef like '%UNIQUE%(user_code_hash)%') then
    raise exception 'FAIL user_code_unique_index';
  end if;
  insert into _results values ('approve_counts_every_attempt'), ('user_code_unique_index');
end $$;

-- 20-PC limit at consume: approving account with 20 PCs gets pcs.limit, code stays usable.
insert into auth.users values ('00000000-0000-0000-0000-0000000005e1', 'lim@example.test');
insert into public.my_pcs (pc_id, user_id, display_name, platform)
  select ('20000000-0000-0000-0000-0000000e' || lpad(g::text, 4, '0'))::uuid,
         '00000000-0000-0000-0000-0000000005e1', 'P' || g, 'windows' from generate_series(1, 20) g;
set role service_role;
select public.pc_login_start('20000000-0000-0000-0000-0000000005e9', pg_temp.spki(), 'Extra',
  pg_temp.h('lim-dev'), pg_temp.h('LIM-USER')) \g /dev/null
reset role;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005e1') \g /dev/null
select public.pc_login_approve(pg_temp.h('LIM-USER')) \g /dev/null
reset role;
set role service_role;
do $$ begin
  if (select status from public.pc_login_consume(pg_temp.h('lim-dev'))) <> 'pcs.limit' then
    raise exception 'FAIL consume_respects_20_limit';
  end if;
  insert into _results values ('consume_respects_20_limit');
end $$;
reset role;
delete from public.my_pcs where pc_id = '20000000-0000-0000-0000-0000000e0001';
set role service_role;
do $$ begin
  if (select status from public.pc_login_consume(pg_temp.h('lim-dev'))) <> 'ok' then
    raise exception 'FAIL consume_after_freeing_slot';
  end if;
  if exists (select 1 from public.pc_keys where pc_id = '20000000-0000-0000-0000-0000000e0001') then
    raise exception 'FAIL pc_keys_cascade';
  end if;
  insert into _results values ('consume_after_freeing_slot'), ('pc_keys_cascade');
end $$;
reset role;

-- cleanup(): removes expired/consumed codes and old rate rows, keeps live ones.
insert into public.rate_limits values ('x', 'old', now() - interval '2 days', 1);
set role service_role;
select public.cleanup() \g /dev/null
reset role;
do $$ begin
  if exists (select 1 from public.pc_login_codes where expires_at <= now() or consumed_at is not null)
     or exists (select 1 from public.rate_limits where key = 'old') then
    raise exception 'FAIL cleanup_removes_stale';
  end if;
  if not exists (select 1 from public.pc_login_codes where device_code_hash = pg_temp.h('rs-d1'))
     or not exists (select 1 from public.rate_limits where bucket = 'pc_login_approve') then
    raise exception 'FAIL cleanup_keeps_live';
  end if;
  insert into _results values ('cleanup_removes_stale'), ('cleanup_keeps_live');
end $$;

select 'PASSED ' || count(*) from _results;
