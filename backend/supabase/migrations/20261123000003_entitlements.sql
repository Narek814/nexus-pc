-- Subscriptions and entitlement computation (groundwork for E7 payments).
-- No card data is ever stored here: only provider-neutral subscription state.
create table public.subscriptions (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references auth.users (id) on delete cascade,
  source text not null check (source in ('manual', 'web', 'appstore')),
  status text not null check (status in ('active', 'canceled', 'refunded', 'revoked', 'expired')),
  product text not null check (product in ('pro_monthly', 'pro_yearly')),
  current_period_end timestamptz,
  revoked_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index subscriptions_user_id_idx on public.subscriptions (user_id);

alter table public.subscriptions enable row level security;
alter table public.subscriptions force row level security;

-- Deny by default: clients read their own rows only; writes are server-side.
revoke all on table public.subscriptions from public, anon, authenticated;
grant select on table public.subscriptions to authenticated;
grant select, insert, update, delete on table public.subscriptions to service_role;

create policy subscriptions_select_own on public.subscriptions
  for select to authenticated
  using (user_id = auth.uid());

-- Webhook idempotency ledger (E7). No client access at all.
create table public.payment_events (
  provider text not null,
  event_id text not null,
  received_at timestamptz not null default now(),
  primary key (provider, event_id)
);

alter table public.payment_events enable row level security;
alter table public.payment_events force row level security;

revoke all on table public.payment_events from public, anon, authenticated;
grant select, insert on table public.payment_events to service_role;

-- Entitlement of a PC, decided by the PC owner's account (my_pcs.user_id),
-- never by whoever is connecting. state: 'active' (pro or free), 'revoked'
-- (latest subscription refunded/revoked and nothing active), 'unlinked'.
create function public.entitlement_for_pc(p_pc_id uuid)
returns table (state text, plan text, features text[])
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_owner uuid;
  v_last text;
begin
  select p.user_id into v_owner from public.my_pcs p where p.pc_id = p_pc_id;
  if v_owner is null then
    return query select 'unlinked'::text, 'free'::text, '{}'::text[];
    return;
  end if;

  if exists (select 1 from public.subscriptions s
             where s.user_id = v_owner
               and s.status in ('active', 'canceled')
               and s.current_period_end > pg_catalog.now()) then
    return query select 'active'::text, 'pro'::text,
      array['profiles', 'rgb.sync', 'automations', 'monitoring.extended', 'files.unlimited']::text[];
    return;
  end if;

  select s.status into v_last from public.subscriptions s
   where s.user_id = v_owner
   order by s.updated_at desc, s.created_at desc, s.id desc
   limit 1;

  if v_last in ('refunded', 'revoked') then
    return query select 'revoked'::text, 'free'::text, '{}'::text[];
  else
    return query select 'active'::text, 'free'::text, '{}'::text[];
  end if;
end;
$$;

revoke all on function public.entitlement_for_pc(uuid) from public, anon, authenticated;
grant execute on function public.entitlement_for_pc(uuid) to service_role;
