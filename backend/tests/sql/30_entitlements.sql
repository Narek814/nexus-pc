-- Tests for public.subscriptions, public.payment_events, entitlement_for_pc.
-- Each check raises with its name on failure.
\set ON_ERROR_STOP on

-- u1 active pro, u2 no subscription, u3 canceled (period in future),
-- u4 active but period ended, u5 refunded, u6 owns a PC (u1 has Pro),
-- u7 revoked after an older expired one, u8 refunded then re-subscribed.
insert into auth.users (id) select ('00000000-0000-0000-0000-00000000000' || i)::uuid
  from generate_series(1, 8) i;

set role service_role;
insert into public.my_pcs (pc_id, user_id, display_name, platform)
  select ('20000000-0000-0000-0000-00000000000' || i)::uuid,
         ('00000000-0000-0000-0000-00000000000' || i)::uuid, 'PC ' || i, 'windows'
  from generate_series(1, 8) i;
insert into public.subscriptions (user_id, source, status, product, current_period_end, updated_at) values
  ('00000000-0000-0000-0000-000000000001', 'manual', 'active',   'pro_monthly', now() + interval '20 days', now()),
  ('00000000-0000-0000-0000-000000000003', 'web',    'canceled', 'pro_yearly',  now() + interval '5 days',  now()),
  ('00000000-0000-0000-0000-000000000004', 'web',    'active',   'pro_monthly', now() - interval '1 second', now()),
  ('00000000-0000-0000-0000-000000000005', 'appstore','refunded','pro_monthly', now() + interval '20 days', now()),
  ('00000000-0000-0000-0000-000000000007', 'web',    'expired',  'pro_monthly', now() - interval '40 days', now() - interval '40 days'),
  ('00000000-0000-0000-0000-000000000007', 'web',    'revoked',  'pro_monthly', now() + interval '3 days',  now()),
  ('00000000-0000-0000-0000-000000000008', 'web',    'refunded', 'pro_monthly', now() + interval '3 days',  now() - interval '1 day'),
  ('00000000-0000-0000-0000-000000000008', 'web',    'active',   'pro_yearly',  now() + interval '300 days', now());
reset role;

-- u9 active with period ending exactly now() (inserted later, in the checking transaction),
-- u10 active with NULL period end, u11/u12 refunded + expired with identical
-- updated_at and created_at: the tie is broken by id desc.
insert into auth.users (id) values
  ('00000000-0000-0000-0000-000000000009'), ('00000000-0000-0000-0000-000000000010'),
  ('00000000-0000-0000-0000-000000000011'), ('00000000-0000-0000-0000-000000000012');
set role service_role;
insert into public.my_pcs (pc_id, user_id, display_name, platform) values
  ('20000000-0000-0000-0000-000000000009', '00000000-0000-0000-0000-000000000009', 'PC 9',  'windows'),
  ('20000000-0000-0000-0000-000000000010', '00000000-0000-0000-0000-000000000010', 'PC 10', 'windows'),
  ('20000000-0000-0000-0000-000000000011', '00000000-0000-0000-0000-000000000011', 'PC 11', 'windows'),
  ('20000000-0000-0000-0000-000000000012', '00000000-0000-0000-0000-000000000012', 'PC 12', 'windows');
insert into public.subscriptions (id, user_id, source, status, product, current_period_end, created_at, updated_at) values
  ('30000000-0000-0000-0000-000000000010', '00000000-0000-0000-0000-000000000010', 'manual', 'active', 'pro_monthly', null, now(), now()),
  ('3fffffff-0000-0000-0000-000000000011', '00000000-0000-0000-0000-000000000011', 'web', 'refunded', 'pro_monthly', now() + interval '3 days', '2026-11-01 00:00+00', '2026-11-02 00:00+00'),
  ('30000000-0000-0000-0000-000000000011', '00000000-0000-0000-0000-000000000011', 'web', 'expired',  'pro_monthly', now() - interval '3 days', '2026-11-01 00:00+00', '2026-11-02 00:00+00'),
  ('30000000-0000-0000-0000-000000000012', '00000000-0000-0000-0000-000000000012', 'web', 'refunded', 'pro_monthly', now() + interval '3 days', '2026-11-01 00:00+00', '2026-11-02 00:00+00'),
  ('3fffffff-0000-0000-0000-000000000012', '00000000-0000-0000-0000-000000000012', 'web', 'expired',  'pro_monthly', now() - interval '3 days', '2026-11-01 00:00+00', '2026-11-02 00:00+00');
reset role;

create temp table _results (name text);
grant insert on _results to authenticated, anon, service_role;

-- Truth table, computed as service_role.
set role service_role;
do $$
declare
  pro text[] := array['profiles','rgb.sync','automations','monitoring.extended','files.unlimited','screen.video'];
  r record;
  c record;
begin
  for c in select * from (values
      ('pro_active',            '20000000-0000-0000-0000-000000000001', 'active',   'pro',  pro),
      ('no_subscription_free',  '20000000-0000-0000-0000-000000000002', 'active',   'free', '{}'::text[]),
      ('canceled_future_pro',   '20000000-0000-0000-0000-000000000003', 'active',   'pro',  pro),
      ('period_ended_free',     '20000000-0000-0000-0000-000000000004', 'active',   'free', '{}'::text[]),
      ('refunded_revoked',      '20000000-0000-0000-0000-000000000005', 'revoked',  'free', '{}'::text[]),
      ('owner_free_other_pro',  '20000000-0000-0000-0000-000000000006', 'active',   'free', '{}'::text[]),
      ('latest_revoked',        '20000000-0000-0000-0000-000000000007', 'revoked',  'free', '{}'::text[]),
      ('active_beats_refund',   '20000000-0000-0000-0000-000000000008', 'active',   'pro',  pro),
      ('unlinked_pc',           '2fffffff-0000-0000-0000-000000000000', 'unlinked', 'free', '{}'::text[])
    ) v(name, pc, st, pl, ft)
  loop
    select * into r from public.entitlement_for_pc(c.pc::uuid);
    if r.state is distinct from c.st or r.plan is distinct from c.pl
       or r.features is distinct from c.ft then
      raise exception 'FAIL % (got %, %, %)', c.name, r.state, r.plan, r.features;
    end if;
    insert into _results values (c.name);
  end loop;
end $$;

-- Edge cases (QA E3-3). One transaction, so now() at insert equals now() in the function.
begin;
insert into public.subscriptions (user_id, source, status, product, current_period_end)
  values ('00000000-0000-0000-0000-000000000009', 'manual', 'active', 'pro_monthly', now());
do $$
declare r record;
begin
  select * into r from public.entitlement_for_pc('20000000-0000-0000-0000-000000000009');
  if r.state is distinct from 'active' or r.plan is distinct from 'free' or r.features <> '{}' then
    raise exception 'FAIL period_end_equals_now_free (got %, %)', r.state, r.plan;
  end if;
  insert into _results values ('period_end_equals_now_free');
end $$;
commit;

do $$
declare r record;
begin
  select * into r from public.entitlement_for_pc('20000000-0000-0000-0000-000000000010');
  if r.state is distinct from 'active' or r.plan is distinct from 'free' or r.features <> '{}' then
    raise exception 'FAIL period_end_null_active_free (got %, %)', r.state, r.plan;
  end if;
  insert into _results values ('period_end_null_active_free');
end $$;

do $$
declare r record; i int;
begin
  for i in 1..5 loop
    select * into r from public.entitlement_for_pc('20000000-0000-0000-0000-000000000011');
    if r.state is distinct from 'revoked' or r.plan is distinct from 'free' then
      raise exception 'FAIL tie_broken_by_id_refunded (got %, %)', r.state, r.plan;
    end if;
    select * into r from public.entitlement_for_pc('20000000-0000-0000-0000-000000000012');
    if r.state is distinct from 'active' or r.plan is distinct from 'free' then
      raise exception 'FAIL tie_broken_by_id_expired (got %, %)', r.state, r.plan;
    end if;
  end loop;
  insert into _results values ('tie_broken_by_id_stable');
end $$;
reset role;

-- As user 1 (client).
set role authenticated;
select set_config('request.jwt.claims', '{"sub":"00000000-0000-0000-0000-000000000001","role":"authenticated"}', false) \g /dev/null

do $$ begin
  if (select count(*) from public.subscriptions) <> 1
     or exists (select 1 from public.subscriptions where user_id <> '00000000-0000-0000-0000-000000000001') then
    raise exception 'FAIL own_subscriptions_only';
  end if;
  insert into _results values ('own_subscriptions_only');
end $$;

do $$ begin
  begin
    insert into public.subscriptions (user_id, source, status, product)
      values ('00000000-0000-0000-0000-000000000001', 'manual', 'active', 'pro_yearly');
    raise exception 'FAIL insert_denied';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('insert_denied');
end $$;

do $$ begin
  begin
    update public.subscriptions set current_period_end = now() + interval '10 years';
    raise exception 'FAIL update_denied';
  exception when insufficient_privilege then null;
  end;
  begin
    delete from public.subscriptions;
    raise exception 'FAIL delete_denied';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('update_delete_denied');
end $$;

do $$ begin
  begin
    perform public.entitlement_for_pc('20000000-0000-0000-0000-000000000001');
    raise exception 'FAIL entitlement_fn_denied';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('entitlement_fn_denied');
end $$;

do $$ begin
  begin
    perform 1 from public.payment_events;
    raise exception 'FAIL payment_events_denied';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('payment_events_denied');
end $$;

-- User 2 sees nothing of user 1.
select set_config('request.jwt.claims', '{"sub":"00000000-0000-0000-0000-000000000002","role":"authenticated"}', false) \g /dev/null
do $$ begin
  if (select count(*) from public.subscriptions) <> 0 then
    raise exception 'FAIL other_user_sees_none';
  end if;
  insert into _results values ('other_user_sees_none');
end $$;
reset role;

-- anon: nothing.
set role anon;
do $$ begin
  begin
    perform 1 from public.subscriptions;
    raise exception 'FAIL anon_subscriptions_denied';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('anon_subscriptions_denied');
end $$;
reset role;

-- Idempotency: duplicate (provider, event_id) rejected.
set role service_role;
do $$ begin
  insert into public.payment_events (provider, event_id) values ('web', 'evt_1');
  begin
    insert into public.payment_events (provider, event_id) values ('web', 'evt_1');
    raise exception 'FAIL payment_event_unique';
  exception when unique_violation then null;
  end;
  insert into _results values ('payment_event_unique');
end $$;
reset role;

-- Catalog checks.
do $$ begin
  if not exists (select 1 from pg_proc where oid = 'public.entitlement_for_pc(uuid)'::regprocedure
      and prosecdef and proconfig @> array['search_path=""']) then
    raise exception 'FAIL fn_definer_empty_search_path';
  end if;
  if has_function_privilege('authenticated', 'public.entitlement_for_pc(uuid)', 'execute')
     or has_function_privilege('anon', 'public.entitlement_for_pc(uuid)', 'execute')
     or has_function_privilege('public', 'public.entitlement_for_pc(uuid)', 'execute')
     or not has_function_privilege('service_role', 'public.entitlement_for_pc(uuid)', 'execute') then
    raise exception 'FAIL fn_execute_service_role_only';
  end if;
  insert into _results values ('fn_catalog');
end $$;

do $$ begin
  if not (select relrowsecurity and relforcerowsecurity from pg_class where oid = 'public.subscriptions'::regclass)
     or not (select relrowsecurity and relforcerowsecurity from pg_class where oid = 'public.payment_events'::regclass) then
    raise exception 'FAIL rls_enabled_forced';
  end if;
  if (select count(*) from pg_policies where schemaname = 'public' and tablename = 'subscriptions') <> 1
     or (select count(*) from pg_policies where schemaname = 'public' and tablename = 'payment_events') <> 0 then
    raise exception 'FAIL policy_set';
  end if;
  insert into _results values ('rls_and_policies');
end $$;

do $$ begin
  if has_table_privilege('anon', 'public.subscriptions', 'select,insert,update,delete,truncate,references,trigger')
     or has_table_privilege('authenticated', 'public.subscriptions', 'insert,update,delete,truncate,references,trigger')
     or has_any_column_privilege('authenticated', 'public.subscriptions', 'insert,update,references')
     or has_any_column_privilege('anon', 'public.subscriptions', 'select,insert,update,references')
     or has_table_privilege('anon', 'public.payment_events', 'select,insert,update,delete,truncate,references,trigger')
     or has_table_privilege('authenticated', 'public.payment_events', 'select,insert,update,delete,truncate,references,trigger')
     or has_any_column_privilege('anon', 'public.payment_events', 'select,insert,update,references')
     or has_any_column_privilege('authenticated', 'public.payment_events', 'select,insert,update,references') then
    raise exception 'FAIL no_extra_grants';
  end if;
  insert into _results values ('no_extra_grants');
end $$;

select 'PASSED ' || count(*) from _results;
