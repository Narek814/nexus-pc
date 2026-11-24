-- RLS tests for public.my_pcs. Each check raises with its name on failure.
\set ON_ERROR_STOP on

insert into auth.users (id, email) values
  ('00000000-0000-0000-0000-0000000000a1', 'pa@example.test'),
  ('00000000-0000-0000-0000-0000000000b1', 'pb@example.test');

-- Rows are created server-side (service_role), as at PC sign-in.
set role service_role;
insert into public.my_pcs (pc_id, user_id, display_name, platform) values
  ('10000000-0000-0000-0000-0000000000a1', '00000000-0000-0000-0000-0000000000a1', 'A desktop', 'windows'),
  ('10000000-0000-0000-0000-0000000000b1', '00000000-0000-0000-0000-0000000000b1', 'B desktop', 'windows'),
  ('10000000-0000-0000-0000-0000000000b2', '00000000-0000-0000-0000-0000000000b1', 'B laptop', 'windows');
reset role;

create temp table _results (name text);
grant insert on _results to authenticated, anon, service_role;

-- As user A.
set role authenticated;
select set_config('request.jwt.claims', '{"sub":"00000000-0000-0000-0000-0000000000a1","role":"authenticated"}', false) \g /dev/null

do $$ begin
  if (select count(*) from public.my_pcs) <> 1
     or (select count(*) from public.my_pcs where pc_id = '10000000-0000-0000-0000-0000000000a1') <> 1 then
    raise exception 'FAIL own_pc_visible_only';
  end if;
  insert into _results values ('own_pc_visible_only');
end $$;

do $$ begin
  if (select count(*) from public.my_pcs where user_id = '00000000-0000-0000-0000-0000000000b1') <> 0 then
    raise exception 'FAIL other_pcs_hidden';
  end if;
  insert into _results values ('other_pcs_hidden');
end $$;

do $$ declare n int; begin
  update public.my_pcs set display_name = 'pwn' where pc_id = '10000000-0000-0000-0000-0000000000b1';
  get diagnostics n = row_count;
  if n <> 0 then raise exception 'FAIL update_other_pc_zero_rows'; end if;
  insert into _results values ('update_other_pc_zero_rows');
end $$;

do $$ begin
  begin
    delete from public.my_pcs where pc_id = '10000000-0000-0000-0000-0000000000b1';
    raise exception 'FAIL delete_other_pc_denied';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('delete_other_pc_denied');
end $$;

do $$ declare n int; begin
  update public.my_pcs set display_name = 'Office' where pc_id = '10000000-0000-0000-0000-0000000000a1';
  get diagnostics n = row_count;
  if n <> 1 then raise exception 'FAIL update_own_display_name'; end if;
  insert into _results values ('update_own_display_name');
end $$;

do $$ begin
  begin
    update public.my_pcs set display_name = '' where pc_id = '10000000-0000-0000-0000-0000000000a1';
    raise exception 'FAIL display_name_length_check (empty)';
  exception when check_violation then null;
  end;
  begin
    update public.my_pcs set display_name = repeat('x', 65) where pc_id = '10000000-0000-0000-0000-0000000000a1';
    raise exception 'FAIL display_name_length_check (65)';
  exception when check_violation then null;
  end;
  insert into _results values ('display_name_length_check');
end $$;

-- E3-5d N6: Cc/Cf/Zl/Zp are rejected in my_pcs.display_name too.
do $$ declare bad text; i int := 0; begin
  foreach bad in array array['PC' || chr(7), chr(8238) || 'cp.exe', 'P' || chr(8203) || 'C',
      chr(65279) || 'PC', 'PC' || chr(917569), 'PC' || chr(133), 'PC' || chr(8232) || 'x',
      'PC' || chr(8233) || 'x'] loop
    i := i + 1;
    begin
      update public.my_pcs set display_name = bad where pc_id = '10000000-0000-0000-0000-0000000000a1';
      raise exception 'FAIL my_pcs_display_name_printable (%)', i;
    exception when check_violation then null;
    end;
  end loop;
  update public.my_pcs set display_name = 'Мой ПК 🎮 — Dev' where pc_id = '10000000-0000-0000-0000-0000000000a1';
  update public.my_pcs set display_name = 'Office' where pc_id = '10000000-0000-0000-0000-0000000000a1';
  insert into _results values ('my_pcs_display_name_printable'), ('my_pcs_display_name_allows_unicode');
end $$;

do $$ begin
  begin
    update public.my_pcs set user_id = '00000000-0000-0000-0000-0000000000b1' where pc_id = '10000000-0000-0000-0000-0000000000a1';
    raise exception 'FAIL update_user_id_denied';
  exception when insufficient_privilege then null;
  end;
  begin
    update public.my_pcs set last_seen_at = now(), platform = 'windows' where pc_id = '10000000-0000-0000-0000-0000000000a1';
    raise exception 'FAIL update_other_columns_denied';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('update_only_display_name');
end $$;

do $$ begin
  begin
    insert into public.my_pcs (pc_id, user_id, display_name, platform)
      values ('10000000-0000-0000-0000-0000000000cc', '00000000-0000-0000-0000-0000000000a1', 'x', 'windows');
    raise exception 'FAIL insert_denied';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('insert_denied');
end $$;

-- As B: own delete is denied too (a PC is removed only via pc_release).
select set_config('request.jwt.claims', '{"sub":"00000000-0000-0000-0000-0000000000b1","role":"authenticated"}', false) \g /dev/null
do $$ begin
  if (select display_name from public.my_pcs where pc_id = '10000000-0000-0000-0000-0000000000b1') <> 'B desktop' then
    raise exception 'FAIL other_pc_unchanged';
  end if;
  begin
    delete from public.my_pcs where pc_id = '10000000-0000-0000-0000-0000000000b2';
    raise exception 'FAIL delete_own_pc_denied';
  exception when insufficient_privilege then null;
  end;
  if not exists (select 1 from public.my_pcs where pc_id = '10000000-0000-0000-0000-0000000000b2') then
    raise exception 'FAIL delete_own_pc_denied (row gone)';
  end if;
  insert into _results values ('delete_own_pc_denied');
end $$;

-- As anon (claims of B still set; must not help).
reset role;
set role anon;
do $$ declare n int; begin
  begin
    select count(*) into n from public.my_pcs;
    if n <> 0 then raise exception 'FAIL anon_sees_nothing'; end if;
  exception when insufficient_privilege then null;
  end;
  begin
    update public.my_pcs set display_name = 'x';
    get diagnostics n = row_count;
    if n <> 0 then raise exception 'FAIL anon_update'; end if;
  exception when insufficient_privilege then null;
  end;
  begin
    delete from public.my_pcs;
    get diagnostics n = row_count;
    if n <> 0 then raise exception 'FAIL anon_delete'; end if;
  exception when insufficient_privilege then null;
  end;
  begin
    insert into public.my_pcs (pc_id, user_id, display_name, platform)
      values ('10000000-0000-0000-0000-0000000000dd', '00000000-0000-0000-0000-0000000000a1', 'x', 'windows');
    raise exception 'FAIL anon_insert';
  exception when insufficient_privilege then null;
  end;
  insert into _results values ('anon_denied');
end $$;
reset role;
select set_config('request.jwt.claims', '', false) \g /dev/null

-- Platform check and per-user limit, as service_role.
set role service_role;
do $$ begin
  begin
    insert into public.my_pcs (pc_id, user_id, display_name, platform)
      values ('10000000-0000-0000-0000-0000000000ee', '00000000-0000-0000-0000-0000000000a1', 'mac', 'macos');
    raise exception 'FAIL platform_check';
  exception when check_violation then null;
  end;
  insert into _results values ('platform_check');
end $$;

do $$ declare i int; begin
  -- A already has 1 PC; add 19 more to reach exactly 20.
  for i in 1..19 loop
    insert into public.my_pcs (pc_id, user_id, display_name, platform)
      values (gen_random_uuid(), '00000000-0000-0000-0000-0000000000a1', 'pc' || i, 'windows');
  end loop;
  if (select count(*) from public.my_pcs where user_id = '00000000-0000-0000-0000-0000000000a1') <> 20 then
    raise exception 'FAIL limit_allows_20';
  end if;
  insert into _results values ('limit_allows_20');
  begin
    insert into public.my_pcs (pc_id, user_id, display_name, platform)
      values (gen_random_uuid(), '00000000-0000-0000-0000-0000000000a1', 'pc21', 'windows');
    raise exception 'FAIL limit_rejects_21_service_role';
  exception when check_violation then null;
  end;
  insert into _results values ('limit_rejects_21_service_role');
  begin
    update public.my_pcs set user_id = '00000000-0000-0000-0000-0000000000a1'
      where pc_id = '10000000-0000-0000-0000-0000000000b1';
    raise exception 'FAIL limit_on_reassign';
  exception when check_violation then null;
  end;
  insert into _results values ('limit_on_reassign');
end $$;
reset role;

-- Table owner (superuser) is also bound by the trigger.
do $$ begin
  begin
    insert into public.my_pcs (pc_id, user_id, display_name, platform)
      values (gen_random_uuid(), '00000000-0000-0000-0000-0000000000a1', 'pc21', 'windows');
    raise exception 'FAIL limit_rejects_21_owner';
  exception when check_violation then null;
  end;
  insert into _results values ('limit_rejects_21_owner');
end $$;

do $$ begin
  delete from auth.users where id = '00000000-0000-0000-0000-0000000000a1';
  if exists (select 1 from public.my_pcs where user_id = '00000000-0000-0000-0000-0000000000a1') then
    raise exception 'FAIL cascade_delete';
  end if;
  insert into _results values ('cascade_delete');
end $$;

-- Catalog checks.
do $$ begin
  if (select array_agg(column_name::text order by column_name) from information_schema.columns
      where table_schema = 'public' and table_name = 'my_pcs')
     <> array['added_at','display_name','last_seen_at','pc_id','platform','user_id'] then
    raise exception 'FAIL exact_columns_no_network_data';
  end if;
  insert into _results values ('exact_columns_no_network_data');
end $$;

do $$ begin
  if not (select relrowsecurity and relforcerowsecurity from pg_class where oid = 'public.my_pcs'::regclass) then
    raise exception 'FAIL rls_enabled_and_forced';
  end if;
  insert into _results values ('rls_enabled_and_forced');
end $$;

do $$ begin
  if (select count(*) from pg_policies where schemaname = 'public' and tablename = 'my_pcs') <> 2
     or not exists (select 1 from pg_policies where tablename = 'my_pcs' and policyname = 'my_pcs_select_own'
         and cmd = 'SELECT' and qual = '(user_id = auth.uid())' and roles = '{authenticated}')
     or not exists (select 1 from pg_policies where tablename = 'my_pcs' and policyname = 'my_pcs_update_own'
         and cmd = 'UPDATE' and qual = '(user_id = auth.uid())' and with_check = '(user_id = auth.uid())'
         and roles = '{authenticated}')
     or exists (select 1 from pg_policies where tablename = 'my_pcs' and cmd in ('DELETE', 'ALL')
         and roles && '{authenticated,public}') then
    raise exception 'FAIL policy_definitions';
  end if;
  insert into _results values ('policy_definitions');
end $$;

do $$ begin
  if not exists (select 1 from pg_proc where oid = 'public.my_pcs_enforce_limit()'::regprocedure
      and prosecdef and proconfig @> array['search_path=""']) then
    raise exception 'FAIL limit_fn_definer_empty_search_path';
  end if;
  if has_function_privilege('authenticated', 'public.my_pcs_enforce_limit()', 'execute')
     or has_function_privilege('anon', 'public.my_pcs_enforce_limit()', 'execute') then
    raise exception 'FAIL limit_fn_not_executable_by_clients';
  end if;
  if not exists (select 1 from pg_trigger where tgrelid = 'public.my_pcs'::regclass
      and tgname = 'my_pcs_limit' and tgenabled = 'O') then
    raise exception 'FAIL limit_trigger_present';
  end if;
  insert into _results values ('limit_fn_and_trigger');
end $$;

do $$ begin
  if has_table_privilege('anon', 'public.my_pcs', 'select,insert,update,delete,truncate,references,trigger')
     or has_table_privilege('authenticated', 'public.my_pcs', 'insert,delete,truncate,references,trigger')
     or has_table_privilege('authenticated', 'public.my_pcs', 'update')
     or has_any_column_privilege('anon', 'public.my_pcs', 'select,insert,update,references')
     or has_column_privilege('authenticated', 'public.my_pcs', 'user_id', 'update')
     or has_column_privilege('authenticated', 'public.my_pcs', 'pc_id', 'update')
     or has_column_privilege('authenticated', 'public.my_pcs', 'last_seen_at', 'update')
     or not has_column_privilege('authenticated', 'public.my_pcs', 'display_name', 'update')
     or has_any_column_privilege('authenticated', 'public.my_pcs', 'insert') then
    raise exception 'FAIL no_extra_grants';
  end if;
  insert into _results values ('no_extra_grants');
end $$;

select 'PASSED ' || count(*) from _results;
