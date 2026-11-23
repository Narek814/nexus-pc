-- Tests for PC sign-in codes, pc_keys, signed requests and rate limits (E3-5, E3-5b).
\set ON_ERROR_STOP on

insert into auth.users (id, email) values
  ('00000000-0000-0000-0000-0000000005a1', 'la@example.test'),
  ('00000000-0000-0000-0000-0000000005b1', 'lb@example.test');

create temp table _results (name text);
grant insert on _results to authenticated, anon, service_role;

-- Test helpers (temp schema, dropped with the session).
create function pg_temp.h(t text) returns bytea language sql immutable
  as $$ select sha256(convert_to(t, 'UTF8')) $$;
-- Fake SPKI blobs: key n -> 91 bytes of 0x30+n. Signatures are verified by the
-- Edge Function; SQL only gets the key it verified with.
create function pg_temp.k(n int) returns bytea language sql immutable
  as $$ select decode(repeat(to_hex(48 + n), 91), 'hex') $$;
create function pg_temp.spki() returns bytea language sql immutable
  as $$ select pg_temp.k(0) $$;
create function pg_temp.nn() returns bytea language sql volatile
  as $$ select uuid_send(gen_random_uuid()) $$;
-- Fresh 32-byte refresh token hash for a consume poll (E3-6).
create function pg_temp.rh() returns bytea language sql volatile
  as $$ select sha256(uuid_send(gen_random_uuid())) $$;
create function pg_temp.as_user(u text) returns void language sql
  as $$ select set_config('request.jwt.claims',
          json_build_object('sub', u, 'role', 'authenticated')::text, false) $$;
-- Signed start with a fresh nonce.
create function pg_temp.st(pc uuid, key bytea, name text, dev text, usr text, signer bytea)
  returns text language sql volatile
  as $$ select public.pc_login_start(pc, key, name, pg_temp.h(dev), pg_temp.h(usr),
          signer, pg_temp.nn(), now()) $$;
-- Signed poll; moves last_poll_at 10 s back to simulate the poll interval.
create function pg_temp.co(dev text, key bytea)
  returns table (status text, pc_id uuid, user_id uuid, rebound boolean)
  language plpgsql volatile as $$
#variable_conflict use_column
begin
  update public.pc_login_codes set last_poll_at = last_poll_at - interval '10 seconds'
   where device_code_hash = pg_temp.h(dev);
  return query select * from public.pc_login_consume(pg_temp.h(dev), pg_temp.rh(), key, pg_temp.nn(), now());
end $$;
-- Two-step approval by the current user.
create function pg_temp.ok(usr text) returns text language plpgsql volatile as $$
declare s text;
begin
  select status into s from public.pc_login_describe(pg_temp.h(usr));
  if s <> 'ok' then return 'describe:' || s; end if;
  return public.pc_login_approve(pg_temp.h(usr));
end $$;
grant execute on all functions in schema pg_temp to authenticated, anon, service_role;

-- Catalog: deny by default for API roles.
do $$ declare t text; r text; begin
  foreach t in array array['pc_keys','pc_login_codes','pc_login_nonces','rate_limits'] loop
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
      'public.pc_login_start(uuid,bytea,text,bytea,bytea,bytea,bytea,timestamptz)',
      'public.pc_login_consume(bytea,bytea,bytea,bytea,timestamptz)', 'public.cleanup()',
      'public.pc_revoke_refresh_tokens(uuid,uuid)', 'public.pc_nonce_use(bytea,timestamptz)',
      'public.pc_lock(uuid)', 'public.pc_approve_gate(uuid)'] loop
    foreach r in array array['anon','authenticated'] loop
      if has_function_privilege(r, f, 'execute') then
        raise exception 'FAIL service_only_functions % %', r, f;
      end if;
    end loop;
  end loop;
  foreach f in array array['public.pc_login_approve(bytea)', 'public.pc_login_describe(bytea)',
      'public.pc_release(uuid)'] loop
    if has_function_privilege('anon', f, 'execute')
       or not has_function_privilege('authenticated', f, 'execute') then
      raise exception 'FAIL approve_grants %', f;
    end if;
  end loop;
  insert into _results values ('service_only_functions'), ('approve_grants');
end $$;

do $$ declare n int; begin
  select count(*) into n from pg_proc p join pg_namespace s on s.oid = p.pronamespace
   where s.nspname = 'public'
     and p.proname in ('rate_limit_hit','pc_login_start','pc_login_approve','pc_login_consume','cleanup',
       'pc_revoke_refresh_tokens','pc_nonce_use','pc_lock','pc_approve_gate','pc_login_describe','pc_release')
     and p.prosecdef and p.proconfig @> array['search_path=""'];
  if n <> 11 then raise exception 'FAIL definer_empty_search_path (%)', n; end if;
  insert into _results values ('definer_empty_search_path');
end $$;

-- First binding: start signed by the new key itself.
set role service_role;
do $$ begin
  if pg_temp.st('20000000-0000-0000-0000-0000000005a1', pg_temp.spki(), 'Home PC',
       'dev-1', 'USER-1', pg_temp.spki()) <> 'ok' then
    raise exception 'FAIL first_binding_start_ok';
  end if;
  insert into _results values ('first_binding_start_ok');
end $$;
reset role;

set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ declare t text; begin
  foreach t in array array['pc_keys','pc_login_codes','pc_login_nonces','rate_limits'] loop
    begin
      execute 'select count(*) from public.' || t;
      raise exception 'FAIL authenticated_cannot_read %', t;
    exception when insufficient_privilege then null;
    end;
  end loop;
  begin
    perform * from public.pc_login_consume(pg_temp.h('dev-1'), pg_temp.rh(), pg_temp.spki(), pg_temp.nn(), now());
    raise exception 'FAIL authenticated_cannot_consume';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('authenticated_cannot_read'), ('authenticated_cannot_consume');
end $$;
reset role;

set role anon;
select set_config('request.jwt.claims', '{"role":"anon"}', false) \g /dev/null
do $$ declare t text; begin
  foreach t in array array['pc_keys','pc_login_codes','pc_login_nonces','rate_limits'] loop
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
  begin
    perform * from public.pc_login_describe(pg_temp.h('USER-1'));
    raise exception 'FAIL anon_cannot_describe';
  exception when insufficient_privilege then null;
  end;
  begin
    perform public.pc_release('20000000-0000-0000-0000-0000000005a1');
    raise exception 'FAIL anon_cannot_release';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('anon_cannot_read'), ('anon_cannot_approve'),
    ('anon_cannot_describe'), ('anon_cannot_release');
end $$;
reset role;

-- consume before approve -> pending; unsigned / wrong-key polls are refused.
set role service_role;
do $$ declare s text; begin
  select status into s from pg_temp.co('dev-1', pg_temp.spki());
  if s <> 'pending' then raise exception 'FAIL consume_before_approve_pending (%)', s; end if;
  if (select attempts from public.pc_login_codes where device_code_hash = pg_temp.h('dev-1')) <> 1 then
    raise exception 'FAIL consume_pending_counts_attempt';
  end if;
  select status into s from pg_temp.co('dev-1', null);
  if s <> 'pc.key_required' then raise exception 'FAIL consume_unsigned_refused (%)', s; end if;
  select status into s from pg_temp.co('dev-1', pg_temp.k(9));
  if s <> 'pc.key_required' then raise exception 'FAIL consume_wrong_key_refused (%)', s; end if;
  insert into _results values ('consume_before_approve_pending'), ('consume_pending_counts_attempt'),
    ('consume_unsigned_refused'), ('consume_wrong_key_refused');
end $$;
reset role;

-- Two-step approval: approve without describe is refused; describe shows name and time only.
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ declare r record; begin
  if public.pc_login_approve(pg_temp.h('USER-1')) <> 'code.invalid' then
    raise exception 'FAIL approve_requires_describe';
  end if;
  select * into r from public.pc_login_describe(pg_temp.h('USER-1'));
  if r.status <> 'ok' or r.display_name <> 'Home PC' or r.requested_at is null
     or r.requested_at > now() then
    raise exception 'FAIL describe_ok (%)', r;
  end if;
  insert into _results values ('approve_requires_describe'), ('describe_ok');
end $$;
-- Another user can neither take over the describe (E3-5d N5) nor approve a
-- code described by someone else.
select pg_temp.as_user('00000000-0000-0000-0000-0000000005b1') \g /dev/null
do $$ begin
  if (select status from public.pc_login_describe(pg_temp.h('USER-1'))) <> 'code.invalid'
     or public.pc_login_approve(pg_temp.h('USER-1')) <> 'code.invalid' then
    raise exception 'FAIL approve_requires_own_describe';
  end if;
end $$;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ begin
  if pg_temp.ok('USER-1') <> 'ok' then raise exception 'FAIL approve_ok'; end if;
  if public.pc_login_approve(pg_temp.h('USER-1')) <> 'code.invalid' then
    raise exception 'FAIL approve_twice_invalid';
  end if;
  if (select status from public.pc_login_describe(pg_temp.h('USER-1'))) <> 'code.invalid' then
    raise exception 'FAIL describe_after_approve_invalid';
  end if;
  insert into _results values ('approve_requires_own_describe'), ('approve_ok'),
    ('approve_twice_invalid'), ('describe_after_approve_invalid');
end $$;
reset role;
do $$ begin
  if (select proallargtypes::regtype[]::text from pg_proc where proname = 'pc_login_describe')
     <> '{bytea,text,text,"timestamp with time zone"}' then
    raise exception 'FAIL describe_returns_no_ids';
  end if;
  insert into _results values ('describe_returns_no_ids');
end $$;

set role service_role;
do $$ declare r record; begin
  select * into r from pg_temp.co('dev-1', pg_temp.spki());
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
  select * into r from pg_temp.co('dev-1', pg_temp.spki());
  if r.status <> 'code.invalid' then raise exception 'FAIL consume_twice_invalid'; end if;
  select * into r from pg_temp.co('no-such', pg_temp.spki());
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

-- Rebind needs the old key: start for a bound pc_id without it -> pc.key_required.
-- The real revoke hook is saved here and restored from it below (not retyped),
-- so the test never hides a broken migration body.
create temp table _hook_orig as
  select pg_get_functiondef('public.pc_revoke_refresh_tokens(uuid,uuid)'::regprocedure) as def;
create table public._revoke_log (pc_id uuid, old_user uuid);
grant insert on public._revoke_log to service_role;
create or replace function public.pc_revoke_refresh_tokens(p_pc_id uuid, p_old_user uuid)
returns void language plpgsql security definer set search_path = '' as $$
begin insert into public._revoke_log values (p_pc_id, p_old_user); end $$;
revoke all on function public.pc_revoke_refresh_tokens(uuid, uuid) from public, anon, authenticated, service_role;

set role service_role;
do $$ declare s text; begin
  s := pg_temp.st('20000000-0000-0000-0000-0000000005a1', pg_temp.k(1), 'Evil', 'evil-1', 'EVIL-1', null);
  if s <> 'pc.key_required' then raise exception 'FAIL rebind_unsigned_refused (%)', s; end if;
  s := pg_temp.st('20000000-0000-0000-0000-0000000005a1', pg_temp.k(1), 'Evil', 'evil-2', 'EVIL-2', pg_temp.k(1));
  if s <> 'pc.key_required' then raise exception 'FAIL rebind_new_key_only_refused (%)', s; end if;
  if exists (select 1 from public.pc_login_codes where display_name = 'Evil') then
    raise exception 'FAIL rebind_refused_no_row';
  end if;
  if exists (select 1 from public.rate_limits where bucket = 'pc_login_start'
             and key = '20000000-0000-0000-0000-0000000005a1' and count > 1) then
    raise exception 'FAIL start_refused_not_counted';
  end if;
  s := pg_temp.st('20000000-0000-0000-0000-0000000005a1', pg_temp.k(1), 'Moved PC', 'dev-2', 'USER-2', pg_temp.spki());
  if s <> 'ok' then raise exception 'FAIL rebind_old_key_start_ok (%)', s; end if;
  insert into _results values ('rebind_unsigned_refused'), ('rebind_new_key_only_refused'),
    ('rebind_refused_no_row'), ('start_refused_not_counted'), ('rebind_old_key_start_ok');
end $$;
reset role;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005b1') \g /dev/null
select pg_temp.ok('USER-2') \g /dev/null
reset role;
set role service_role;
do $$ declare r record; begin
  -- Consume must be signed by the new key, not the old one.
  select * into r from pg_temp.co('dev-2', pg_temp.spki());
  if r.status <> 'pc.key_required' then raise exception 'FAIL rebind_consume_needs_new_key (%)', r; end if;
  select * into r from pg_temp.co('dev-2', pg_temp.k(1));
  if r.status <> 'ok' or not r.rebound or r.user_id <> '00000000-0000-0000-0000-0000000005b1' then
    raise exception 'FAIL rebind_ok (%)', r;
  end if;
  insert into _results values ('rebind_consume_needs_new_key'), ('rebind_ok');
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
     <> pg_temp.k(1) then
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
  -- A cannot release B's PC; the answer is the same as for an unknown PC.
  if public.pc_release('20000000-0000-0000-0000-0000000005a1') <> 'pc.not_found'
     or public.pc_release(gen_random_uuid()) <> 'pc.not_found' then
    raise exception 'FAIL release_other_users_pc_refused';
  end if;
  insert into _results values ('old_owner_loses_pc'), ('release_other_users_pc_refused');
end $$;
reset role;
do $$ begin
  if not exists (select 1 from public.my_pcs where pc_id = '20000000-0000-0000-0000-0000000005a1')
     or exists (select 1 from public._revoke_log where old_user = '00000000-0000-0000-0000-0000000005a1'
                and pc_id = '20000000-0000-0000-0000-0000000005a1' offset 1) then
    raise exception 'FAIL release_other_users_pc_no_effect';
  end if;
  insert into _results values ('release_other_users_pc_no_effect');
end $$;

-- Release by the owner (B): a pending old-key code is voided, PC and key removed,
-- refresh revoked; afterwards a start without the old key is a first binding.
set role service_role;
select pg_temp.st('20000000-0000-0000-0000-0000000005a1', pg_temp.k(3), 'Pending', 'rel-old', 'REL-OLD', pg_temp.k(1)) \g /dev/null
reset role;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005b1') \g /dev/null
do $$ begin
  if public.pc_release('20000000-0000-0000-0000-0000000005a1') <> 'ok' then raise exception 'FAIL release_ok'; end if;
  if exists (select 1 from public.my_pcs where pc_id = '20000000-0000-0000-0000-0000000005a1') then
    raise exception 'FAIL release_removes_my_pcs';
  end if;
  if (select status from public.pc_login_describe(pg_temp.h('REL-OLD'))) <> 'code.invalid' then
    raise exception 'FAIL release_voids_live_codes';
  end if;
  insert into _results values ('release_ok'), ('release_removes_my_pcs'), ('release_voids_live_codes');
end $$;
reset role;
do $$ begin
  if exists (select 1 from public.pc_keys where pc_id = '20000000-0000-0000-0000-0000000005a1') then
    raise exception 'FAIL release_removes_key';
  end if;
  if (select count(*) from public._revoke_log where pc_id = '20000000-0000-0000-0000-0000000005a1'
      and old_user = '00000000-0000-0000-0000-0000000005b1') <> 1 then
    raise exception 'FAIL release_calls_revoke_hook';
  end if;
  insert into _results values ('release_removes_key'), ('release_calls_revoke_hook');
end $$;
set role service_role;
do $$ begin
  if pg_temp.st('20000000-0000-0000-0000-0000000005a1', pg_temp.k(2), 'Fresh', 'rel-new', 'REL-NEW', pg_temp.k(2)) <> 'ok' then
    raise exception 'FAIL start_after_release_first_binding';
  end if;
  insert into _results values ('start_after_release_first_binding');
end $$;
reset role;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
select pg_temp.ok('REL-NEW') \g /dev/null
reset role;
set role service_role;
do $$ declare r record; begin
  select * into r from pg_temp.co('rel-new', pg_temp.k(2));
  if r.status <> 'ok' or r.rebound or r.user_id <> '00000000-0000-0000-0000-0000000005a1' then
    raise exception 'FAIL consume_after_release_ok (%)', r;
  end if;
  select * into r from pg_temp.co('rel-old', pg_temp.k(3));
  if r.status <> 'code.invalid' then raise exception 'FAIL released_code_stays_invalid (%)', r; end if;
  insert into _results values ('consume_after_release_ok'), ('released_code_stays_invalid');
end $$;
reset role;
drop table public._revoke_log;
do $$ begin execute (select def from _hook_orig); end $$;
revoke all on function public.pc_revoke_refresh_tokens(uuid, uuid) from public, anon, authenticated, service_role;
do $$ begin
  if pg_get_functiondef('public.pc_revoke_refresh_tokens(uuid,uuid)'::regprocedure)
     is distinct from (select def from _hook_orig) then
    raise exception 'FAIL revoke_hook_restored';
  end if;
  insert into _results values ('revoke_hook_restored');
end $$;

-- Binding changed between start and consume: two first-binding codes of one new
-- pc_id, both approved; the second consume is refused, not a silent takeover.
truncate public.rate_limits;
set role service_role;
select pg_temp.st('20000000-0000-0000-0000-0000000005f1', pg_temp.k(4), 'Twin A', 'tw-a', 'TW-A', pg_temp.k(4)) \g /dev/null
select pg_temp.st('20000000-0000-0000-0000-0000000005f1', pg_temp.k(5), 'Twin B', 'tw-b', 'TW-B', pg_temp.k(5)) \g /dev/null
reset role;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
select pg_temp.ok('TW-A') \g /dev/null
select pg_temp.as_user('00000000-0000-0000-0000-0000000005b1') \g /dev/null
select pg_temp.ok('TW-B') \g /dev/null
reset role;
set role service_role;
do $$ begin
  if (select status from pg_temp.co('tw-a', pg_temp.k(4))) <> 'ok'
     or (select status from pg_temp.co('tw-b', pg_temp.k(5))) <> 'code.invalid' then
    raise exception 'FAIL consume_binding_changed_invalid';
  end if;
  if (select user_id from public.my_pcs where pc_id = '20000000-0000-0000-0000-0000000005f1')
     <> '00000000-0000-0000-0000-0000000005a1' then
    raise exception 'FAIL consume_binding_changed_keeps_owner';
  end if;
  insert into _results values ('consume_binding_changed_invalid'), ('consume_binding_changed_keeps_owner');
end $$;
reset role;
-- Defence in depth: a my_pcs row without pc_keys appearing after a first-binding
-- start (no such path today) also blocks the consume.
set role service_role;
select pg_temp.st('20000000-0000-0000-0000-0000000005f2', pg_temp.k(4), 'Twin C', 'tw-c', 'TW-C', pg_temp.k(4)) \g /dev/null
reset role;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
select pg_temp.ok('TW-C') \g /dev/null
reset role;
insert into public.my_pcs (pc_id, user_id, display_name, platform)
  values ('20000000-0000-0000-0000-0000000005f2', '00000000-0000-0000-0000-0000000005b1', 'Keyless', 'windows');
set role service_role;
do $$ begin
  if (select status from pg_temp.co('tw-c', pg_temp.k(4))) <> 'code.invalid' then
    raise exception 'FAIL consume_keyless_binding_invalid';
  end if;
  insert into _results values ('consume_keyless_binding_invalid');
end $$;
reset role;

-- Expired code -> code.invalid on describe/approve and consume.
set role service_role;
select pg_temp.st('20000000-0000-0000-0000-0000000005a2', pg_temp.spki(), 'Old', 'dev-3', 'USER-3', pg_temp.spki()) \g /dev/null
select pg_temp.st('20000000-0000-0000-0000-0000000005a2', pg_temp.spki(), 'Old', 'dev-4', 'USER-4', pg_temp.spki()) \g /dev/null
reset role;
do $$ begin
  if (select max(expires_at - created_at) from public.pc_login_codes) <> interval '5 minutes'
     or (select min(expires_at - created_at) from public.pc_login_codes where consumed_at is null
         and display_name = 'Old') <> interval '5 minutes' then
    raise exception 'FAIL code_ttl_5_min';
  end if;
  insert into _results values ('code_ttl_5_min');
end $$;
update public.pc_login_codes set expires_at = now() - interval '1 second'
 where device_code_hash in (pg_temp.h('dev-3'), pg_temp.h('dev-4'));
-- dev-4 approved before expiry.
update public.pc_login_codes set approved_by = '00000000-0000-0000-0000-0000000005a1', approved_at = now()
 where device_code_hash = pg_temp.h('dev-4');
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ begin
  if public.pc_login_approve(pg_temp.h('USER-3')) <> 'code.invalid'
     or (select status from public.pc_login_describe(pg_temp.h('USER-3'))) <> 'code.invalid' then
    raise exception 'FAIL approve_expired_invalid';
  end if;
  insert into _results values ('approve_expired_invalid');
end $$;
reset role;
set role service_role;
do $$ begin
  if (select status from pg_temp.co('dev-3', pg_temp.spki())) <> 'code.invalid'
     or (select status from pg_temp.co('dev-4', pg_temp.spki())) <> 'code.invalid' then
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
    perform public.pc_login_start(gen_random_uuid(), pg_temp.spki(), 'x', convert_to('ABCD-1234','UTF8'),
      pg_temp.h('z'), pg_temp.spki(), pg_temp.nn(), now());
    raise exception 'FAIL plaintext_code_rejected';
  exception when check_violation then null;
  end;
  insert into _results values ('only_hashes_stored'), ('no_text_code_columns'), ('plaintext_code_rejected');
end $$;
reset role;

-- Start limits on one pc_id: unsigned starts are not counted; at most 3 live
-- codes; 6th accepted start within 10 minutes -> rate.limited.
set role service_role;
do $$ declare i int; s text; begin
  for i in 1..8 loop
    s := pg_temp.st('20000000-0000-0000-0000-0000000005c1', pg_temp.spki(), 'R', 'un-d' || i, 'un-u' || i, null);
    if s <> 'pc.key_required' then raise exception 'FAIL start_unsigned_first_binding_refused (%)', s; end if;
  end loop;
  if exists (select 1 from public.rate_limits where bucket = 'pc_login_start'
             and key = '20000000-0000-0000-0000-0000000005c1') then
    raise exception 'FAIL start_unsigned_not_counted';
  end if;
  for i in 1..3 loop
    s := pg_temp.st('20000000-0000-0000-0000-0000000005c1', pg_temp.spki(), 'R', 'rs-d' || i, 'rs-u' || i, pg_temp.spki());
    if s <> 'ok' then raise exception 'FAIL start_3_live_ok (%: %)', i, s; end if;
  end loop;
  s := pg_temp.st('20000000-0000-0000-0000-0000000005c1', pg_temp.spki(), 'R', 'rs-dx', 'rs-ux', pg_temp.spki());
  if s <> 'rate.limited' then raise exception 'FAIL start_4th_live_limited (%)', s; end if;
  -- Codes 2 and 3 run out; slots free up, the per-pc counter still applies.
  update public.pc_login_codes set expires_at = now() - interval '1 second'
   where device_code_hash in (pg_temp.h('rs-d2'), pg_temp.h('rs-d3'));
  s := pg_temp.st('20000000-0000-0000-0000-0000000005c1', pg_temp.spki(), 'R', 'rs-d5', 'rs-u5', pg_temp.spki());
  if s <> 'ok' then raise exception 'FAIL start_5th_ok (%)', s; end if;
  update public.pc_login_codes set expires_at = now() - interval '1 second'
   where device_code_hash = pg_temp.h('rs-d5');
  s := pg_temp.st('20000000-0000-0000-0000-0000000005c1', pg_temp.spki(), 'R', 'rs-d6', 'rs-u6', pg_temp.spki());
  if s <> 'rate.limited' then raise exception 'FAIL start_6th_rate_limited (%)', s; end if;
  if (select count(*) from public.pc_login_codes where pc_id = '20000000-0000-0000-0000-0000000005c1') <> 4 then
    raise exception 'FAIL start_limited_no_row';
  end if;
  insert into _results values ('start_unsigned_first_binding_refused'), ('start_unsigned_not_counted'),
    ('start_3_live_ok'), ('start_4th_live_limited'), ('start_5th_ok'), ('start_6th_rate_limited'),
    ('start_limited_no_row');
end $$;
reset role;

-- Signed-request freshness: ts within +-300 s, each nonce once (start and consume share it).
set role service_role;
do $$ declare n bytea := pg_temp.nn(); s text; r record; begin
  s := public.pc_login_start('20000000-0000-0000-0000-0000000005c2', pg_temp.spki(), 'N',
    pg_temp.h('nc-d1'), pg_temp.h('nc-u1'), pg_temp.spki(), n, now() - interval '299 seconds');
  if s <> 'ok' then raise exception 'FAIL ts_within_window_ok (%)', s; end if;
  s := public.pc_login_start('20000000-0000-0000-0000-0000000005c2', pg_temp.spki(), 'N',
    pg_temp.h('nc-d2'), pg_temp.h('nc-u2'), pg_temp.spki(), n, now());
  if s <> 'nonce.replay' then raise exception 'FAIL start_nonce_replay (%)', s; end if;
  select * into r from public.pc_login_consume(pg_temp.h('nc-d1'), pg_temp.rh(), pg_temp.spki(), n, now());
  if r.status <> 'nonce.replay' then raise exception 'FAIL consume_nonce_replay (%)', r; end if;
  s := public.pc_login_start('20000000-0000-0000-0000-0000000005c2', pg_temp.spki(), 'N',
    pg_temp.h('nc-d3'), pg_temp.h('nc-u3'), pg_temp.spki(), pg_temp.nn(), now() - interval '301 seconds');
  if s <> 'request.stale' then raise exception 'FAIL start_ts_old_stale (%)', s; end if;
  s := public.pc_login_start('20000000-0000-0000-0000-0000000005c2', pg_temp.spki(), 'N',
    pg_temp.h('nc-d4'), pg_temp.h('nc-u4'), pg_temp.spki(), pg_temp.nn(), now() + interval '301 seconds');
  if s <> 'request.stale' then raise exception 'FAIL start_ts_future_stale (%)', s; end if;
  select * into r from public.pc_login_consume(pg_temp.h('nc-d1'), pg_temp.rh(), pg_temp.spki(), pg_temp.nn(),
    now() - interval '301 seconds');
  if r.status <> 'request.stale' then raise exception 'FAIL consume_ts_stale (%)', r; end if;
  s := public.pc_login_start('20000000-0000-0000-0000-0000000005c2', pg_temp.spki(), 'N',
    pg_temp.h('nc-d5'), pg_temp.h('nc-u5'), pg_temp.spki(), null, now());
  if s <> 'request.stale' then raise exception 'FAIL start_no_nonce_refused (%)', s; end if;
  if (select count(*) from public.pc_login_codes where pc_id = '20000000-0000-0000-0000-0000000005c2') <> 1 then
    raise exception 'FAIL freshness_refused_no_row';
  end if;
  insert into _results values ('ts_within_window_ok'), ('start_nonce_replay'), ('consume_nonce_replay'),
    ('start_ts_old_stale'), ('start_ts_future_stale'), ('consume_ts_stale'), ('start_no_nonce_refused'),
    ('freshness_refused_no_row');
end $$;
reset role;
-- Replay across transactions (the nonce row is committed).
select public.pc_login_start('20000000-0000-0000-0000-0000000005c3', pg_temp.spki(), 'N',
  pg_temp.h('nc2-d1'), pg_temp.h('nc2-u1'), pg_temp.spki(), '\x000102030405060708090a0b0c0d0e0f', now()) \g /dev/null
do $$ begin
  if public.pc_login_start('20000000-0000-0000-0000-0000000005c3', pg_temp.spki(), 'N',
       pg_temp.h('nc2-d2'), pg_temp.h('nc2-u2'), pg_temp.spki(), '\x000102030405060708090a0b0c0d0e0f', now())
     <> 'nonce.replay' then
    raise exception 'FAIL nonce_replay_next_tx';
  end if;
  insert into _results values ('nonce_replay_next_tx');
end $$;

-- Poll rate: second poll within 5 s -> slow_down; 151st poll voids the code.
set role service_role;
select pg_temp.st('20000000-0000-0000-0000-0000000005c4', pg_temp.spki(), 'Poll', 'pl-d', 'PL-U', pg_temp.spki()) \g /dev/null
select status from public.pc_login_consume(pg_temp.h('pl-d'), pg_temp.rh(), pg_temp.spki(), pg_temp.nn(), now()) \g /dev/null
do $$ begin
  if (select status from public.pc_login_consume(pg_temp.h('pl-d'), pg_temp.rh(), pg_temp.spki(), pg_temp.nn(), now()))
     <> 'slow_down' then
    raise exception 'FAIL poll_within_5s_slow_down';
  end if;
  insert into _results values ('poll_within_5s_slow_down');
end $$;
reset role;
update public.pc_login_codes set attempts = 149 where device_code_hash = pg_temp.h('pl-d');
set role service_role;
do $$ begin
  if (select status from pg_temp.co('pl-d', pg_temp.spki())) <> 'pending' then
    raise exception 'FAIL poll_150_allowed';
  end if;
  if (select status from pg_temp.co('pl-d', pg_temp.spki())) <> 'code.invalid' then
    raise exception 'FAIL poll_151_invalid';
  end if;
  insert into _results values ('poll_150_allowed'), ('poll_151_invalid');
end $$;
reset role;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ begin
  if (select status from public.pc_login_describe(pg_temp.h('PL-U'))) <> 'code.invalid' then
    raise exception 'FAIL poll_cap_voids_code';
  end if;
  insert into _results values ('poll_cap_voids_code');
end $$;
reset role;

-- display_name: Cc and Cf characters (controls, bidi, zero-width, tags) are rejected.
set role service_role;
do $$ declare bad text; i int := 0; begin
  foreach bad in array array['PC' || chr(7), 'PC' || chr(27) || '[2J', chr(8238) || 'cp.exe',
      'P' || chr(8203) || 'C', chr(65279) || 'PC', 'PC' || chr(917569), 'P' || chr(173) || 'C',
      'PC' || chr(8294), 'PC' || chr(127), 'PC' || chr(133),
      'PC' || chr(8232) || 'x', 'PC' || chr(8233) || 'x'] loop
    i := i + 1;
    begin
      perform pg_temp.st(gen_random_uuid(), pg_temp.spki(), bad, 'bad-d' || i, 'bad-u' || i, pg_temp.spki());
      raise exception 'FAIL display_name_rejects_cc_cf (%)', i;
    exception when check_violation then null;
    end;
  end loop;
  if pg_temp.st(gen_random_uuid(), pg_temp.spki(), 'Мой ПК 🎮 — Dev', 'good-d', 'good-u', pg_temp.spki()) <> 'ok' then
    raise exception 'FAIL display_name_allows_unicode';
  end if;
  insert into _results values ('display_name_rejects_cc_cf'), ('display_name_allows_unicode');
end $$;
reset role;

-- Approve rate limit: failures count, describe and approve share it; the 11th
-- call is rate.limited even for a valid code. Each statement is its own
-- transaction, like separate API calls.
truncate public.rate_limits;
set role service_role;
select pg_temp.st('20000000-0000-0000-0000-0000000005d1', pg_temp.spki(), 'Valid', 'rl-dev', 'RL-VALID', pg_temp.spki()) \g /dev/null
-- RL-VALID is described by B below and cannot be taken over (E3-5d N5), so
-- user A approves a separate code.
select pg_temp.st('20000000-0000-0000-0000-0000000005d3', pg_temp.spki(), 'Other', 'rl-dev-a', 'RL-OTHER', pg_temp.spki()) \g /dev/null
reset role;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005b1') \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-1')) \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-2')) \g /dev/null
select public.pc_login_describe(pg_temp.h('guess-3')) \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-4')) \g /dev/null
select public.pc_login_describe(pg_temp.h('guess-5')) \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-6')) \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-7')) \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-8')) \g /dev/null
select public.pc_login_approve(pg_temp.h('guess-9')) \g /dev/null
do $$ begin
  if (select status from public.pc_login_describe(pg_temp.h('RL-VALID'))) <> 'ok' then
    raise exception 'FAIL approve_10th_still_checked';
  end if;
  insert into _results values ('approve_10th_still_checked');
end $$;
do $$ begin
  if public.pc_login_approve(pg_temp.h('RL-VALID')) <> 'rate.limited' then raise exception 'FAIL approve_11th_rate_limited'; end if;
  if (select status from public.pc_login_describe(pg_temp.h('RL-VALID'))) <> 'rate.limited' then
    raise exception 'FAIL describe_rate_limited';
  end if;
  insert into _results values ('approve_11th_rate_limited'), ('describe_rate_limited');
end $$;
-- Another user is not affected by B's limit.
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ begin
  if pg_temp.ok('RL-OTHER') <> 'ok' then raise exception 'FAIL rate_limit_per_user'; end if;
  insert into _results values ('rate_limit_per_user');
end $$;
reset role;

-- Global describe/approve limit: 10000 per 10 minutes across all users.
truncate public.rate_limits;
insert into public.rate_limits
  select 'pc_login_approve_global', 'all',
         to_timestamp(floor(extract(epoch from now()) / 600) * 600 + d), 10000
    from (values (0), (600)) v(d);
set role service_role;
select pg_temp.st('20000000-0000-0000-0000-0000000005d2', pg_temp.spki(), 'G', 'gl-dev', 'GL-U', pg_temp.spki()) \g /dev/null
reset role;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ begin
  if (select status from public.pc_login_describe(pg_temp.h('GL-U'))) <> 'rate.limited'
     or public.pc_login_approve(pg_temp.h('GL-U')) <> 'rate.limited' then
    raise exception 'FAIL global_approve_limit';
  end if;
  insert into _results values ('global_approve_limit');
end $$;
reset role;
do $$ begin
  if (select described_by from public.pc_login_codes where user_code_hash = pg_temp.h('GL-U')) is not null then
    raise exception 'FAIL global_limit_no_effect';
  end if;
  insert into _results values ('global_limit_no_effect');
end $$;

-- E3-5d N1: a user over the own limit does not hit the global counter.
-- 9999 global hits so far; B burns 10 own + 1000 more calls; A still approves.
truncate public.rate_limits;
insert into public.rate_limits
  select 'pc_login_approve_global', 'all',
         to_timestamp(floor(extract(epoch from now()) / 600) * 600 + d), 9989
    from (values (0), (600)) v(d);
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005b1') \g /dev/null
do $$ declare i int; begin
  for i in 1..1010 loop
    perform public.pc_login_approve(pg_temp.h('flood-' || i));
  end loop;
  if public.pc_login_approve(pg_temp.h('flood-x')) <> 'rate.limited' then
    raise exception 'FAIL flood_user_limited';
  end if;
end $$;
reset role;
do $$ begin
  if (select max(count) from public.rate_limits where bucket = 'pc_login_approve_global') <> 9999 then
    raise exception 'FAIL global_not_hit_over_user_limit (%)',
      (select max(count) from public.rate_limits where bucket = 'pc_login_approve_global');
  end if;
  insert into _results values ('global_not_hit_over_user_limit');
end $$;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ begin
  if (select status from public.pc_login_describe(pg_temp.h('GL-U'))) <> 'ok' then
    raise exception 'FAIL other_user_approves_after_flood';
  end if;
  insert into _results values ('other_user_approves_after_flood');
end $$;
reset role;

-- E3-5d N5: describe by another user does not take over described_by.
truncate public.rate_limits;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005b1') \g /dev/null
do $$ declare r text; begin
  select x::text into r from public.pc_login_describe(pg_temp.h('GL-U')) x;
  if r <> '(code.invalid,,)' then raise exception 'FAIL describe_no_takeover (%)', r; end if;
  if public.pc_login_approve(pg_temp.h('GL-U')) <> 'code.invalid' then
    raise exception 'FAIL approve_after_foreign_describe';
  end if;
end $$;
reset role;
do $$ begin
  if (select described_by from public.pc_login_codes where user_code_hash = pg_temp.h('GL-U'))
       <> '00000000-0000-0000-0000-0000000005a1'
     or (select sum(count) from public.rate_limits where bucket = 'pc_login_approve') <> 2 then
    raise exception 'FAIL describe_no_takeover_state';
  end if;
  insert into _results values ('describe_no_takeover'), ('approve_after_foreign_describe');
end $$;
-- The first describer can still describe again and approve.
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005a1') \g /dev/null
do $$ begin
  if pg_temp.ok('GL-U') <> 'ok' then raise exception 'FAIL first_describer_approves'; end if;
  insert into _results values ('first_describer_approves');
end $$;
reset role;

-- E3-5d: nonce longer than 32 bytes is refused (Edge limit mirrored in SQL).
set role service_role;
do $$ begin
  if public.pc_login_start(gen_random_uuid(), pg_temp.spki(), 'N', pg_temp.h('n33-d'), pg_temp.h('N33-U'),
       pg_temp.spki(), decode(repeat('ab', 33), 'hex'), now()) <> 'request.stale'
     or public.pc_login_start(gen_random_uuid(), pg_temp.spki(), 'N', pg_temp.h('n32-d'), pg_temp.h('N32-U'),
       pg_temp.spki(), decode(repeat('ab', 32), 'hex'), now()) <> 'ok' then
    raise exception 'FAIL nonce_max_32_bytes';
  end if;
  insert into _results values ('nonce_max_32_bytes');
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
  c := public.pc_login_approve(pg_temp.h('RL-OTHER'));   -- already approved
  d := public.pc_login_approve(pg_temp.h('USER-1'));     -- consumed
  if a <> 'code.invalid' or b <> a or c <> a or d <> a then
    raise exception 'FAIL approve_no_existence_oracle (% % % %)', a, b, c, d;
  end if;
  insert into _results values ('approve_no_existence_oracle');
end $$;
do $$ declare r text[]; begin
  select array_agg(x::text) into r from (
    select public.pc_login_describe(pg_temp.h(c)) x
      from unnest(array['never-issued', 'USER-3', 'RL-OTHER', 'USER-1']) c) s;
  if r <> array_fill('(code.invalid,,)'::text, array[4]) then
    raise exception 'FAIL describe_no_existence_oracle (%)', r;
  end if;
  insert into _results values ('describe_no_existence_oracle');
end $$;
reset role;
do $$ begin
  if (select sum(count) from public.rate_limits where bucket = 'pc_login_approve') <> 8
     or (select sum(count) from public.rate_limits where bucket = 'pc_login_approve_global') <> 8 then
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
select pg_temp.st('20000000-0000-0000-0000-0000000005e9', pg_temp.spki(), 'Extra', 'lim-dev', 'LIM-USER', pg_temp.spki()) \g /dev/null
reset role;
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-0000000005e1') \g /dev/null
select pg_temp.ok('LIM-USER') \g /dev/null
reset role;
set role service_role;
do $$ begin
  if (select status from pg_temp.co('lim-dev', pg_temp.spki())) <> 'pcs.limit' then
    raise exception 'FAIL consume_respects_20_limit';
  end if;
  insert into _results values ('consume_respects_20_limit');
end $$;
reset role;
delete from public.my_pcs where pc_id = '20000000-0000-0000-0000-0000000e0001';
set role service_role;
do $$ begin
  if (select status from pg_temp.co('lim-dev', pg_temp.spki())) <> 'ok' then
    raise exception 'FAIL consume_after_freeing_slot';
  end if;
  if exists (select 1 from public.pc_keys where pc_id = '20000000-0000-0000-0000-0000000e0001') then
    raise exception 'FAIL pc_keys_cascade';
  end if;
  insert into _results values ('consume_after_freeing_slot'), ('pc_keys_cascade');
end $$;
reset role;

-- cleanup(): removes expired/consumed codes, old nonces and rate rows, keeps live ones.
insert into public.rate_limits values ('x', 'old', now() - interval '2 days', 1);
insert into public.pc_login_nonces values ('\xffffffffffffffffffffffffffffffff', now() - interval '2 hours');
set role service_role;
select public.cleanup() \g /dev/null
reset role;
do $$ begin
  if exists (select 1 from public.pc_login_codes where expires_at <= now() or consumed_at is not null)
     or exists (select 1 from public.rate_limits where key = 'old')
     or exists (select 1 from public.pc_login_nonces where created_at < now() - interval '1 hour') then
    raise exception 'FAIL cleanup_removes_stale';
  end if;
  if not exists (select 1 from public.pc_login_codes where device_code_hash = pg_temp.h('rs-d1'))
     or not exists (select 1 from public.rate_limits where bucket = 'pc_login_approve')
     or not exists (select 1 from public.pc_login_nonces) then
    raise exception 'FAIL cleanup_keeps_live';
  end if;
  insert into _results values ('cleanup_removes_stale'), ('cleanup_keeps_live');
end $$;

select 'PASSED ' || count(*) from _results;
