-- E3-7a: entitlement by user (Edge issues the entitlement token by the
-- user_id returned from pc_login_consume / pc_rotate_refresh), and read
-- helpers for Edge (service_role only) to pick the key that verifies a
-- signed PC request before any write.

-- Rights of a user: same truth table as entitlement_for_pc for the owner.
-- p_user_id null -> 'unlinked' (no owner).
create function public.entitlement_for_user(p_user_id uuid)
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
      array['profiles', 'rgb.sync', 'automations', 'monitoring.extended', 'files.unlimited']::text[];
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

-- Same signature and behaviour as in 000003; logic now lives in
-- entitlement_for_user. create or replace keeps the existing grants.
create or replace function public.entitlement_for_pc(p_pc_id uuid)
returns table (state text, plan text, features text[])
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_owner uuid;
begin
  select p.user_id into v_owner from public.my_pcs p where p.pc_id = p_pc_id;
  if v_owner is null then
    return query select 'unlinked'::text, 'free'::text, '{}'::text[];
    return;
  end if;
  return query select e.state, e.plan, e.features from public.entitlement_for_user(v_owner) e;
end;
$$;

-- Current account key of a PC (null if none). Edge verifies refresh and
-- rebind-start signatures with it before calling the writing functions.
create function public.pc_key_get(p_pc_id uuid)
returns bytea
language sql
stable
security definer
set search_path = ''
as $$
  select k.public_key_spki from public.pc_keys k where k.pc_id = p_pc_id;
$$;

-- Live login code by device-code hash: pc_id and the new key of the code
-- (Edge verifies the consume signature with it). Unknown, consumed or
-- expired codes return no row.
create function public.pc_login_code_key(p_device_code_hash bytea)
returns table (pc_id uuid, public_key_spki bytea)
language sql
stable
security definer
set search_path = ''
as $$
  select l.pc_id, l.public_key_spki from public.pc_login_codes l
   where l.device_code_hash = p_device_code_hash
     and l.consumed_at is null and l.expires_at > pg_catalog.now();
$$;

revoke all on function public.entitlement_for_user(uuid) from public, anon, authenticated;
revoke all on function public.entitlement_for_pc(uuid) from public, anon, authenticated;
revoke all on function public.pc_key_get(uuid) from public, anon, authenticated;
revoke all on function public.pc_login_code_key(bytea) from public, anon, authenticated;
grant execute on function public.entitlement_for_user(uuid) to service_role;
grant execute on function public.entitlement_for_pc(uuid) to service_role;
grant execute on function public.pc_key_get(uuid) to service_role;
grant execute on function public.pc_login_code_key(bytea) to service_role;
