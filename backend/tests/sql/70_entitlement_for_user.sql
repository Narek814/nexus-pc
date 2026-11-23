-- Tests for entitlement_for_user, the refactored entitlement_for_pc and the
-- Edge read helpers pc_key_get / pc_login_code_key (migration 000007).
-- Each check raises with its name on failure.
\set ON_ERROR_STOP on

create temp table _results (name text);
grant insert on _results to authenticated, anon, service_role;

-- u71 Pro, u72 no subscription, u73 refunded, u74 revoked after expired,
-- u75 period ended. PCs 7100..7105 owned by u71..u75.
insert into auth.users (id) select ('71000000-0000-0000-0000-00000000000' || i)::uuid
  from generate_series(1, 5) i;
set role service_role;
insert into public.my_pcs (pc_id, user_id, display_name, platform)
  select ('72000000-0000-0000-0000-00000000000' || i)::uuid,
         ('71000000-0000-0000-0000-00000000000' || i)::uuid, 'PC7 ' || i, 'windows'
  from generate_series(1, 5) i;
insert into public.subscriptions (user_id, source, status, product, current_period_end, updated_at) values
  ('71000000-0000-0000-0000-000000000001', 'manual', 'active',   'pro_monthly', now() + interval '20 days', now()),
  ('71000000-0000-0000-0000-000000000003', 'web',    'refunded', 'pro_monthly', now() + interval '20 days', now()),
  ('71000000-0000-0000-0000-000000000004', 'web',    'expired',  'pro_monthly', now() - interval '40 days', now() - interval '40 days'),
  ('71000000-0000-0000-0000-000000000004', 'web',    'revoked',  'pro_monthly', now() + interval '3 days',  now()),
  ('71000000-0000-0000-0000-000000000005', 'web',    'active',   'pro_monthly', now() - interval '1 second', now());
insert into public.pc_keys (pc_id, public_key_spki)
  values ('72000000-0000-0000-0000-000000000001', decode(repeat('ab', 91), 'hex'));
insert into public.pc_login_codes (pc_id, public_key_spki, display_name, device_code_hash, user_code_hash, consumed_at, expires_at) values
  ('7fffffff-0000-0000-0000-000000000001', decode(repeat('cd', 91), 'hex'), 'Live',
     sha256('dc-live'), sha256('uc-live'), null, now() + interval '5 minutes'),
  ('7fffffff-0000-0000-0000-000000000002', decode(repeat('ce', 91), 'hex'), 'Used',
     sha256('dc-used'), sha256('uc-used'), now(), now() + interval '5 minutes'),
  ('7fffffff-0000-0000-0000-000000000003', decode(repeat('cf', 91), 'hex'), 'Expired',
     sha256('dc-exp'), sha256('uc-exp'), null, now() - interval '1 second');
reset role;

-- Truth table of entitlement_for_user, and entitlement_for_pc equals it for
-- the owner of every PC (all PCs of all test files, so 000003 behaviour is kept).
set role service_role;
do $$
declare
  pro text[] := array['profiles','rgb.sync','automations','monitoring.extended','files.unlimited'];
  r record;
  c record;
begin
  for c in select * from (values
      ('user_pro',             '71000000-0000-0000-0000-000000000001', 'active',   'pro',  pro),
      ('user_free',            '71000000-0000-0000-0000-000000000002', 'active',   'free', '{}'::text[]),
      ('user_refunded_revoked','71000000-0000-0000-0000-000000000003', 'revoked',  'free', '{}'::text[]),
      ('user_latest_revoked',  '71000000-0000-0000-0000-000000000004', 'revoked',  'free', '{}'::text[]),
      ('user_period_ended',    '71000000-0000-0000-0000-000000000005', 'active',   'free', '{}'::text[]),
      ('user_unknown_free',    '7eeeeeee-0000-0000-0000-000000000000', 'active',   'free', '{}'::text[])
    ) v(name, u, st, pl, ft)
  loop
    select * into r from public.entitlement_for_user(c.u::uuid);
    if r.state is distinct from c.st or r.plan is distinct from c.pl
       or r.features is distinct from c.ft then
      raise exception 'FAIL % (got %, %, %)', c.name, r.state, r.plan, r.features;
    end if;
    insert into _results values (c.name);
  end loop;

  select * into r from public.entitlement_for_user(null);
  if r.state is distinct from 'unlinked' or r.plan is distinct from 'free' or r.features <> '{}' then
    raise exception 'FAIL user_null_unlinked';
  end if;
  insert into _results values ('user_null_unlinked');

  if (select count(*) from public.entitlement_for_user('71000000-0000-0000-0000-000000000001')) <> 1 then
    raise exception 'FAIL user_one_row';
  end if;
  insert into _results values ('user_one_row');
end $$;
reset role;

do $$
declare n int;
begin
  select count(*) into n from public.my_pcs p
   cross join lateral public.entitlement_for_pc(p.pc_id) a
   cross join lateral public.entitlement_for_user(p.user_id) b
   where (a.state, a.plan, a.features) is distinct from (b.state, b.plan, b.features);
  if n <> 0 or (select count(*) from public.my_pcs) < 10 then
    raise exception 'FAIL pc_equals_owner (% differ)', n;
  end if;
  insert into _results values ('pc_equals_owner');
  if (select row(state, plan, features)::text from public.entitlement_for_pc('7ddddddd-0000-0000-0000-000000000000'))
     is distinct from '(unlinked,free,{})' then
    raise exception 'FAIL pc_unlinked_kept';
  end if;
  insert into _results values ('pc_unlinked_kept');
end $$;

-- Catalog: security definer, search_path = '', signatures unchanged.
do $$
declare
  f text;
begin
  foreach f in array array['public.entitlement_for_user(uuid)', 'public.entitlement_for_pc(uuid)',
                           'public.pc_key_get(uuid)', 'public.pc_login_code_key(bytea)'] loop
    if not exists (select 1 from pg_proc where oid = f::regprocedure and prosecdef
                     and proconfig = array['search_path=""']) then
      raise exception 'FAIL definer_search_path %', f;
    end if;
  end loop;
  insert into _results values ('definer_search_path');
  if pg_get_function_result('public.entitlement_for_pc(uuid)'::regprocedure)
       <> 'TABLE(state text, plan text, features text[])'
     or pg_get_function_result('public.entitlement_for_user(uuid)'::regprocedure)
       <> 'TABLE(state text, plan text, features text[])' then
    raise exception 'FAIL result_signature';
  end if;
  insert into _results values ('result_signature');
end $$;

-- Execute only for service_role (catalog view of the grants).
do $$
declare
  f text;
  r text;
begin
  foreach f in array array['public.entitlement_for_user(uuid)', 'public.entitlement_for_pc(uuid)',
                           'public.pc_key_get(uuid)', 'public.pc_login_code_key(bytea)'] loop
    if not has_function_privilege('service_role', f, 'execute') then
      raise exception 'FAIL service_role_execute %', f;
    end if;
    foreach r in array array['anon', 'authenticated', 'public'] loop
      if exists (select 1 from pg_proc p, aclexplode(p.proacl) a
                  where p.oid = f::regprocedure and a.privilege_type = 'EXECUTE'
                    and a.grantee = case when r = 'public' then 0 else r::regrole::oid end) then
        raise exception 'FAIL execute_granted % %', r, f;
      end if;
    end loop;
  end loop;
  insert into _results values ('service_role_execute'), ('no_execute_anon_authenticated_public');
end $$;

-- Calls by anon and authenticated are refused.
set role authenticated;
select set_config('request.jwt.claims', '{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}', false) \g /dev/null
do $$ begin
  begin
    perform public.entitlement_for_user('71000000-0000-0000-0000-000000000001');
    raise exception 'FAIL authenticated_user_fn_denied';
  exception when insufficient_privilege then null;
  end;
  begin
    perform public.pc_key_get('72000000-0000-0000-0000-000000000001');
    raise exception 'FAIL authenticated_key_get_denied';
  exception when insufficient_privilege then null;
  end;
  begin
    perform public.pc_login_code_key(sha256('dc-live'));
    raise exception 'FAIL authenticated_code_key_denied';
  exception when insufficient_privilege then null;
  end;
  begin
    perform public.entitlement_for_pc('72000000-0000-0000-0000-000000000001');
    raise exception 'FAIL authenticated_pc_fn_denied';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('authenticated_denied');
end $$;
reset role;
set role anon;
do $$ begin
  begin
    perform public.entitlement_for_user('71000000-0000-0000-0000-000000000001');
    raise exception 'FAIL anon_user_fn_denied';
  exception when insufficient_privilege then null;
  end;
  begin
    perform public.pc_key_get('72000000-0000-0000-0000-000000000001');
    raise exception 'FAIL anon_key_get_denied';
  exception when insufficient_privilege then null;
  end;
  begin
    perform public.pc_login_code_key(sha256('dc-live'));
    raise exception 'FAIL anon_code_key_denied';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('anon_denied');
end $$;
reset role;

-- Read helpers as service_role.
set role service_role;
do $$
declare r record;
begin
  if public.pc_key_get('72000000-0000-0000-0000-000000000001') is distinct from decode(repeat('ab', 91), 'hex') then
    raise exception 'FAIL key_get_bound';
  end if;
  if public.pc_key_get('72000000-0000-0000-0000-000000000002') is not null then
    raise exception 'FAIL key_get_no_key';
  end if;
  if public.pc_key_get('7ddddddd-0000-0000-0000-000000000000') is not null then
    raise exception 'FAIL key_get_unknown';
  end if;
  insert into _results values ('key_get_bound'), ('key_get_null');

  select * into r from public.pc_login_code_key(sha256('dc-live'));
  if r.pc_id is distinct from '7fffffff-0000-0000-0000-000000000001'
     or r.public_key_spki is distinct from decode(repeat('cd', 91), 'hex') then
    raise exception 'FAIL code_key_live';
  end if;
  insert into _results values ('code_key_live');
  if exists (select 1 from public.pc_login_code_key(sha256('dc-used')))
     or exists (select 1 from public.pc_login_code_key(sha256('dc-exp')))
     or exists (select 1 from public.pc_login_code_key(sha256('dc-none')))
     or exists (select 1 from public.pc_login_code_key(sha256('uc-live'))) then
    raise exception 'FAIL code_key_dead_none';
  end if;
  insert into _results values ('code_key_dead_none');
end $$;
reset role;

select 'PASSED ' || count(*) from _results;
