-- RLS tests for public.profiles. Each check raises with its name on failure.
\set ON_ERROR_STOP on

insert into auth.users (id, email) values
  ('00000000-0000-0000-0000-00000000000a', 'a@example.test'),
  ('00000000-0000-0000-0000-00000000000b', 'b@example.test');

create temp table _results (name text);
grant insert on _results to authenticated, anon;

do $$ begin
  if (select count(*) from public.profiles) <> 2 then
    raise exception 'FAIL trigger_creates_profiles';
  end if;
  insert into _results values ('trigger_creates_profiles');
end $$;

-- As user A.
set role authenticated;
select set_config('request.jwt.claims', '{"sub":"00000000-0000-0000-0000-00000000000a","role":"authenticated"}', false) \g /dev/null

do $$ begin
  if (select count(*) from public.profiles where id = '00000000-0000-0000-0000-00000000000a') <> 1 then
    raise exception 'FAIL own_profile_visible';
  end if;
  insert into _results values ('own_profile_visible');
end $$;

do $$ begin
  if (select count(*) from public.profiles where id = '00000000-0000-0000-0000-00000000000b') <> 0 then
    raise exception 'FAIL other_profile_hidden';
  end if;
  if (select count(*) from public.profiles) <> 1 then
    raise exception 'FAIL other_profile_hidden (total count)';
  end if;
  insert into _results values ('other_profile_hidden');
end $$;

do $$ declare n int; begin
  update public.profiles set locale = 'ru' where id = '00000000-0000-0000-0000-00000000000b';
  get diagnostics n = row_count;
  if n <> 0 then raise exception 'FAIL update_other_profile_zero_rows'; end if;
  insert into _results values ('update_other_profile_zero_rows');
end $$;

do $$ declare n int; begin
  update public.profiles set locale = 'ru' where id = '00000000-0000-0000-0000-00000000000a';
  get diagnostics n = row_count;
  if n <> 1 then raise exception 'FAIL update_own_profile'; end if;
  insert into _results values ('update_own_profile');
end $$;

do $$ begin
  begin
    insert into public.profiles (id) values ('00000000-0000-0000-0000-0000000000cc');
    raise exception 'FAIL insert_denied';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('insert_denied');
end $$;

do $$ begin
  begin
    delete from public.profiles where id = '00000000-0000-0000-0000-00000000000b';
    raise exception 'FAIL delete_denied';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('delete_denied');
end $$;

-- As anon (claims of A still set; must not help).
reset role;
set role anon;
do $$ declare n int; begin
  begin
    select count(*) into n from public.profiles;
    if n <> 0 then raise exception 'FAIL anon_sees_nothing'; end if;
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('anon_sees_nothing');
end $$;

reset role;
select set_config('request.jwt.claims', '', false) \g /dev/null

do $$ begin
  delete from auth.users where id = '00000000-0000-0000-0000-00000000000b';
  if exists (select 1 from public.profiles where id = '00000000-0000-0000-0000-00000000000b') then
    raise exception 'FAIL cascade_delete';
  end if;
  insert into _results values ('cascade_delete');
end $$;

-- Catalog checks for properties behaviour tests above cannot observe.
-- Locale check (behavioural), as user A.
set role authenticated;
select set_config('request.jwt.claims', '{"sub":"00000000-0000-0000-0000-00000000000a","role":"authenticated"}', false) \g /dev/null
do $$ begin
  begin
    update public.profiles set locale = 'de' where id = '00000000-0000-0000-0000-00000000000a';
    raise exception 'FAIL locale_check';
  exception when check_violation then null;
  end;
  insert into _results values ('locale_check');
end $$;
reset role;
select set_config('request.jwt.claims', '', false) \g /dev/null

do $$ begin
  if not exists (select 1 from pg_policies where schemaname='public' and tablename='profiles'
      and policyname='profiles_update_own' and cmd='UPDATE'
      and qual = '(id = auth.uid())' and with_check = '(id = auth.uid())'
      and roles = '{authenticated}') then
    raise exception 'FAIL update_policy_definition';
  end if;
  insert into _results values ('update_policy_definition');
end $$;

do $$ begin
  if not (select relrowsecurity and relforcerowsecurity from pg_class where oid = 'public.profiles'::regclass) then
    raise exception 'FAIL rls_enabled_and_forced';
  end if;
  insert into _results values ('rls_enabled_and_forced');
end $$;

do $$ begin
  if not exists (select 1 from pg_proc where oid = 'public.handle_new_user()'::regprocedure
      and prosecdef and proconfig @> array['search_path=""']) then
    raise exception 'FAIL trigger_fn_definer_empty_search_path';
  end if;
  insert into _results values ('trigger_fn_definer_empty_search_path');
end $$;

do $$ begin
  if has_table_privilege('anon', 'public.profiles', 'select,insert,update,delete')
     or has_table_privilege('authenticated', 'public.profiles', 'insert,delete') then
    raise exception 'FAIL no_extra_grants';
  end if;
  insert into _results values ('no_extra_grants');
end $$;

select 'PASSED ' || count(*) from _results;
