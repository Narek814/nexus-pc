-- Profiles: minimal per-user row. No email or other personal data here.
create table public.profiles (
  id uuid primary key references auth.users (id) on delete cascade,
  created_at timestamptz not null default now(),
  locale text check (locale in ('en', 'ru'))
);

alter table public.profiles enable row level security;
alter table public.profiles force row level security;

-- Deny by default: start from zero privileges, grant only what policies cover.
revoke all on table public.profiles from public, anon, authenticated;
grant select, update (locale) on table public.profiles to authenticated;

create policy profiles_select_own on public.profiles
  for select to authenticated
  using (id = auth.uid());

create policy profiles_update_own on public.profiles
  for update to authenticated
  using (id = auth.uid())
  with check (id = auth.uid());

-- Rows are created only by this trigger on auth.users (no insert policy).
create function public.handle_new_user() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
  insert into public.profiles (id) values (new.id);
  return new;
end;
$$;

revoke all on function public.handle_new_user() from public, anon, authenticated;

create trigger on_auth_user_created
  after insert on auth.users
  for each row execute function public.handle_new_user();
