-- PC refresh tokens: rotation with reuse detection (E3-6).
-- Only 32-byte hashes are stored (the Edge Function generates the token and
-- hashes it); the tokens themselves never reach the database.
-- A token lives 90 days from its issue (sliding: every rotation issues a new
-- token with a fresh 90 days). Rotation is a signed PC request: the Edge
-- Function verifies the ECDSA signature and passes the key it verified with,
-- the nonce and ts; SQL checks the key, ts window and nonce reuse through the
-- same pc_nonce_use / pc_login_nonces as start and consume.
-- All objects are service_role only; anon and authenticated have no access.

create table public.pc_refresh_tokens (
  id bigint generated always as identity primary key,
  pc_id uuid not null references public.my_pcs (pc_id) on delete cascade,
  family_id uuid not null,
  token_hash bytea not null unique check (octet_length(token_hash) = 32),
  created_at timestamptz not null default now(),
  used_at timestamptz,
  -- Hash of the token this one was rotated to (set with used_at). Lets a
  -- retry after a lost response get the same answer (R-1). Hash only.
  replaced_by_hash bytea check (octet_length(replaced_by_hash) = 32),
  revoked_at timestamptz,
  expires_at timestamptz not null default now() + interval '90 days'
);

create index pc_refresh_tokens_pc_id_idx on public.pc_refresh_tokens (pc_id);
create index pc_refresh_tokens_family_idx on public.pc_refresh_tokens (family_id);
create index pc_refresh_tokens_expires_idx on public.pc_refresh_tokens (expires_at);

alter table public.pc_refresh_tokens enable row level security;
alter table public.pc_refresh_tokens force row level security;

-- Deny by default: no policies, no grants for anon/authenticated.
revoke all on table public.pc_refresh_tokens from public, anon, authenticated;
grant select, insert, update, delete on table public.pc_refresh_tokens to service_role;

-- Revokes every live refresh token of a PC (all families).
-- Internal: called from pc_issue_refresh and pc_revoke_refresh_tokens
-- (rebind in pc_login_consume, pc_release). Caller holds pc_lock.
create function public.pc_revoke_all(p_pc_id uuid)
returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
  update public.pc_refresh_tokens t set revoked_at = pg_catalog.now()
   where t.pc_id = p_pc_id and t.revoked_at is null;
end;
$$;

-- Starts a new family for a PC with its first token and returns the row id.
-- One live family per PC: older families are revoked first.
-- Internal: called only from pc_login_consume on 'ok' (caller holds pc_lock),
-- so a refresh token is never issued without a consumed login code.
create function public.pc_issue_refresh(p_pc_id uuid, p_hash bytea)
returns bigint
language plpgsql
security definer
set search_path = ''
as $$
declare
  rid bigint;
begin
  if p_hash is null or octet_length(p_hash) <> 32 then
    raise exception 'invalid refresh hash' using errcode = 'invalid_parameter_value';
  end if;
  perform public.pc_revoke_all(p_pc_id);
  insert into public.pc_refresh_tokens (pc_id, family_id, token_hash)
  values (p_pc_id, pg_catalog.gen_random_uuid(), p_hash)
  returning id into rid;
  return rid;
end;
$$;

-- Signed refresh request of a PC (service_role, Edge Function).
-- status: 'ok' | 'reuse.detected' | 'revoked' | 'rate.limited' |
-- 'request.stale' | 'nonce.replay'.
-- user_id: owner of the PC on 'ok' (the Edge Function then issues the
-- entitlement token for this account), otherwise null.
-- - live and unused old token: marked used, new token in the same family;
-- - used token retried within 60 s of its rotation with the same new hash
--   (the Edge Function derives the new token deterministically from the old
--   one, see docs/BACKEND.md) while that new token is still live and unused:
--   'ok' again, nothing changes (a lost response, not a clone);
-- - any other use of a used token: the whole family is revoked,
--   'reuse.detected';
-- - revoked, expired, unknown or of another pc_id, PC not bound, or signer
--   is not the current key of the PC: 'revoked' (same answer).
-- Lock order: pc_lock, then my_pcs row, then token rows (as consume/release),
-- so concurrent rotations of one token give exactly one 'ok'.
-- Limit: 30 attempts per hour per pc_id. Every request that passed the
-- nonce, ts and key checks counts, whatever its outcome (ok, retry, reuse,
-- revoked); requests refused before the key check do not count.
create function public.pc_rotate_refresh(p_old_hash bytea, p_new_hash bytea, p_pc_id uuid,
  p_signer_spki bytea, p_nonce bytea, p_ts timestamptz)
returns table (status text, user_id uuid)
language plpgsql
security definer
set search_path = ''
as $$
declare
  f text;
  owner uuid;
  cur_key bytea;
  t public.pc_refresh_tokens%rowtype;
begin
  if p_new_hash is null or octet_length(p_new_hash) <> 32 then
    raise exception 'invalid refresh hash' using errcode = 'invalid_parameter_value';
  end if;
  f := public.pc_nonce_use(p_nonce, p_ts);
  if f is not null then
    return query select f, null::uuid;
    return;
  end if;
  perform public.pc_lock(p_pc_id);
  select m.user_id into owner from public.my_pcs m where m.pc_id = p_pc_id for key share;
  select k.public_key_spki into cur_key from public.pc_keys k where k.pc_id = p_pc_id;
  -- Released/deleted PC (tokens gone with my_pcs) and a wrong key look the
  -- same: no hint whether the pc_id is bound (R-4).
  if owner is null or p_signer_spki is null or cur_key is null or p_signer_spki <> cur_key then
    return query select 'revoked'::text, null::uuid;
    return;
  end if;
  if not public.rate_limit_hit('pc_refresh', p_pc_id::text, 30, 3600) then
    return query select 'rate.limited'::text, null::uuid;
    return;
  end if;

  select * into t from public.pc_refresh_tokens r
   where r.token_hash = p_old_hash and r.pc_id = p_pc_id
   for update;
  if not found or t.revoked_at is not null then
    return query select 'revoked'::text, null::uuid;
    return;
  end if;
  if t.used_at is not null then
    -- Retry after a lost response: same new hash, within 60 s, and the new
    -- token was neither used nor revoked since.
    if t.replaced_by_hash = p_new_hash
       and t.used_at > pg_catalog.now() - interval '60 seconds'
       and exists (select 1 from public.pc_refresh_tokens n
                    where n.token_hash = p_new_hash and n.family_id = t.family_id
                      and n.pc_id = p_pc_id and n.used_at is null and n.revoked_at is null
                      and n.expires_at > pg_catalog.now()) then
      return query select 'ok'::text, owner;
      return;
    end if;
    -- A used token presented again: a clone exists. Kill the family.
    update public.pc_refresh_tokens r set revoked_at = pg_catalog.now()
     where r.family_id = t.family_id and r.revoked_at is null;
    return query select 'reuse.detected'::text, null::uuid;
    return;
  end if;
  if t.expires_at <= pg_catalog.now() then
    return query select 'revoked'::text, null::uuid;
    return;
  end if;

  update public.pc_refresh_tokens r set used_at = pg_catalog.now(), replaced_by_hash = p_new_hash
   where r.id = t.id;
  insert into public.pc_refresh_tokens (pc_id, family_id, token_hash)
  values (p_pc_id, t.family_id, p_new_hash);
  update public.my_pcs m set last_seen_at = pg_catalog.now() where m.pc_id = p_pc_id;
  return query select 'ok'::text, owner;
end;
$$;

revoke all on function public.pc_revoke_all(uuid) from public, anon, authenticated, service_role;
revoke all on function public.pc_issue_refresh(uuid, bytea) from public, anon, authenticated, service_role;
revoke all on function public.pc_rotate_refresh(bytea, bytea, uuid, bytea, bytea, timestamptz)
  from public, anon, authenticated;
grant execute on function public.pc_rotate_refresh(bytea, bytea, uuid, bytea, bytea, timestamptz)
  to service_role;
