-- E4-2: pcId is derived from the permanent PC key (docs/API.md «Идентификатор ПК»,
-- shared/fixtures/pc-id/derive.v1.json). The key is never rotated: losing it
-- means a new PC (new pcId).

-- pcId = UUID of u = SHA-256("nexus-pc-id-v1\n" || SPKI)[0..16] with
-- u[6] = (u[6] & 0x0F) | 0x80 and u[8] = (u[8] & 0x3F) | 0x80.
-- Only the exact 91-byte DER of a P-256 uncompressed point is accepted
-- (fixed 27-byte prefix incl. 0x04); anything else gives null.
create function public.pc_id_from_spki(p_spki bytea)
returns uuid
language sql
immutable
set search_path = ''
as $$
  select case
    when pg_catalog.octet_length(p_spki) = 91
     and pg_catalog.substr(p_spki, 1, 27)
         = '\x3059301306072a8648ce3d020106082a8648ce3d03010703420004'::bytea
    then pg_catalog.encode(
           pg_catalog.set_byte(pg_catalog.set_byte(u,
             6, (pg_catalog.get_byte(u, 6) & 15) | 128),
             8, (pg_catalog.get_byte(u, 8) & 63) | 128), 'hex')::uuid
  end
  from (select pg_catalog.substr(pg_catalog.sha256(
          '\x6e657875732d70632d69642d76310a'::bytea || p_spki), 1, 16) as u) s
$$;

revoke all on function public.pc_id_from_spki(bytea) from public, anon, authenticated;
grant execute on function public.pc_id_from_spki(bytea) to service_role;

-- Same as 000005, plus: p_pc_id must be derived from p_spki (always, first
-- binding and rebind), and a stored pc_keys key must equal p_spki (no key
-- rotation). Both failures return 'pc.key_required' and are not counted.
create or replace function public.pc_login_start(p_pc_id uuid, p_spki bytea, p_name text,
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
  if p_pc_id is distinct from public.pc_id_from_spki(p_spki) then
    return 'pc.key_required';
  end if;
  perform public.pc_lock(p_pc_id);
  select exists (select 1 from public.my_pcs m where m.pc_id = p_pc_id) into bound;
  select k.public_key_spki into cur_key from public.pc_keys k where k.pc_id = p_pc_id;
  if p_signer_spki is null
     or (cur_key is not null and p_spki <> cur_key)
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
