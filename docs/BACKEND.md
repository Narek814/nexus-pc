# Backend Nexus PC

## Назначение

Сервер аккаунтов, прав (entitlements) и оплаты. Провайдер — Supabase (Free, регион ЕС, Франкфурт; решение владельца, `docs/COMMERCIAL_PLAN.md`, раздел 0). Облачный проект создаёт и настраивает только владелец; в репозитории — схема, миграции и локальные тесты.

## Главные правила

- **Аккаунт не даёт доступа к ПК.** Сервер хранит только «визитку» ПК: без IP, MAC, отпечатков и ключей сопряжения. Доступ к ПК по-прежнему даёт только сопряжение в локальной сети.
- **RLS deny by default + тест на каждую политику.** На каждой таблице включены `ENABLE` и `FORCE ROW LEVEL SECURITY`, у `anon` и `authenticated` отозваны все права, выдаются только нужные. Каждая политика покрыта тестом «чужой user_id → пусто или отказ».
- **Минимум личных данных.** В `public.profiles` нет email и других личных данных; email живёт только в `auth.users` (управляет Supabase Auth).
- **Секреты не хранятся в репозитории.** Ключи Supabase (`service_role`), приватный ключ подписи токена прав, пароли SMTP задаются только в окружении облака или в локальном `.env` (игнорируется git). Тестовые ключи генерируются в тестах.

## Структура `backend/`

| Путь | Что это |
|------|---------|
| `supabase/config.toml` | Минимальный конфиг Supabase CLI (`project_id = "nexus-pc-local"`), без ключей |
| `supabase/migrations/*.sql` | Миграции схемы, применяются по порядку имён |
| `tests/sql/00_supabase_stub.sql` | Заглушка окружения Supabase только для тестов: роли `anon`, `authenticated`, `service_role`, схема `auth`, `auth.users`, `auth.uid()`, default privileges как в Supabase (новые таблицы, функции и последовательности `public` выдаются `anon`/`authenticated`/`service_role`, поэтому миграции обязаны делать `revoke`) |
| `tests/sql/[1-9]*.sql` | Тесты RLS (`10_profiles_rls.sql`, `20_my_pcs_rls.sql`, `30_entitlements.sql`); при неудаче — `RAISE EXCEPTION 'FAIL <имя проверки>'`, в конце строка `PASSED <n>` |
| `tests/run_sql_tests.sh` | Запуск тестов на временном кластере PostgreSQL |
| `tests/race_tests.sh` | Тесты гонок параллельными `psql`; подключается из `run_sql_tests.sh` |
| `supabase/functions/_shared/entitlement.ts` | Подпись токена прав ES256 (WebCrypto) |
| `tests/ts/entitlement.test.ts` | Тесты подписи (`node --test`) |
| `tests/py/verify_tokens.py` | Эталонная Python-проверка токенов и сверка с фикстурами |
| `tests/run_ts_tests.sh` | Запуск TS-тестов и Python-сверки |
| `.gitignore` | Исключает локальный `.env` и служебные каталоги CLI |

Таблицы:

- `public.profiles` (`id` → `auth.users`, каскадное удаление; `created_at`; `locale` ∈ {`en`,`ru`}). Строка создаётся только триггером `on_auth_user_created` (SECURITY DEFINER, `search_path = ''`). Пользователь видит и меняет (только `locale`) свой профиль; вставка и удаление клиентом запрещены.
- `public.my_pcs` — «визитка» ПК в аккаунте:
  - `pc_id` (uuid, PK) — идентификатор ПК, как `pcId` в токене прав;
  - `user_id` (uuid, не null) → `auth.users`, каскадное удаление; владелец ПК;
  - `display_name` (text, 1..64 символа) — имя ПК для показа;
  - `platform` (text) ∈ {`windows`};
  - `added_at` (timestamptz, по умолчанию `now()`) — когда ПК привязан;
  - `last_seen_at` (timestamptz, может быть null) — последний вход ПК.

  **Запрещено хранить** в этой и любой другой таблице сетевые и аппаратные данные ПК: IP-адреса, MAC, имена хостов в сети, отпечатки оборудования и сертификатов, ключи и токены сопряжения. Тест `exact_columns_no_network_data` сверяет точный список колонок через `information_schema`; новая колонка — только через решение владельца.

  Права: `authenticated` видит, переименовывает (только `display_name`) и удаляет только свои ПК (политики `my_pcs_select_own`, `my_pcs_update_own`, `my_pcs_delete_own`); вставка клиентом запрещена — запись создаёт сервер при входе ПК (E3-5, `service_role`). У `anon` прав нет. Лимит — не больше 20 ПК на пользователя: триггер `my_pcs_limit` (BEFORE INSERT / UPDATE OF `user_id`, функция `my_pcs_enforce_limit`, SECURITY DEFINER, `search_path = ''`, advisory-блокировка по пользователю против гонок) действует для всех ролей, включая `service_role` и владельца таблицы.
- `public.subscriptions` — подписки (заготовка под E7; данных карт нет): `id`, `user_id` → `auth.users` (каскад), `source` ∈ {`manual`,`web`,`appstore`}, `status` ∈ {`active`,`canceled`,`refunded`,`revoked`,`expired`}, `product` ∈ {`pro_monthly`,`pro_yearly`}, `current_period_end`, `revoked_at`, `created_at`, `updated_at`. `authenticated` только читает свои строки (`subscriptions_select_own`); пишет только `service_role`; у `anon` прав нет.
- `public.payment_events` (`provider`, `event_id`, `received_at`; PK `(provider, event_id)`) — журнал идемпотентности webhooks для E7. Доступ только у `service_role` (select, insert), политик нет.

Функция `public.entitlement_for_pc(p_pc_id uuid) → (state, plan, features[])` — SECURITY DEFINER, `search_path = ''`, EXECUTE только у `service_role`. Права считаются **по владельцу ПК** (`my_pcs.user_id`), а не по подключившемуся:

| Условие | state | plan | features |
|---------|-------|------|----------|
| ПК не привязан | `unlinked` | `free` | `{}` |
| есть подписка `active`/`canceled` с `current_period_end > now()` | `active` | `pro` | `profiles`, `rgb.sync`, `automations`, `monitoring.extended`, `files.unlimited` |
| иначе, последняя подписка (по `updated_at`) `refunded`/`revoked` | `revoked` | `free` | `{}` |
| иначе (нет подписки, срок прошёл, `expired`) | `active` | `free` | `{}` |

Граничные случаи (тесты `period_end_equals_now_free`, `period_end_null_active_free`, `tie_broken_by_id_stable`):
- `current_period_end = now()` — срок уже не действует (сравнение строгое `>`): `active`/`free`.
- `active` с `current_period_end = NULL` — Pro **не** даётся (`NULL > now()` ложно): `active`/`free`. Подписка без даты окончания считается неоплаченной; сервер оплаты обязан всегда ставить дату.
- Несколько подписок с одинаковыми `updated_at` и `created_at` — «последняя» определяется по `id desc`; результат стабилен между вызовами.

## Вход ПК и лимиты частоты (миграция `20261123000005_pc_login.sql`)

Схема device-code: ПК показывает короткий код, пользователь вводит его в приложении, ПК опрашивает сервер. Коды генерирует Edge Function; в базу попадают **только SHA-256 хэши** (32 байта, `check`), открытые коды не хранятся.

Таблицы (RLS включена и принудительна, политик нет, доступ только у `service_role`; `anon` и `authenticated` прав не имеют):

- `public.pc_keys` (`pc_id` PK → `my_pcs` с каскадным удалением, `public_key_spki` bytea, `created_at`) — публичный ключ ПК для подписи запросов (E3-6). Это ключ входа в аккаунт, а не ключ сопряжения: доступа к ПК он не даёт.
- `public.pc_login_codes` (`id`, `pc_id`, `public_key_spki`, `display_name`, `device_code_hash` unique, `user_code_hash` unique, `approved_by`, `approved_at`, `consumed_at`, `created_at`, `expires_at = now() + 10 мин`, `attempts` — число опросов в состоянии `pending`).
- `public.rate_limits` (`bucket`, `key`, `window_start`, `count`; PK из трёх первых) — счётчики фиксированного окна.

Функции (все SECURITY DEFINER, `search_path = ''`). Ошибки предметной области возвращаются **значением**, а не исключением: иначе откат транзакции стёр бы увеличение счётчика попыток.

| Функция | Кто вызывает | Результат |
|---------|--------------|-----------|
| `rate_limit_hit(bucket, key, limit, window_sec) → boolean` | `service_role` (Edge: вход, коды, refresh) | `true`, если попытка в пределах лимита; счётчик растёт при каждом вызове |
| `pc_login_start(pc_id, spki, name, device_code_hash, user_code_hash) → text` | `service_role` | `ok` / `rate.limited` (больше 5 стартов за 10 мин на `pc_id`) |
| `pc_login_approve(user_code_hash) → text` | `authenticated`, `approved_by = auth.uid()` | `ok` / `code.invalid` / `rate.limited` (больше 10 попыток за 10 мин на пользователя, неудачные тоже считаются) |
| `pc_login_consume(device_code_hash) → (status, pc_id, user_id, rebound)` | `service_role` | `ok` / `pending` / `code.invalid` / `pcs.limit` |
| `cleanup()` | `service_role`; расписание pg_cron — в облаке, позже | удаляет просроченные и использованные коды и окна `rate_limits` старше суток |
| `pc_revoke_refresh_tokens(pc_id, old_user)` | только изнутри `pc_login_consume` | заглушка; тело — в E3-6 |

Правила:
- `pc_login_approve`: неизвестный, просроченный, уже одобренный и использованный код дают одинаковый `code.invalid` одним и тем же путём (одна проба уникального индекса, один шаг счётчика) — существование кода не раскрывается ни ответом, ни побочным эффектом.
- `pc_login_consume` одноразовый: строка кода блокируется `FOR UPDATE`, из параллельных вызовов `ok` получает ровно один. Без одобрения — `pending`; использованный, просроченный, неизвестный — `code.invalid`.
- После одобрения создаётся или обновляется `my_pcs` (имя из кода, `platform = windows`) и `pc_keys`. Если ПК был привязан к другому аккаунту — **перепривязка к одобрившему** (решение владельца), `rebound = true` и вызов `pc_revoke_refresh_tokens` для старой привязки.
- Лимит 20 ПК соблюдается и при consume: при переполнении — `pcs.limit`, код не тратится и годен до истечения срока.

Тесты: `tests/sql/50_pc_login.sql` (44 проверки) и `tests/race_tests.sh` (вызывается из `run_sql_tests.sh`; параллельные сессии `psql`: 4 одновременных consume одного кода → ровно один `ok`; 25 одновременных consume для одного аккаунта → ровно 20 ПК).

## Токен прав (`supabase/functions/_shared/entitlement.ts`)

`signEntitlement({kid, privateKeyPkcs8B64, iss, sub, pcId, plan, features, now, ttlSec, graceSec})` → `kid "." b64u(payload) "." b64u(sig)` по `docs/API.md` («Токен прав»). Только WebCrypto (`crypto.subtle`), без пакетов; работает в Deno и Node 22. Payload — ровно 12 полей схемы, `iat = now`, `exp = iat + ttlSec`, `graceUntil = exp + graceSec`, `jti = crypto.randomUUID()`. Подпись ES256 (P1363, 64 байта) над ASCII `kid "." payload_b64`. Исключение при `ttlSec` вне 1..86400, `graceSec` вне 0..604800, `kid` не `[A-Za-z0-9_-]{1,64}`, полях не по схеме, токене длиннее 4096 символов; текст исключения ключ не содержит. Free тоже получает токен (`plan: "free"`, `features: []`). Рабочий `iss` задаётся после выбора домена; в тестах — `nexus-test-issuer`.

Эталонная проверка — `tests/py/verify_tokens.py` (Python, `cryptography`): порядок проверок из `docs/API.md`, ключ A выводится из публичной строки, как в `tools/gen_entitlement_fixtures.py`; приватных ключей в репозитории нет.

`state` согласован с `entitlementState` из `docs/API.md` («Планы»): `active` — сервер выдаёт действующий токен (Pro или Free); `revoked` — токен Pro не выдаётся, агент сразу переходит на Free. `grace`, `expired`, `none` вычисляет агент, сервер их не возвращает. `unlinked` — внутреннее значение сервера, агенту не передаётся (токен не выдаётся).

## Как запустить тесты

```
bash backend/tests/run_sql_tests.sh
```

Нужен только PostgreSQL 16 (бинарники; путь меняется через `PG_BIN`, по умолчанию `/usr/lib/postgresql/16/bin`). Сеть и Docker не нужны. Скрипт создаёт кластер в `mktemp -d` вне репозитория, слушает только Unix-сокет на свободном порту, применяет заглушку, миграции и тесты с `ON_ERROR_STOP`, печатает число пройденных проверок и удаляет кластер при выходе. Под root запускает PostgreSQL от пользователя `postgres`.

Код выхода 0 — все проверки прошли; иначе — ненулевой код и имя упавшей проверки.

```
bash backend/tests/run_ts_tests.sh
```

Нужны Node 22 (`node --test`, `.ts` без сборки) и Python 3 с `cryptography` (ставится во временный venv вне репозитория; путь к интерпретатору — `PYTHON`). Сначала `verify_tokens.py --fixtures` сверяет все кейсы `shared/protocol/fixtures/entitlements.v1.json` с ожидаемыми `ok`/`reason`, затем `tests/ts/entitlement.test.ts` подписывает токены и отдаёт 12 из них на Python-проверку.

## Переменные окружения

Имена (значения — только вне репозитория): `SUPABASE_URL`, `SUPABASE_SERVICE_ROLE_KEY`, `ENT_SIGNING_KEY_PKCS8_B64`, `ENT_KID`, `ENT_ISS`.
