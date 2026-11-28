-- BE-1: 20-PC limit checked already at approve; 'screen.video' in Pro features.
-- Replaces pc_login_approve (000005) and entitlement_for_user (000007).
-- create or replace keeps the existing grants.

-- Step 2 of approval. Returns 'ok' | 'code.invalid' | 'rate.limited' | 'pcs.limit'.
-- The limit is checked only for a valid code of this user, so an invalid or
-- foreign code always gives the uniform 'code.invalid'. 'pcs.limit' leaves the
-- code unapproved and live until expiry. Same condition and advisory lock as
-- the my_pcs_enforce_limit trigger (000002): a PC already bound to this user
-- is not a new PC. pc_login_consume keeps its own check (approve -> another
-- PC added -> consume race).
create or replace function public.pc_login_approve(p_user_code_hash bytea)
returns text
language plpgsql
security definer
set search_path = ''
as $$
declare
  uid uuid := auth.uid();
  v_id bigint;
  v_pc uuid;
  n int;
begin
  if uid is null then
    raise exception 'not authenticated' using errcode = 'insufficient_privilege';
  end if;
  if not public.pc_approve_gate(uid) then
    return 'rate.limited';
  end if;
  select l.id, l.pc_id into v_id, v_pc from public.pc_login_codes l
   where l.user_code_hash = p_user_code_hash
     and l.described_by = uid
     and l.approved_by is null
     and l.consumed_at is null
     and l.expires_at > pg_catalog.now()
   for update;
  if v_id is null then
    return 'code.invalid';
  end if;
  perform pg_catalog.pg_advisory_xact_lock(
    pg_catalog.hashtextextended(uid::text, 20261123));
  if (select count(*) from public.my_pcs
      where user_id = uid and pc_id <> v_pc) >= 20 then
    return 'pcs.limit';
  end if;
  update public.pc_login_codes
     set approved_by = uid, approved_at = pg_catalog.now()
   where id = v_id
     and approved_by is null
     and consumed_at is null
     and expires_at > pg_catalog.now();
  get diagnostics n = row_count;
  return case when n = 1 then 'ok' else 'code.invalid' end;
end;
$$;

-- Same truth table as 000007; Pro features gain 'screen.video' (appended,
-- order of the others unchanged). entitlement_for_pc delegates here (000007).
create or replace function public.entitlement_for_user(p_user_id uuid)
returns table (state text, plan text, features text[])
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_last text;
begin
  if p_user_id is null then
    return query select 'unlinked'::text, 'free'::text, '{}'::text[];
    return;
  end if;

  if exists (select 1 from public.subscriptions s
             where s.user_id = p_user_id
               and s.status in ('active', 'canceled')
               and s.current_period_end > pg_catalog.now()) then
    return query select 'active'::text, 'pro'::text,
      array['profiles', 'rgb.sync', 'automations', 'monitoring.extended', 'files.unlimited',
            'screen.video']::text[];
    return;
  end if;

  select s.status into v_last from public.subscriptions s
   where s.user_id = p_user_id
   order by s.updated_at desc, s.created_at desc, s.id desc
   limit 1;

  if v_last in ('refunded', 'revoked') then
    return query select 'revoked'::text, 'free'::text, '{}'::text[];
  else
    return query select 'active'::text, 'free'::text, '{}'::text[];
  end if;
end;
$$;
