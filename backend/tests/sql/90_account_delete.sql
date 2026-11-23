-- Tests for account_purge (E3-8): all rows of user A gone, user B untouched,
-- former PCs of A get 'revoked', idempotent, service_role only.
\set ON_ERROR_STOP on

insert into auth.users (id, email) values
  ('00000000-0000-0000-0000-0000000009a1', 'da@example.test'),
  ('00000000-0000-0000-0000-0000000009b1', 'db@example.test');

create temp table _results (name text);
grant insert on _results to authenticated, anon, service_role;

create function pg_temp.h(t text) returns bytea language sql immutable
  as $$ select sha256(convert_to(t, 'UTF8')) $$;
create function pg_temp.k(n int) returns bytea language sql immutable
  as $$ select decode(repeat(to_hex(48 + n), 91), 'hex') $$;
create function pg_temp.nn() returns bytea language sql volatile
  as $$ select uuid_send(gen_random_uuid()) $$;
-- Full sign-in of a PC by a user (as in 60_pc_refresh.sql), refresh r0.
create function pg_temp.bind(pc uuid, key bytea, usr uuid, tag text, r0 text)
  returns text language plpgsql volatile as $$
declare s text;
begin
  set local role service_role;
  s := public.pc_login_start(pc, key, 'PC ' || tag, pg_temp.h('d-' || tag), pg_temp.h('U-' || tag),
         key, pg_temp.nn(), now());
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
-- Rows of a user across every table that can hold user data.
create function pg_temp.user_rows(u uuid) returns bigint language sql stable as $$
  select (select count(*) from public.profiles where id = u)
       + (select count(*) from public.subscriptions where user_id = u)
       + (select count(*) from public.my_pcs where user_id = u)
       + (select count(*) from public.pc_keys k join public.my_pcs m using (pc_id) where m.user_id = u)
       + (select count(*) from public.pc_refresh_tokens t
           where t.pc_id in ('00000000-0000-4000-8000-0000000009a1', '00000000-0000-4000-8000-0000000009a2'))
       + (select count(*) from public.pc_keys
           where pc_id in ('00000000-0000-4000-8000-0000000009a1', '00000000-0000-4000-8000-0000000009a2'))
       + (select count(*) from public.pc_login_codes
           where described_by = u or approved_by = u
              or pc_id in ('00000000-0000-4000-8000-0000000009a1', '00000000-0000-4000-8000-0000000009a2'))
       + (select count(*) from public.rate_limits where key = u::text)
$$;
-- Fingerprint of everything that belongs to user B.
create function pg_temp.b_snapshot() returns text language sql stable as $$
  select md5(concat_ws('|',
    (select string_agg(p::text, ',' order by p.id) from public.profiles p
      where id = '00000000-0000-0000-0000-0000000009b1'),
    (select string_agg(s::text, ',' order by s.id) from public.subscriptions s
      where user_id = '00000000-0000-0000-0000-0000000009b1'),
    (select string_agg(m::text, ',' order by m.pc_id) from public.my_pcs m
      where user_id = '00000000-0000-0000-0000-0000000009b1'),
    (select string_agg(k::text, ',' order by k.pc_id) from public.pc_keys k
      where pc_id = '00000000-0000-4000-8000-0000000009b1'),
    (select string_agg(t::text, ',' order by t.id) from public.pc_refresh_tokens t
      where pc_id = '00000000-0000-4000-8000-0000000009b1'),
    (select string_agg(l::text, ',' order by l.id) from public.pc_login_codes l
      where pc_id in ('00000000-0000-4000-8000-0000000009b1', '00000000-0000-4000-8000-0000000009b2')),
    (select string_agg(r::text, ',' order by r.bucket, r.window_start) from public.rate_limits r
      where key = '00000000-0000-0000-0000-0000000009b1'),
    (select string_agg(e::text, ',' order by e.event_id) from public.payment_events e)))
$$;
grant execute on all functions in schema pg_temp to authenticated, anon, service_role;

-- Catalog: service_role only, definer with empty search_path.
do $$ begin
  if has_function_privilege('anon', 'public.account_purge(uuid)', 'execute')
     or has_function_privilege('authenticated', 'public.account_purge(uuid)', 'execute')
     or not has_function_privilege('service_role', 'public.account_purge(uuid)', 'execute') then
    raise exception 'FAIL purge_service_only';
  end if;
  if not exists (select 1 from pg_proc where oid = 'public.account_purge(uuid)'::regprocedure
                  and prosecdef and proconfig @> array['search_path=""']) then
    raise exception 'FAIL purge_definer_empty_search_path';
  end if;
  insert into _results values ('purge_service_only');
end $$;

-- Data: A has two PCs, Pro subscription, a code described (not approved) for
-- a third PC and an approve rate-limit row; B has one PC, a subscription and
-- a pending code. payment_events has a row (kept).
do $$ declare s text; begin
  s := pg_temp.bind('00000000-0000-4000-8000-0000000009a1', pg_temp.k(1),
    '00000000-0000-0000-0000-0000000009a1', 'a1', 'ra1');
  if s <> 'ok' then raise exception 'FAIL setup a1 %', s; end if;
  s := pg_temp.bind('00000000-0000-4000-8000-0000000009a2', pg_temp.k(2),
    '00000000-0000-0000-0000-0000000009a1', 'a2', 'ra2');
  if s <> 'ok' then raise exception 'FAIL setup a2 %', s; end if;
  s := pg_temp.bind('00000000-0000-4000-8000-0000000009b1', pg_temp.k(3),
    '00000000-0000-0000-0000-0000000009b1', 'b1', 'rb1');
  if s <> 'ok' then raise exception 'FAIL setup b1 %', s; end if;
  -- Code for a third PC described by A only.
  set local role service_role;
  s := public.pc_login_start('00000000-0000-4000-8000-0000000009a3', pg_temp.k(4), 'PC a3',
    pg_temp.h('d-a3'), pg_temp.h('U-a3'), pg_temp.k(4), pg_temp.nn(), now());
  -- Pending code of B's second PC described by B.
  s := public.pc_login_start('00000000-0000-4000-8000-0000000009b2', pg_temp.k(5), 'PC b2',
    pg_temp.h('d-b2'), pg_temp.h('U-b2'), pg_temp.k(5), pg_temp.nn(), now());
  perform set_config('request.jwt.claims',
    json_build_object('sub', '00000000-0000-0000-0000-0000000009a1', 'role', 'authenticated')::text, true);
  set local role authenticated;
  perform public.pc_login_describe(pg_temp.h('U-a3'));
  perform set_config('request.jwt.claims',
    json_build_object('sub', '00000000-0000-0000-0000-0000000009b1', 'role', 'authenticated')::text, true);
  perform public.pc_login_describe(pg_temp.h('U-b2'));
  reset role;
  insert into public.subscriptions (user_id, source, status, product, current_period_end) values
    ('00000000-0000-0000-0000-0000000009a1', 'manual', 'active', 'pro_monthly', now() + interval '20 days'),
    ('00000000-0000-0000-0000-0000000009b1', 'manual', 'active', 'pro_yearly', now() + interval '200 days');
  insert into public.payment_events (provider, event_id) values ('test', 'evt-9');
  perform set_config('request.jwt.claims', '', false);
end $$;

create temp table _snap as select pg_temp.b_snapshot() as b, pg_temp.user_rows('00000000-0000-0000-0000-0000000009a1') as a,
  pg_temp.user_rows('00000000-0000-0000-0000-0000000009b1') as bn;

do $$ begin
  -- profile 1 + sub 1 + pcs 2 + keys 2(join) + tokens 2 + keys 2 + codes 3 + rate row 1
  if (select a from _snap) < 10 or (select bn from _snap) < 5 then
    raise exception 'FAIL setup_counts % %', (select a from _snap), (select bn from _snap);
  end if;
  if (select count(*) from public.rate_limits where key = '00000000-0000-0000-0000-0000000009a1') = 0 then
    raise exception 'FAIL setup_rate_row';
  end if;
  insert into _results values ('setup');
end $$;

-- anon / authenticated cannot call it (also for their own id).
do $$ begin
  perform set_config('request.jwt.claims',
    json_build_object('sub', '00000000-0000-0000-0000-0000000009a1', 'role', 'authenticated')::text, true);
  begin
    set local role authenticated;
    perform public.account_purge('00000000-0000-0000-0000-0000000009a1');
    raise exception 'FAIL purge_authenticated_denied';
  exception when insufficient_privilege then null;
  end;
  reset role;
  begin
    set local role anon;
    perform public.account_purge('00000000-0000-0000-0000-0000000009a1');
    raise exception 'FAIL purge_anon_denied';
  exception when insufficient_privilege then null;
  end;
  reset role;
  if pg_temp.user_rows('00000000-0000-0000-0000-0000000009a1') <> (select a from _snap) then
    raise exception 'FAIL purge_denied_no_change';
  end if;
  insert into _results values ('purge_api_roles_denied');
end $$;

-- Null user id is an error, nothing deleted.
do $$ begin
  begin
    set local role service_role;
    perform public.account_purge(null);
    raise exception 'FAIL purge_null';
  exception when invalid_parameter_value then null;
  end;
  reset role;
  insert into _results values ('purge_null_rejected');
end $$;

-- Purge A as service_role.
do $$ declare s text; begin
  set local role service_role;
  s := public.account_purge('00000000-0000-0000-0000-0000000009a1');
  reset role;
  if s <> 'ok' then raise exception 'FAIL purge_ok %', s; end if;
  insert into _results values ('purge_ok');
end $$;

do $$ begin
  if pg_temp.user_rows('00000000-0000-0000-0000-0000000009a1') <> 0 then
    raise exception 'FAIL purge_zero_rows %', pg_temp.user_rows('00000000-0000-0000-0000-0000000009a1');
  end if;
  insert into _results values ('purge_zero_rows');
  if (select count(*) from public.profiles where id = '00000000-0000-0000-0000-0000000009a1') <> 0 then
    raise exception 'FAIL purge_profile';
  end if;
  insert into _results values ('purge_profile');
  if (select count(*) from public.subscriptions where user_id = '00000000-0000-0000-0000-0000000009a1') <> 0 then
    raise exception 'FAIL purge_subscriptions';
  end if;
  insert into _results values ('purge_subscriptions');
  if (select count(*) from public.pc_login_codes where pc_id = '00000000-0000-4000-8000-0000000009a3') <> 0 then
    raise exception 'FAIL purge_described_code';
  end if;
  insert into _results values ('purge_described_code');
  if (select count(*) from public.rate_limits where key = '00000000-0000-0000-0000-0000000009a1') <> 0 then
    raise exception 'FAIL purge_rate_rows';
  end if;
  insert into _results values ('purge_rate_rows');
  if pg_temp.b_snapshot() <> (select b from _snap)
     or pg_temp.user_rows('00000000-0000-0000-0000-0000000009b1') <> (select bn from _snap) then
    raise exception 'FAIL purge_b_untouched';
  end if;
  insert into _results values ('purge_b_untouched');
  if (select count(*) from public.payment_events where event_id = 'evt-9') <> 1 then
    raise exception 'FAIL purge_payment_events_kept';
  end if;
  insert into _results values ('purge_payment_events_kept');
  -- auth.users row stays until the GoTrue admin delete.
  if (select count(*) from auth.users where id = '00000000-0000-0000-0000-0000000009a1') <> 1 then
    raise exception 'FAIL purge_auth_user_kept';
  end if;
  insert into _results values ('purge_auth_user_kept');
end $$;

-- Former PCs of A: rotation of their refresh -> 'revoked', entitlement unlinked.
do $$ declare s text; e text; begin
  set local role service_role;
  select status into s from public.pc_rotate_refresh(pg_temp.h('ra1'), pg_temp.h('ra1-n'),
    '00000000-0000-4000-8000-0000000009a1', pg_temp.k(1), pg_temp.nn(), now());
  if s <> 'revoked' then raise exception 'FAIL former_pc_revoked a1 %', s; end if;
  select status into s from public.pc_rotate_refresh(pg_temp.h('ra2'), pg_temp.h('ra2-n'),
    '00000000-0000-4000-8000-0000000009a2', pg_temp.k(2), pg_temp.nn(), now());
  if s <> 'revoked' then raise exception 'FAIL former_pc_revoked a2 %', s; end if;
  select state into e from public.entitlement_for_pc('00000000-0000-4000-8000-0000000009a1');
  if e <> 'unlinked' then raise exception 'FAIL former_pc_unlinked %', e; end if;
  -- B still rotates fine.
  select status into s from public.pc_rotate_refresh(pg_temp.h('rb1'), pg_temp.h('rb1-n'),
    '00000000-0000-4000-8000-0000000009b1', pg_temp.k(3), pg_temp.nn(), now());
  if s <> 'ok' then raise exception 'FAIL b_rotates %', s; end if;
  reset role;
  insert into _results values ('former_pcs_revoked');
end $$;

-- Idempotent: second purge (retry after failed admin delete) -> 'ok', B intact.
do $$ declare s text; before text; begin
  before := pg_temp.b_snapshot();
  set local role service_role;
  s := public.account_purge('00000000-0000-0000-0000-0000000009a1');
  reset role;
  if s <> 'ok' or pg_temp.user_rows('00000000-0000-0000-0000-0000000009a1') <> 0
     or pg_temp.b_snapshot() <> before then
    raise exception 'FAIL purge_idempotent';
  end if;
  insert into _results values ('purge_idempotent');
end $$;

-- After the auth user is deleted, a re-purge of the unknown id is still 'ok'.
do $$ declare s text; begin
  delete from auth.users where id = '00000000-0000-0000-0000-0000000009a1';
  set local role service_role;
  s := public.account_purge('00000000-0000-0000-0000-0000000009a1');
  reset role;
  if s <> 'ok' then raise exception 'FAIL purge_unknown_user'; end if;
  insert into _results values ('purge_unknown_user');
end $$;

select 'PASSED ' || count(*) from _results;
