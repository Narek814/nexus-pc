-- PC sign-in with one-time codes (device-code flow) and rate limits.
-- Only SHA-256 hashes of codes are stored; the codes themselves are generated
-- by the Edge Function and never reach the database.
-- All objects here are service_role only, except pc_login_approve.

create table public.pc_keys (
  pc_id uuid primary key references public.my_pcs (pc_id) on delete cascade,
  public_key_spki bytea not null check (octet_length(public_key_spki) between 59 and 200),
  created_at timestamptz not null default now()
);

create table public.pc_login_codes (
  id bigint generated always as identity primary key,
  pc_id uuid not null,
  public_key_spki bytea not null check (octet_length(public_key_spki) between 59 and 200),
  display_name text not null check (char_length(display_name) between 1 and 64),
  device_code_hash bytea not null unique check (octet_length(device_code_hash) = 32),
  user_code_hash bytea not null unique check (octet_length(user_code_hash) = 32),
  approved_by uuid references auth.users (id) on delete cascade,
  approved_at timestamptz,
  consumed_at timestamptz,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null default now() + interval '10 minutes',
  attempts int not null default 0
);

create index pc_login_codes_expires_idx on public.pc_login_codes (expires_at);

create table public.rate_limits (
  bucket text not null,
  key text not null,
  window_start timestamptz not null,
  count int not null,
  primary key (bucket, key, window_start)
);

alter table public.pc_keys enable row level security;
alter table public.pc_keys force row level security;
alter table public.pc_login_codes enable row level security;
alter table public.pc_login_codes force row level security;
alter table public.rate_limits enable row level security;
alter table public.rate_limits force row level security;

-- Deny by default: no policies, no grants for anon/authenticated.
revoke all on table public.pc_keys, public.pc_login_codes, public.rate_limits
  from public, anon, authenticated;
grant select, insert, update, delete on table public.pc_keys, public.pc_login_codes,
  public.rate_limits to service_role;

-- Fixed-window counter. Returns true if the hit is allowed (count <= limit).
-- Callers must not raise after a denied/failed attempt, otherwise the counter
-- increment is rolled back; they return a status instead.
create function public.rate_limit_hit(p_bucket text, p_key text, p_limit int, p_window_sec int)
returns boolean
language plpgsql
security definer
set search_path = ''
as $$
declare
  w timestamptz;
  c int;
begin
  if p_limit < 1 or p_window_sec < 1 then
    raise exception 'invalid rate limit' using errcode = 'invalid_parameter_value';
  end if;
  w := pg_catalog.to_timestamp(
    pg_catalog.floor(extract(epoch from pg_catalog.now()) / p_window_sec) * p_window_sec);
  insert into public.rate_limits as r (bucket, key, window_start, count)
  values (p_bucket, p_key, w, 1)
  on conflict (bucket, key, window_start) do update set count = r.count + 1
  returning r.count into c;
  return c <= p_limit;
end;
$$;

-- Called by the Edge Function (service_role) after it generated both codes.
-- Returns 'ok' | 'rate.limited'. At most 5 starts per 10 minutes per pc_id.
create function public.pc_login_start(p_pc_id uuid, p_spki bytea, p_name text,
  p_device_code_hash bytea, p_user_code_hash bytea)
returns text
language plpgsql
security definer
set search_path = ''
as $$
begin
  if not public.rate_limit_hit('pc_login_start', p_pc_id::text, 5, 600) then
    return 'rate.limited';
  end if;
  insert into public.pc_login_codes (pc_id, public_key_spki, display_name,
    device_code_hash, user_code_hash)
  values (p_pc_id, p_spki, p_name, p_device_code_hash, p_user_code_hash);
  return 'ok';
end;
$$;

-- Called by a signed-in user who typed the code shown on the PC.
-- Returns 'ok' | 'code.invalid' | 'rate.limited'. Unknown, expired, consumed
-- and already approved codes all give the same 'code.invalid' via the same
-- single index lookup, so existence is not revealed. The rate counter is hit
-- first on every call (10 per 10 minutes per user) and survives failures.
create function public.pc_login_approve(p_user_code_hash bytea)
returns text
language plpgsql
security definer
set search_path = ''
as $$
declare
  uid uuid := auth.uid();
  n int;
begin
  if uid is null then
    raise exception 'not authenticated' using errcode = 'insufficient_privilege';
  end if;
  if not public.rate_limit_hit('pc_login_approve', uid::text, 10, 600) then
    return 'rate.limited';
  end if;
  update public.pc_login_codes
     set approved_by = uid, approved_at = pg_catalog.now()
   where user_code_hash = p_user_code_hash
     and approved_by is null
     and consumed_at is null
     and expires_at > pg_catalog.now();
  get diagnostics n = row_count;
  return case when n = 1 then 'ok' else 'code.invalid' end;
end;
$$;

-- Hook for E3-6: revoke every refresh token of the previous binding of a PC.
-- No refresh tokens exist yet; E3-6 replaces the body.
create function public.pc_revoke_refresh_tokens(p_pc_id uuid, p_old_user uuid)
returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
  -- TODO(E3-6): revoke refresh tokens of (p_pc_id, p_old_user).
  return;
end;
$$;

-- Polled by the PC through the Edge Function (service_role).
-- status: 'ok' | 'pending' | 'code.invalid' | 'pcs.limit'.
-- One-time: the row is locked, so of two concurrent calls exactly one gets 'ok'.
-- Owner decision: an approved sign-in rebinds the PC to the approving account
-- and revokes refresh tokens of the old binding.
create function public.pc_login_consume(p_device_code_hash bytea)
returns table (status text, pc_id uuid, user_id uuid, rebound boolean)
language plpgsql
security definer
set search_path = ''
as $$
declare
  c public.pc_login_codes%rowtype;
  old_user uuid;
begin
  select * into c from public.pc_login_codes l
   where l.device_code_hash = p_device_code_hash
   for update;
  if not found or c.consumed_at is not null or c.expires_at <= pg_catalog.now() then
    return query select 'code.invalid'::text, null::uuid, null::uuid, false;
    return;
  end if;
  if c.approved_by is null then
    update public.pc_login_codes l set attempts = l.attempts + 1 where l.id = c.id;
    return query select 'pending'::text, null::uuid, null::uuid, false;
    return;
  end if;

  select m.user_id into old_user from public.my_pcs m where m.pc_id = c.pc_id for update;
  begin
    if old_user is null then
      insert into public.my_pcs (pc_id, user_id, display_name, platform)
      values (c.pc_id, c.approved_by, c.display_name, 'windows');
    else
      update public.my_pcs m
         set user_id = c.approved_by, display_name = c.display_name,
             last_seen_at = pg_catalog.now()
       where m.pc_id = c.pc_id;
    end if;
  exception when check_violation then
    -- 20-PC limit of the approving account. Code stays usable until expiry.
    return query select 'pcs.limit'::text, null::uuid, null::uuid, false;
    return;
  end;

  if old_user is not null and old_user <> c.approved_by then
    perform public.pc_revoke_refresh_tokens(c.pc_id, old_user);
  end if;

  insert into public.pc_keys as k (pc_id, public_key_spki)
  values (c.pc_id, c.public_key_spki)
  on conflict on constraint pc_keys_pkey
  do update set public_key_spki = excluded.public_key_spki, created_at = pg_catalog.now();

  update public.pc_login_codes l set consumed_at = pg_catalog.now() where l.id = c.id;
  return query select 'ok'::text, c.pc_id, c.approved_by,
    (old_user is not null and old_user <> c.approved_by);
end;
$$;

-- Housekeeping; scheduled with pg_cron in the cloud later.
create function public.cleanup()
returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
  delete from public.pc_login_codes
   where expires_at <= pg_catalog.now() or consumed_at is not null;
  delete from public.rate_limits
   where window_start < pg_catalog.now() - interval '1 day';
end;
$$;

revoke all on function public.rate_limit_hit(text, text, int, int) from public, anon, authenticated;
revoke all on function public.pc_login_start(uuid, bytea, text, bytea, bytea) from public, anon, authenticated;
revoke all on function public.pc_login_approve(bytea) from public, anon;
revoke all on function public.pc_revoke_refresh_tokens(uuid, uuid) from public, anon, authenticated, service_role;
revoke all on function public.pc_login_consume(bytea) from public, anon, authenticated;
revoke all on function public.cleanup() from public, anon, authenticated;
grant execute on function public.rate_limit_hit(text, text, int, int) to service_role;
grant execute on function public.pc_login_start(uuid, bytea, text, bytea, bytea) to service_role;
grant execute on function public.pc_login_approve(bytea) to authenticated;
grant execute on function public.pc_login_consume(bytea) to service_role;
grant execute on function public.cleanup() to service_role;
