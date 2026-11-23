-- PC sign-in with one-time codes (device-code flow) and rate limits.
-- Only 32-byte hashes of codes are stored (HMAC-SHA-256 with a pepper held by
-- the Edge Function); the codes themselves never reach the database.
-- PC request signatures (ECDSA P-256) are verified by the Edge Function; it
-- passes the SPKI it verified with (p_signer_spki), the signed nonce and ts.
-- SQL checks that this key is the right one, the ts window and nonce reuse.
-- All objects here are service_role only, except pc_login_describe,
-- pc_login_approve and pc_release (authenticated).

create table public.pc_keys (
  pc_id uuid primary key references public.my_pcs (pc_id) on delete cascade,
  public_key_spki bytea not null check (octet_length(public_key_spki) between 59 and 200),
  created_at timestamptz not null default now()
);

-- Cc, Cf, Zl and Zp characters (controls, bidi overrides, zero-width, tags,
-- U+2028/U+2029) are not allowed in a PC name shown to the user in the
-- approval step (Unicode 15.1). Same set as my_pcs_display_name_printable.
create table public.pc_login_codes (
  id bigint generated always as identity primary key,
  pc_id uuid not null,
  public_key_spki bytea not null check (octet_length(public_key_spki) between 59 and 200),
  -- pc_keys key at start (null = first binding); consume requires it unchanged.
  prev_key_spki bytea,
  display_name text not null
    constraint pc_login_codes_display_name_len check (char_length(display_name) between 1 and 64)
    constraint pc_login_codes_display_name_printable check (display_name !~
      '[\u0001-\u001f\u007f-\u009f\u00ad\u0600-\u0605\u061c\u06dd\u070f\u0890-\u0891\u08e2\u180e\u200b-\u200f\u2028-\u202e\u2060-\u2064\u2066-\u206f\ufeff\ufff9-\ufffb\U000110bd\U000110cd\U00013430-\U0001343f\U0001bca0-\U0001bca3\U0001d173-\U0001d17a\U000e0001\U000e0020-\U000e007f]'),
  device_code_hash bytea not null unique check (octet_length(device_code_hash) = 32),
  user_code_hash bytea not null unique check (octet_length(user_code_hash) = 32),
  described_by uuid references auth.users (id) on delete cascade,
  described_at timestamptz,
  approved_by uuid references auth.users (id) on delete cascade,
  approved_at timestamptz,
  consumed_at timestamptz,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null default now() + interval '5 minutes',
  last_poll_at timestamptz,
  attempts int not null default 0
);

create index pc_login_codes_expires_idx on public.pc_login_codes (expires_at);
create index pc_login_codes_pc_id_idx on public.pc_login_codes (pc_id);

-- One-time nonces of signed PC requests. start and consume share one space;
-- the signed string also names the operation.
create table public.pc_login_nonces (
  nonce bytea primary key check (octet_length(nonce) between 16 and 32),
  created_at timestamptz not null default now()
);

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
alter table public.pc_login_nonces enable row level security;
alter table public.pc_login_nonces force row level security;
alter table public.rate_limits enable row level security;
alter table public.rate_limits force row level security;

-- Deny by default: no policies, no grants for anon/authenticated.
revoke all on table public.pc_keys, public.pc_login_codes, public.pc_login_nonces,
  public.rate_limits from public, anon, authenticated;
grant select, insert, update, delete on table public.pc_keys, public.pc_login_codes,
  public.pc_login_nonces, public.rate_limits to service_role;

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

-- Signed-request freshness: null if fresh, otherwise a status.
-- ts must be within +-300 s of server time; a nonce is accepted once.
create function public.pc_nonce_use(p_nonce bytea, p_ts timestamptz)
returns text
language plpgsql
security definer
set search_path = ''
as $$
declare
  n int;
begin
  if p_ts is null or p_nonce is null
     or p_ts < pg_catalog.now() - interval '300 seconds'
     or p_ts > pg_catalog.now() + interval '300 seconds' then
    return 'request.stale';
  end if;
  if octet_length(p_nonce) not between 16 and 32 then
    return 'request.stale';
  end if;
  insert into public.pc_login_nonces (nonce) values (p_nonce)
  on conflict (nonce) do nothing;
  get diagnostics n = row_count;
  return case when n = 1 then null else 'nonce.replay' end;
end;
$$;

-- Serialises start / consume / release of one pc_id.
create function public.pc_lock(p_pc_id uuid)
returns void
language sql
security definer
set search_path = ''
as $$
  select pg_catalog.pg_advisory_xact_lock(
    pg_catalog.hashtextextended(p_pc_id::text, 20261124));
$$;

-- Called by the Edge Function (service_role) after it generated both codes and
-- verified the request signature. p_signer_spki is the key the signature was
-- verified with: the stored pc_keys key if the PC is bound (rebind needs the
-- old key), otherwise the new key p_spki (first binding, proof of possession).
-- Returns 'ok' | 'rate.limited' | 'pc.key_required' | 'request.stale' |
-- 'nonce.replay'. Limits: 5 accepted starts per 10 min per pc_id (counted
-- only after the key check), at most 3 live codes per pc_id.
create function public.pc_login_start(p_pc_id uuid, p_spki bytea, p_name text,
  p_device_code_hash bytea, p_user_code_hash bytea,
  p_signer_spki bytea, p_nonce bytea, p_ts timestamptz)
returns text
language plpgsql
security definer
set search_path = ''
as $$
declare
  f text;
  cur_key bytea;
  bound boolean;
  live int;
begin
  f := public.pc_nonce_use(p_nonce, p_ts);
  if f is not null then
    return f;
  end if;
  perform public.pc_lock(p_pc_id);
  select exists (select 1 from public.my_pcs m where m.pc_id = p_pc_id) into bound;
  select k.public_key_spki into cur_key from public.pc_keys k where k.pc_id = p_pc_id;
  if p_signer_spki is null
     or (bound and (cur_key is null or p_signer_spki <> cur_key))
     or (not bound and p_signer_spki <> p_spki) then
    return 'pc.key_required';
  end if;
  if not public.rate_limit_hit('pc_login_start', p_pc_id::text, 5, 600) then
    return 'rate.limited';
  end if;
  select count(*) into live from public.pc_login_codes l
   where l.pc_id = p_pc_id and l.consumed_at is null and l.expires_at > pg_catalog.now();
  if live >= 3 then
    return 'rate.limited';
  end if;
  insert into public.pc_login_codes (pc_id, public_key_spki, prev_key_spki, display_name,
    device_code_hash, user_code_hash)
  values (p_pc_id, p_spki, case when bound then cur_key end, p_name,
    p_device_code_hash, p_user_code_hash);
  return 'ok';
end;
$$;

-- Shared gate of describe/approve: 10 per 10 min per user, then 10000 per
-- 10 min globally (alarm fuse; the main protection is this per-account limit
-- and the per-IP limit in Edge). The global counter is hit only when the
-- per-user limit passed, so one account cannot exhaust it for everyone.
create function public.pc_approve_gate(p_uid uuid)
returns boolean
language plpgsql
security definer
set search_path = ''
as $$
begin
  if not public.rate_limit_hit('pc_login_approve', p_uid::text, 10, 600) then
    return false;
  end if;
  return public.rate_limit_hit('pc_login_approve_global', 'all', 10000, 600);
end;
$$;

-- Step 1 of approval: the user typed the code; show PC name and request time.
-- No pc_id, no keys. Unknown, expired, consumed and approved codes all give the
-- same 'code.invalid' through the same single index lookup. Marks the code as
-- described by this user; approve requires that. A code already described by
-- another user is not taken over: same 'code.invalid', same path and counter.
create function public.pc_login_describe(p_user_code_hash bytea)
returns table (status text, display_name text, requested_at timestamptz)
language plpgsql
security definer
set search_path = ''
as $$
declare
  uid uuid := auth.uid();
  n text;
  t timestamptz;
begin
  if uid is null then
    raise exception 'not authenticated' using errcode = 'insufficient_privilege';
  end if;
  if not public.pc_approve_gate(uid) then
    return query select 'rate.limited'::text, null::text, null::timestamptz;
    return;
  end if;
  update public.pc_login_codes l
     set described_by = uid, described_at = pg_catalog.now()
   where l.user_code_hash = p_user_code_hash
     and (l.described_by is null or l.described_by = uid)
     and l.approved_by is null
     and l.consumed_at is null
     and l.expires_at > pg_catalog.now()
  returning l.display_name, l.created_at into n, t;
  if n is null then
    return query select 'code.invalid'::text, null::text, null::timestamptz;
  else
    return query select 'ok'::text, n, t;
  end if;
end;
$$;

-- Step 2 of approval. Returns 'ok' | 'code.invalid' | 'rate.limited'.
-- Same gate and the same uniform 'code.invalid' as describe; the code must have
-- been described by the same user first.
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
  if not public.pc_approve_gate(uid) then
    return 'rate.limited';
  end if;
  update public.pc_login_codes
     set approved_by = uid, approved_at = pg_catalog.now()
   where user_code_hash = p_user_code_hash
     and described_by = uid
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

-- Owner releases a PC: removes it from my_pcs (pc_keys cascades), revokes
-- refresh tokens and voids live codes of this PC. The next start for this
-- pc_id is a first binding. Returns 'ok' | 'pc.not_found' (also for PCs of
-- other users, so ownership is not revealed).
create function public.pc_release(p_pc_id uuid)
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
  perform public.pc_lock(p_pc_id);
  delete from public.my_pcs m where m.pc_id = p_pc_id and m.user_id = uid;
  get diagnostics n = row_count;
  if n = 0 then
    return 'pc.not_found';
  end if;
  perform public.pc_revoke_refresh_tokens(p_pc_id, uid);
  update public.pc_login_codes l set expires_at = pg_catalog.now()
   where l.pc_id = p_pc_id and l.consumed_at is null and l.expires_at > pg_catalog.now();
  return 'ok';
end;
$$;

-- Polled by the PC through the Edge Function (service_role). The poll is
-- signed by the new key of this code (p_signer_spki must equal it).
-- status: 'ok' | 'pending' | 'slow_down' | 'code.invalid' | 'pcs.limit' |
-- 'pc.key_required' | 'request.stale' | 'nonce.replay'.
-- One-time: pc_id is serialised with pc_lock first, then the code row is
-- locked (same order as pc_release: pc_lock -> code rows), so of concurrent
-- consumes of one code, or of two codes of one pc_id, exactly
-- one gets 'ok'. Polls: not more often than every 5 s ('slow_down'), at most
-- 150 per code, after that the code is voided ('code.invalid').
-- The binding seen at start (prev_key_spki) must be unchanged at consume.
create function public.pc_login_consume(p_device_code_hash bytea,
  p_signer_spki bytea, p_nonce bytea, p_ts timestamptz)
returns table (status text, pc_id uuid, user_id uuid, rebound boolean)
language plpgsql
security definer
set search_path = ''
as $$
declare
  c public.pc_login_codes%rowtype;
  f text;
  old_user uuid;
  cur_key bytea;
  lock_pc uuid;
begin
  f := public.pc_nonce_use(p_nonce, p_ts);
  if f is not null then
    return query select f, null::uuid, null::uuid, false;
    return;
  end if;
  -- Lock order: pc_lock, then the code row (as in pc_release and start).
  -- pc_id of a code never changes, so this unlocked read is safe.
  select l.pc_id into lock_pc from public.pc_login_codes l
   where l.device_code_hash = p_device_code_hash;
  if lock_pc is not null then
    perform public.pc_lock(lock_pc);
  end if;
  select * into c from public.pc_login_codes l
   where l.device_code_hash = p_device_code_hash
   for update;
  if not found or c.consumed_at is not null or c.expires_at <= pg_catalog.now() then
    return query select 'code.invalid'::text, null::uuid, null::uuid, false;
    return;
  end if;
  if p_signer_spki is null or p_signer_spki <> c.public_key_spki then
    return query select 'pc.key_required'::text, null::uuid, null::uuid, false;
    return;
  end if;

  update public.pc_login_codes l
     set attempts = l.attempts + 1, last_poll_at = pg_catalog.now()
   where l.id = c.id;
  if c.attempts + 1 > 150 then
    update public.pc_login_codes l set expires_at = pg_catalog.now() where l.id = c.id;
    return query select 'code.invalid'::text, null::uuid, null::uuid, false;
    return;
  end if;
  if c.last_poll_at is not null and c.last_poll_at > pg_catalog.now() - interval '5 seconds' then
    return query select 'slow_down'::text, null::uuid, null::uuid, false;
    return;
  end if;
  if c.approved_by is null then
    return query select 'pending'::text, null::uuid, null::uuid, false;
    return;
  end if;

  select m.user_id into old_user from public.my_pcs m where m.pc_id = c.pc_id for update;
  select k.public_key_spki into cur_key from public.pc_keys k where k.pc_id = c.pc_id;
  if (old_user is not null and c.prev_key_spki is null)
     or c.prev_key_spki is distinct from cur_key then
    -- Binding changed since start (other code consumed, release, rebind).
    update public.pc_login_codes l set expires_at = pg_catalog.now() where l.id = c.id;
    return query select 'code.invalid'::text, null::uuid, null::uuid, false;
    return;
  end if;

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
-- Nonces are kept 1 h (more than the +-300 s ts window).
create function public.cleanup()
returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
  delete from public.pc_login_codes
   where expires_at <= pg_catalog.now() or consumed_at is not null;
  delete from public.pc_login_nonces
   where created_at < pg_catalog.now() - interval '1 hour';
  delete from public.rate_limits
   where window_start < pg_catalog.now() - interval '1 day';
end;
$$;

revoke all on function public.rate_limit_hit(text, text, int, int) from public, anon, authenticated;
revoke all on function public.pc_nonce_use(bytea, timestamptz) from public, anon, authenticated, service_role;
revoke all on function public.pc_lock(uuid) from public, anon, authenticated, service_role;
revoke all on function public.pc_approve_gate(uuid) from public, anon, authenticated, service_role;
revoke all on function public.pc_login_start(uuid, bytea, text, bytea, bytea, bytea, bytea, timestamptz)
  from public, anon, authenticated;
revoke all on function public.pc_login_describe(bytea) from public, anon;
revoke all on function public.pc_login_approve(bytea) from public, anon;
revoke all on function public.pc_release(uuid) from public, anon;
revoke all on function public.pc_revoke_refresh_tokens(uuid, uuid) from public, anon, authenticated, service_role;
revoke all on function public.pc_login_consume(bytea, bytea, bytea, timestamptz) from public, anon, authenticated;
revoke all on function public.cleanup() from public, anon, authenticated;
grant execute on function public.rate_limit_hit(text, text, int, int) to service_role;
grant execute on function public.pc_login_start(uuid, bytea, text, bytea, bytea, bytea, bytea, timestamptz)
  to service_role;
grant execute on function public.pc_login_describe(bytea) to authenticated;
grant execute on function public.pc_login_approve(bytea) to authenticated;
grant execute on function public.pc_release(uuid) to authenticated;
grant execute on function public.pc_login_consume(bytea, bytea, bytea, timestamptz) to service_role;
grant execute on function public.cleanup() to service_role;
