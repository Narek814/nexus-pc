-- my_pcs: a PC "business card" bound to an account. Never store network data
-- (IP, MAC), hardware fingerprints or pairing keys here: an account must not
-- grant access to a PC.
create table public.my_pcs (
  pc_id uuid primary key,
  user_id uuid not null references auth.users (id) on delete cascade,
  -- No Cc, Cf, Zl, Zp characters (Unicode 15.1); same set as
  -- pc_login_codes_display_name_printable.
  display_name text not null
    constraint my_pcs_display_name_len check (char_length(display_name) between 1 and 64)
    constraint my_pcs_display_name_printable check (display_name !~
      '[\u0001-\u001f\u007f-\u009f­؀-؅؜۝܏࢐-࢑࣢᠎​-‏ -‮⁠-⁤⁦-⁯﻿￹-￻\U000110bd\U000110cd\U00013430-\U0001343f\U0001bca0-\U0001bca3\U0001d173-\U0001d17a\U000e0001\U000e0020-\U000e007f]'),
  platform text not null check (platform in ('windows')),
  added_at timestamptz not null default now(),
  last_seen_at timestamptz
);

create index my_pcs_user_id_idx on public.my_pcs (user_id);

alter table public.my_pcs enable row level security;
alter table public.my_pcs force row level security;

-- Deny by default. Rows are created only server-side at PC sign-in (E3-5).
revoke all on table public.my_pcs from public, anon, authenticated;
-- No DELETE for clients: a PC is removed only via pc_release, which also
-- revokes refresh tokens and voids live sign-in codes (E3-5c).
grant select, update (display_name) on table public.my_pcs to authenticated;
grant select, insert, update, delete on table public.my_pcs to service_role;

create policy my_pcs_select_own on public.my_pcs
  for select to authenticated
  using (user_id = auth.uid());

create policy my_pcs_update_own on public.my_pcs
  for update to authenticated
  using (user_id = auth.uid())
  with check (user_id = auth.uid());

-- At most 20 PCs per user. Serialised per user with an advisory lock so that
-- concurrent inserts cannot exceed the limit.
create function public.my_pcs_enforce_limit() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
  perform pg_catalog.pg_advisory_xact_lock(
    pg_catalog.hashtextextended(new.user_id::text, 20261123));
  if (select count(*) from public.my_pcs
      where user_id = new.user_id and pc_id <> new.pc_id) >= 20 then
    raise exception 'my_pcs limit reached (20 per user)'
      using errcode = 'check_violation';
  end if;
  return new;
end;
$$;

revoke all on function public.my_pcs_enforce_limit() from public, anon, authenticated;

create trigger my_pcs_limit
  before insert or update of user_id on public.my_pcs
  for each row execute function public.my_pcs_enforce_limit();
