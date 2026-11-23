-- E3-7b: run public.cleanup() every 10 minutes with pg_cron.
-- pg_cron is available on Supabase; a local PostgreSQL without the extension
-- (tests) skips scheduling with a notice, so the migration applies everywhere.
-- Idempotent: an existing job with the same name is replaced.
do $$
begin
  if not exists (select 1 from pg_catalog.pg_available_extensions where name = 'pg_cron') then
    raise notice 'pg_cron not available: cleanup() is not scheduled';
    return;
  end if;
  execute 'create extension if not exists pg_cron with schema pg_catalog';
  if exists (select 1 from cron.job where jobname = 'nexus-cleanup') then
    perform cron.unschedule('nexus-cleanup');
  end if;
  perform cron.schedule('nexus-cleanup', '*/10 * * * *', 'select public.cleanup()');
end;
$$;
