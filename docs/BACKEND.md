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

`state` согласован с `entitlementState` из `docs/API.md` («Планы»): `active` — сервер выдаёт действующий токен (Pro или Free); `revoked` — токен Pro не выдаётся, агент сразу переходит на Free. `grace`, `expired`, `none` вычисляет агент, сервер их не возвращает. `unlinked` — внутреннее значение сервера, агенту не передаётся (токен не выдаётся).

## Как запустить тесты

```
bash backend/tests/run_sql_tests.sh
```

Нужен только PostgreSQL 16 (бинарники; путь меняется через `PG_BIN`, по умолчанию `/usr/lib/postgresql/16/bin`). Сеть и Docker не нужны. Скрипт создаёт кластер в `mktemp -d` вне репозитория, слушает только Unix-сокет на свободном порту, применяет заглушку, миграции и тесты с `ON_ERROR_STOP`, печатает число пройденных проверок и удаляет кластер при выходе. Под root запускает PostgreSQL от пользователя `postgres`.

Код выхода 0 — все проверки прошли; иначе — ненулевой код и имя упавшей проверки.

## Переменные окружения

Имена (значения — только вне репозитория): `SUPABASE_URL`, `SUPABASE_SERVICE_ROLE_KEY`, `ENT_SIGNING_KEY_PKCS8_B64`, `ENT_KID`, `ENT_ISS`.
