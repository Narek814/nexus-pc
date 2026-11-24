-- Tests for pc_id_from_spki and the pcId <-> key check in pc_login_start (E4-2).
-- Vectors are read from shared/fixtures/pc-id/derive.v1.json (path passed by
-- run_sql_tests.sh as :pcid_fixture; the test cluster runs as superuser).
\set ON_ERROR_STOP on

create temp table _results (name text);
grant insert on _results to authenticated, anon, service_role;

create temp table _fx as
  select pg_read_file(:'pcid_fixture')::jsonb as j;
grant select on _fx to service_role;

create function pg_temp.b64u(s text) returns bytea language sql immutable as $$
  select decode(rpad(translate(s, '-_', '+/'), (length(s) + 3) / 4 * 4, '='), 'base64') $$;
create function pg_temp.h(t text) returns bytea language sql immutable
  as $$ select sha256(convert_to(t, 'UTF8')) $$;
create function pg_temp.nn() returns bytea language sql volatile
  as $$ select uuid_send(gen_random_uuid()) $$;
grant execute on all functions in schema pg_temp to authenticated, anon, service_role;

-- Catalog: immutable, empty search_path, not a definer, no new extensions,
-- callable by service_role only.
do $$ declare r text; begin
  if not exists (select 1 from pg_proc where oid = 'public.pc_id_from_spki(bytea)'::regprocedure
                 and provolatile = 'i' and not prosecdef and proconfig @> array['search_path=""']
                 and prorettype = 'uuid'::regtype) then
    raise exception 'FAIL derive_catalog';
  end if;
  foreach r in array array['anon','authenticated'] loop
    if has_function_privilege(r, 'public.pc_id_from_spki(bytea)', 'execute') then
      raise exception 'FAIL derive_no_api_execute %', r;
    end if;
  end loop;
  if exists (select 1 from pg_extension where extname not in ('plpgsql')) then
    raise exception 'FAIL derive_no_extensions';
  end if;
  insert into _results values ('derive_catalog'), ('derive_no_api_execute'), ('derive_no_extensions');
end $$;

-- All fixture vectors.
do $$ declare v jsonb; n int := 0; begin
  for v in select jsonb_array_elements(j -> 'valid') from _fx loop
    n := n + 1;
    if public.pc_id_from_spki(pg_temp.b64u(v ->> 'spkiB64u')) is distinct from (v ->> 'pcId')::uuid
       or public.pc_id_from_spki(pg_temp.b64u(v ->> 'spkiB64u'))::text <> v ->> 'pcId' then
      raise exception 'FAIL derive_valid_vectors (%)', v ->> 'name';
    end if;
  end loop;
  if n < 5 then raise exception 'FAIL derive_valid_vectors (count %)', n; end if;
  n := 0;
  for v in select jsonb_array_elements(j -> 'invalid') from _fx loop
    n := n + 1;
    if public.pc_id_from_spki(pg_temp.b64u(v ->> 'spkiB64u')) is not null then
      raise exception 'FAIL derive_invalid_vectors (%)', v ->> 'name';
    end if;
  end loop;
  if n < 5 then raise exception 'FAIL derive_invalid_vectors (count %)', n; end if;
  if public.pc_id_from_spki(null) is not null or public.pc_id_from_spki(''::bytea) is not null then
    raise exception 'FAIL derive_null_empty';
  end if;
  insert into _results values ('derive_valid_vectors'), ('derive_invalid_vectors'), ('derive_null_empty');
end $$;

-- pc_login_start: first binding with a pcId not derived from spki is refused,
-- before the rate limit and without a row; the fixture pcId is accepted.
set role service_role;
do $$ declare k1 bytea; k2 bytea; id1 uuid; id2 uuid; s text; begin
  select pg_temp.b64u(j -> 'valid' -> 0 ->> 'spkiB64u'), (j -> 'valid' -> 0 ->> 'pcId')::uuid,
         pg_temp.b64u(j -> 'valid' -> 1 ->> 'spkiB64u'), (j -> 'valid' -> 1 ->> 'pcId')::uuid
    into k1, id1, k2, id2 from _fx;
  -- Foreign pcId (another key's), random pcId, invalid SPKI, null SPKI.
  s := public.pc_login_start(id2, k1, 'X', pg_temp.h('dx-1'), pg_temp.h('UX-1'), k1, pg_temp.nn(), now());
  if s <> 'pc.key_required' then raise exception 'FAIL start_foreign_pc_id_refused (%)', s; end if;
  s := public.pc_login_start(gen_random_uuid(), k1, 'X', pg_temp.h('dx-2'), pg_temp.h('UX-2'), k1, pg_temp.nn(), now());
  if s <> 'pc.key_required' then raise exception 'FAIL start_random_pc_id_refused (%)', s; end if;
  s := public.pc_login_start(id1, k1 || '\x00'::bytea, 'X', pg_temp.h('dx-3'), pg_temp.h('UX-3'),
         k1 || '\x00'::bytea, pg_temp.nn(), now());
  if s <> 'pc.key_required' then raise exception 'FAIL start_invalid_spki_refused (%)', s; end if;
  s := public.pc_login_start(id1, null, 'X', pg_temp.h('dx-4'), pg_temp.h('UX-4'), k1, pg_temp.nn(), now());
  if s <> 'pc.key_required' then raise exception 'FAIL start_null_spki_refused (%)', s; end if;
  if exists (select 1 from public.pc_login_codes where display_name = 'X')
     or exists (select 1 from public.rate_limits where bucket = 'pc_login_start'
                and key in (id1::text, id2::text)) then
    raise exception 'FAIL start_mismatch_no_row_not_counted';
  end if;
  s := public.pc_login_start(id1, k1, 'Fixture', pg_temp.h('dfx-1'), pg_temp.h('UFX-1'), k1, pg_temp.nn(), now());
  if s <> 'ok' then raise exception 'FAIL start_fixture_pc_id_ok (%)', s; end if;
  insert into _results values ('start_foreign_pc_id_refused'), ('start_random_pc_id_refused'),
    ('start_invalid_spki_refused'), ('start_null_spki_refused'), ('start_mismatch_no_row_not_counted'),
    ('start_fixture_pc_id_ok');
end $$;
reset role;

-- Stored key must equal p_spki (no rotation), even if pcId and signer fit.
-- A pc_keys row for id2 bound to key k1 cannot occur through the API; it is
-- planted here to test the check in isolation.
insert into auth.users (id, email) values ('00000000-0000-0000-0000-0000000055a1', 'pd@example.test');
do $$ declare k1 bytea; id2 uuid; begin
  select pg_temp.b64u(j -> 'valid' -> 0 ->> 'spkiB64u'), (j -> 'valid' -> 1 ->> 'pcId')::uuid
    into k1, id2 from _fx;
  insert into public.my_pcs (pc_id, user_id, display_name, platform)
    values (id2, '00000000-0000-0000-0000-0000000055a1', 'Planted', 'windows');
  insert into public.pc_keys values (id2, k1);
end $$;
set role service_role;
do $$ declare k1 bytea; k2 bytea; id2 uuid; s text; begin
  select pg_temp.b64u(j -> 'valid' -> 0 ->> 'spkiB64u'), pg_temp.b64u(j -> 'valid' -> 1 ->> 'spkiB64u'),
         (j -> 'valid' -> 1 ->> 'pcId')::uuid into k1, k2, id2 from _fx;
  s := public.pc_login_start(id2, k2, 'Y', pg_temp.h('dy-1'), pg_temp.h('UY-1'), k1, pg_temp.nn(), now());
  if s <> 'pc.key_required' then raise exception 'FAIL start_stored_key_must_match (%)', s; end if;
  insert into _results values ('start_stored_key_must_match');
end $$;
reset role;

select 'PASSED ' || count(*) from _results;
