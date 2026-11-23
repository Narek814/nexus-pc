-- E3-8: account deletion. account_purge removes every row of a user from the
-- public schema; the Edge Function delete-account then deletes the auth user
-- through the GoTrue Admin API (FK cascades clean up anything left).
-- payment_events has no user_id and is kept (legal retention: owner decision
-- pending, E7). rate_limits rows keyed by the raw user id (pc_login_approve)
-- are deleted; HMAC-keyed buckets (delete-account, per IP) expire through
-- cleanup(), so the delete-account limit survives a retry.
-- service_role only.

-- Idempotent: a second call for the same user (retry after a failed admin
-- delete) finds nothing and returns 'ok' as well.
-- Order matters for concurrency: login codes described/approved by the user
-- are deleted first (row locks wait for a running pc_login_consume), then the
-- PCs, each under pc_lock (same lock order as pc_release / consume), so a
-- consume that committed meanwhile is seen by the my_pcs delete.
create function public.account_purge(p_user_id uuid)
returns text
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_pc uuid;
begin
  if p_user_id is null then
    raise exception 'user id required' using errcode = 'invalid_parameter_value';
  end if;

  delete from public.pc_login_codes l
   where l.described_by = p_user_id or l.approved_by = p_user_id;

  for v_pc in
    select m.pc_id from public.my_pcs m where m.user_id = p_user_id order by m.pc_id
  loop
    perform public.pc_lock(v_pc);
    -- Re-check under the lock: the PC may have been released or rebound.
    if exists (select 1 from public.my_pcs m where m.pc_id = v_pc and m.user_id = p_user_id) then
      perform public.pc_revoke_all(v_pc);
      delete from public.pc_login_codes l where l.pc_id = v_pc;
      -- pc_keys and pc_refresh_tokens cascade with my_pcs.
      delete from public.my_pcs m where m.pc_id = v_pc and m.user_id = p_user_id;
    end if;
  end loop;

  delete from public.subscriptions s where s.user_id = p_user_id;
  delete from public.profiles p where p.id = p_user_id;
  delete from public.rate_limits r where r.key = p_user_id::text;
  return 'ok';
end;
$$;

revoke all on function public.account_purge(uuid) from public, anon, authenticated;
grant execute on function public.account_purge(uuid) to service_role;
