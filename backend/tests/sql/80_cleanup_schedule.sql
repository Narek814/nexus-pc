-- Tests for migration 000008 (cleanup schedule). Locally pg_cron is usually
-- absent: the migration must have applied as a no-op. With pg_cron the job
-- must exist exactly once with the 10-minute schedule.
\set ON_ERROR_STOP on

create temp table _results (name text);

do $$
declare
  has_cron boolean := exists (select 1 from pg_catalog.pg_extension where extname = 'pg_cron');
  n int;
begin
  if not has_cron then
    if exists (select 1 from pg_catalog.pg_available_extensions where name = 'pg_cron') then
      raise exception 'FAIL cron_installed_when_available';
    end if;
    if exists (select 1 from pg_catalog.pg_namespace where nspname = 'cron') then
      raise exception 'FAIL cron_absent_noop';
    end if;
    insert into _results values ('cron_absent_noop');
  else
    execute 'select count(*) from cron.job where jobname = ''nexus-cleanup''
              and schedule = ''*/10 * * * *'' and command = ''select public.cleanup()''' into n;
    if n <> 1 then
      raise exception 'FAIL cron_job_scheduled';
    end if;
    insert into _results values ('cron_job_scheduled');
  end if;
  -- cleanup() itself stays service_role only.
  if has_function_privilege('authenticated', 'public.cleanup()', 'execute')
     or has_function_privilege('anon', 'public.cleanup()', 'execute')
     or not has_function_privilege('service_role', 'public.cleanup()', 'execute') then
    raise exception 'FAIL cleanup_grants';
  end if;
  insert into _results values ('cleanup_grants');
end $$;

select 'PASSED ' || count(*) from _results;
