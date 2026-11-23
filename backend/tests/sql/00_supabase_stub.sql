-- Test-only stub of the Supabase environment. NOT a migration.
create role anon nologin;
create role authenticated nologin;
create role service_role nologin bypassrls;

create schema auth;
grant usage on schema auth to anon, authenticated, service_role;

create table auth.users (
  id uuid primary key,
  email text
);

create function auth.uid() returns uuid
language sql stable
as $$
  select nullif(current_setting('request.jwt.claims', true)::json->>'sub', '')::uuid
$$;

grant execute on function auth.uid() to anon, authenticated, service_role;
grant usage on schema public to anon, authenticated, service_role;
