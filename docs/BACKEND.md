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

  Права: `authenticated` видит и переименовывает (только `display_name`) только свои ПК (политики `my_pcs_select_own`, `my_pcs_update_own`); прямого DELETE у клиента нет (ни права, ни политики) — ПК удаляется только через `pc_release`, который отзывает refresh-токены и аннулирует живые коды входа (E3-5c); вставка клиентом запрещена — запись создаёт сервер при входе ПК (E3-5, `service_role`). У `anon` прав нет. Лимит — не больше 20 ПК на пользователя: триггер `my_pcs_limit` (BEFORE INSERT / UPDATE OF `user_id`, функция `my_pcs_enforce_limit`, SECURITY DEFINER, `search_path = ''`, advisory-блокировка по пользователю против гонок) действует для всех ролей, включая `service_role` и владельца таблицы.
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

Схема device-code: ПК показывает короткий код, пользователь вводит его в приложении, ПК опрашивает сервер. Коды генерирует Edge Function (E3-7); в базу попадают **только 32-байтные хэши** (`check`), открытые коды не хранятся. Хэш — **HMAC-SHA-256 с секретом (pepper) в секретах Supabase Edge**, переменная `CODE_HASH_PEPPER` (не в БД, не в репозитории): утечка таблицы не позволяет перебрать короткий `user_code` офлайн. Формат в SQL не меняется (32 байта); тесты используют SHA-256 как заменитель.

Миграция правится на месте, а не новой `…000005b`: она ещё не применялась в облаке, а смена сигнатур `pc_login_start`/`pc_login_consume` новой миграцией потребовала бы `drop function` и дублирования всего кода.

Таблицы (RLS включена и принудительна, политик нет, доступ только у `service_role`; `anon` и `authenticated` прав не имеют):

- `public.pc_keys` (`pc_id` PK → `my_pcs` с каскадным удалением, `public_key_spki` bytea, `created_at`) — публичный ключ ПК для подписи запросов. Это ключ входа в аккаунт, а не ключ сопряжения: доступа к ПК он не даёт.
- `public.pc_login_codes` (`id`, `pc_id`, `public_key_spki` — новый ключ, `prev_key_spki` — ключ из `pc_keys` на момент start или null при первой привязке, `display_name`, `device_code_hash` unique, `user_code_hash` unique, `described_by`/`described_at`, `approved_by`/`approved_at`, `consumed_at`, `created_at`, `expires_at = now() + 5 мин`, `last_poll_at`, `attempts` — число опросов).
- `public.pc_login_nonces` (`nonce` bytea 16..32 байт PK, `created_at`) — одноразовые nonce подписанных запросов start и consume (общее пространство).
- `public.rate_limits` (`bucket`, `key`, `window_start`, `count`; PK из трёх первых) — счётчики фиксированного окна.

`display_name` (1..64 символа) не может содержать символы категорий Unicode Cc, Cf, Zl и Zp (управляющие, bidi-переключатели U+202A..U+202E и U+2066..U+2069, нулевой ширины, BOM, теги U+E0000.., разделители строк и абзацев U+2028/U+2029; список — Unicode 15.1). Одинаковые ограничения: `pc_login_codes_display_name_printable` и `my_pcs_display_name_printable` (последнее действует и на переименование пользователем). Тестовый кластер создаётся в UTF-8, как в Supabase.

### Подписи запросов ПК

Подпись ECDSA P-256 (ключ ПК, SPKI DER) проверяет Edge Function; SQL получает от неё **ключ, которым подпись проверена** (`p_signer_spki`), `nonce` и `ts` из подписанной строки, и сам проверяет, что это правильный ключ, что `ts` в пределах ±300 с от времени сервера (`request.stale`) и что nonce не использовался (`nonce.replay`; nonce записывается даже при последующем отказе). Строки для подписи (ASCII, поля через `|`, `b64u` — base64url без `=`):

```
nexus-pc-login-v1|start|{pcId}|{spkiNewB64u}|{sha256hex(name)}|{nonceB64u}|{ts}
nexus-pc-login-v1|consume|{pcId}|{sha256hex(deviceCode)}|{nonceB64u}|{ts}
```

`pcId` — uuid в нижнем регистре, `ts` — секунды Unix, nonce — 16..32 случайных байт (SQL отвергает другую длину: `request.stale`), `sha256hex(name)` — SHA-256 от UTF-8 имени ПК в нижнем hex (имя подписано, подменить его по дороге нельзя). Edge перед вызовом SQL берёт ключ из `pc_keys` (если есть) и проверяет подпись start им, иначе — новым ключом `spkiNew`; consume — ключом из кода (новым).

Правила привязки (решение владельца, раздел 0):
- **Первая привязка** (нет строки `my_pcs`): start подписан новым ключом (`p_signer_spki = p_spki`).
- **Перепривязка** — только если start подписан **старым ключом** из `pc_keys`. Без подписи, с чужим или только новым ключом — `pc.key_required`, строка кода не создаётся, счётчик start не растёт. Строка `my_pcs` без `pc_keys` (сейчас невозможна) тоже даёт `pc.key_required`.
- Либо после того, как владелец **отпустил ПК** (`pc_release`) или удалил его из `my_pcs` (каскадно удаляется `pc_keys`) — тогда следующий start снова первая привязка. Отдельного «подтвердить перепривязку» нет: это и есть `pc_release`.
- **consume** требует подписи новым ключом кода (proof-of-possession), иначе `pc.key_required`.
- При consume привязка должна совпадать с той, что была при start (`prev_key_spki` = текущий ключ `pc_keys`; при первой привязке — нет `my_pcs`), иначе код аннулируется и `code.invalid`. Так второй код того же нового `pc_id` или код, выданный до `pc_release`, не перехватывает ПК.

### Функции

Все SECURITY DEFINER, `search_path = ''`. Ошибки предметной области возвращаются **значением**, а не исключением: иначе откат транзакции стёр бы увеличение счётчика попыток и запись nonce.

| Функция | Кто вызывает | Результат |
|---------|--------------|-----------|
| `rate_limit_hit(bucket, key, limit, window_sec) → boolean` | `service_role` (Edge: вход, коды, refresh) | `true`, если попытка в пределах лимита; счётчик растёт при каждом вызове |
| `pc_login_start(pc_id, spki, name, device_code_hash, user_code_hash, signer_spki, nonce, ts) → text` | `service_role` | `ok` / `pc.key_required` / `request.stale` / `nonce.replay` / `rate.limited` (больше 5 принятых стартов за 10 мин на `pc_id` или уже 3 живых кода у `pc_id`) |
| `pc_login_describe(user_code_hash) → (status, display_name, requested_at)` | `authenticated` | шаг 1: `ok` с именем ПК и временем запроса (без `pc_id` и ключей) / `code.invalid` / `rate.limited`; помечает код `described_by = auth.uid()` |
| `pc_login_approve(user_code_hash) → text` | `authenticated` | шаг 2: `ok` / `code.invalid` / `rate.limited`; только если тот же пользователь сделал describe |
| `pc_login_consume(device_code_hash, refresh_hash, signer_spki, nonce, ts) → (status, pc_id, user_id, rebound)` | `service_role` | `ok` / `pending` / `slow_down` / `code.invalid` / `pcs.limit` / `pc.key_required` / `request.stale` / `nonce.replay`; при `ok` сохраняет `refresh_hash` как первый refresh новой семьи (E3-6); `refresh_hash` не 32 байта — исключение |
| `pc_release(pc_id) → text` | `authenticated`, владелец | `ok` (удаляет `my_pcs` и каскадно `pc_keys`, вызывает `pc_revoke_refresh_tokens`, аннулирует живые коды ПК) / `pc.not_found` (и для чужого ПК) |
| `cleanup()` | `service_role`; расписание pg_cron — в облаке, позже | удаляет просроченные и использованные коды, nonce старше 1 ч, окна `rate_limits` старше суток, просроченные refresh-токены ПК |
| `pc_revoke_refresh_tokens(pc_id, old_user)` | только изнутри `pc_login_consume` и `pc_release` | вызывает `pc_revoke_all(pc_id)` (E3-6) |
| `pc_nonce_use`, `pc_lock`, `pc_approve_gate` | только изнутри функций выше | проверка ts/nonce; advisory-блокировка по `pc_id`; общий лимит describe/approve |

Правила:
- **Одобрение в два шага.** describe и approve делят один лимит: 10 вызовов за 10 мин на пользователя, затем 10000 за 10 мин на всех (бакет `pc_login_approve_global`, тревожный предохранитель; основная защита — лимит по аккаунту и лимит по IP в Edge). Счётчик пользователя растёт при каждом вызове, неудачные тоже; глобальный — только если лимит пользователя пройден, поэтому один аккаунт не может заблокировать одобрение всем. describe не перехватывает код, уже помеченный другим пользователем (`described_by`): тот же `code.invalid`, тот же путь и счётчик. Неизвестный, просроченный, аннулированный, одобренный и использованный код дают одинаковый `code.invalid` одним путём (одна проба уникального индекса) — существование кода не раскрывается.
- **Опрос consume:** не чаще 1 раза в 5 с (иначе `slow_down`, опрос всё равно считается и `last_poll_at` обновляется), не больше 150 опросов на код — 151-й аннулирует код (`code.invalid`). Неподписанные опросы не считаются.
- `pc_login_consume` одноразовый: сначала `pc_id` сериализуется advisory-блокировкой (`pc_lock`, её же берут start и `pc_release`), затем строка кода блокируется `FOR UPDATE`. Порядок блокировок везде один (`pc_lock` → строки кодов), поэтому взаимной блокировки release ↔ consume нет. Из параллельных consume одного кода или разных кодов одного `pc_id` `ok` получает ровно один, остальные — `code.invalid`, без `unique_violation`.
- После consume создаётся или обновляется `my_pcs` (имя из кода, `platform = windows`) и `pc_keys`. Если ПК был привязан к другому аккаунту (start подписан старым ключом) — перепривязка к одобрившему, `rebound = true` и вызов `pc_revoke_refresh_tokens` для старой привязки.
- Лимит 20 ПК соблюдается и при consume: при переполнении — `pcs.limit`, код не тратится и годен до истечения срока.
- **Лимиты start:** счётчик на `pc_id` (5 за 10 мин) растёт только для запросов, прошедших проверку ключа, — чужой неподписанный трафик не может исчерпать лимит настоящего ПК; не больше 3 живых кодов на `pc_id`. **Лимит по IP делает Edge Function** (SQL IP не видит и не хранит; `rate_limit_hit` с ключом-хэшем IP, без записи самого IP).

### Требования к Edge (E3-7)

Обязательно реализовать в Edge Function до запуска входа ПК:

- `p_signer_spki` — только ключ, которым **реально проверена** подпись: для start — ключ из `pc_keys`, если он есть, иначе `spkiNew` из запроса; для consume — ключ кода (новый ключ из `pc_login_codes`). Не брать ключ из полей запроса без проверки подписи.
- `nonce` и `ts` передавать в SQL только из подписанной строки; nonce — 16..32 байт.
- Сверять `pcId` подписанной строки с запросом и с кодом; для consume — `sha256hex(deviceCode)` из подписанной строки с присланным `deviceCode`; для start — `sha256hex(name)` с присланным именем.
- Хэши кодов — HMAC-SHA-256 с `CODE_HASH_PEPPER` (секрет Edge, не в БД и не в репозитории).
- `user_code` — 8 символов из алфавита в 32 символа без похожих (без `0/O`, `1/I/L` и т. п.), генерация через CSPRNG с отбраковкой (rejection sampling, без смещения по модулю). `deviceCode` — не меньше 128 бит из CSPRNG.
- Лимиты по хэшу IP (`rate_limit_hit`, ключ — HMAC от IP, сам IP не хранится) для start, consume, describe и approve, плюс общий лимит start на всех.
- Строгий разбор SPKI: только `id-ecPublicKey` с кривой P-256 (`prime256v1`), несжатая точка на кривой, без лишних байт; другое — отказ до вызова SQL.
- До запуска — расписание `cleanup()` в pg_cron (например, раз в 10 мин).

Тесты: `tests/sql/50_pc_login.sql` (102 проверки) и `tests/race_tests.sh` (вызывается из `run_sql_tests.sh`; параллельные сессии `psql`: 4 одновременных consume одного кода → ровно один `ok`; 25 одновременных consume для одного аккаунта → ровно 20 ПК; два одобренных кода одного нового `pc_id`, по два consume на каждый → ровно один `ok`, ни одного исключения; один nonce в 4 параллельных start → ровно один `ok`; `pc_release` владельцем параллельно с consume одобренного кода перепривязки, 16 ПК → без deadlock и ошибок, на каждый ПК выигрывает ровно одно из двух, итоговое состояние соответствует победителю, refresh старой привязки отозваны).

## Refresh-токены ПК (миграция `20261123000006_pc_refresh.sql`, E3-6)

Решение владельца: срок refresh ПК — **90 дней, скользящий** (каждая ротация выдаёт новый токен на 90 дней). Токен генерирует Edge Function (не меньше 256 бит из CSPRNG); в базу попадает только 32-байтный хэш (HMAC-SHA-256 с `CODE_HASH_PEPPER` или отдельным pepper — решает E3-7; тесты используют SHA-256).

Таблица `public.pc_refresh_tokens` (`id`, `pc_id` → `my_pcs` с каскадным удалением, `family_id`, `token_hash` unique, `created_at`, `used_at`, `revoked_at`, `expires_at = now() + 90 дней`). RLS включена и принудительна, политик нет, права только у `service_role`. Привязки к пользователю в строке нет: владелец берётся из `my_pcs`, поэтому смена владельца и удаление ПК сразу действуют на токены. У ПК одна живая семья: выдача новой семьи отзывает остальные.

| Функция | Кто вызывает | Результат |
|---------|--------------|-----------|
| `pc_rotate_refresh(old_hash, new_hash, pc_id, signer_spki, nonce, ts) → (status, user_id)` | `service_role` (Edge, подписанный запрос ПК) | `ok` + владелец ПК (Edge выдаёт по нему токен прав) / `reuse.detected` / `revoked` / `rate.limited` / `pc.key_required` / `request.stale` / `nonce.replay`; `new_hash` не 32 байта — исключение |
| `pc_issue_refresh(pc_id, hash) → id` | только изнутри `pc_login_consume` при `ok` | новая семья с первым токеном; прочие семьи ПК отзываются |
| `pc_revoke_all(pc_id)` | только изнутри (`pc_issue_refresh`, `pc_revoke_refresh_tokens` ← перепривязка и `pc_release`) | отзывает все живые токены ПК |

Правила ротации:
- Действующий неиспользованный токен → `used_at`, новый токен в той же семье, `ok`.
- Уже использованный → отзывается **вся семья**, `reuse.detected` (клон; нужен повторный вход ПК). Новый токен не выдаётся.
- Отозванный, просроченный, неизвестный, чужого `pc_id`, а также ПК, удалённый из `my_pcs`, → одинаковый `revoked`.
- Подпись запроса — ключом из `pc_keys` (`p_signer_spki`), иначе `pc.key_required`. Лимит — 30 принятых ротаций в час на `pc_id` (бакет `pc_refresh`, считается после проверки ключа).
- Первый refresh выдаётся только в `pc_login_consume`, в той же транзакции, что возвращает `ok`: Edge генерирует токен на каждый опрос и передаёт его хэш; сохраняется он ровно один раз — при `ok`. Отдельной выдачи refresh для `service_role` нет. Edge отдаёт токен ПК только при `ok`.
- Перепривязка к другому аккаунту и `pc_release` отзывают токены старой привязки (`pc_revoke_refresh_tokens` → `pc_revoke_all`); удаление строки `my_pcs` удаляет их каскадно.

Подписанная строка ротации (проверяет Edge, E3-7): `nexus-pc-login-v1|refresh|{pcId}|{sha256hex(refreshToken)}|{nonceB64u}|{ts}`.

**Nonce — общий механизм E3-5** (`pc_nonce_use` + `pc_login_nonces`), а не отдельная таблица `pc_request_nonces(pc_id, nonce)`. Причины: те же требования (16..32 байт, ±300 с, одноразовость, очистка в `cleanup()` через 1 ч) уже реализованы и проверены; глобальная уникальность nonce строже уникальности в паре `(pc_id, nonce)` и при 128+ битах случайности не даёт ложных отказов; подписанная строка содержит имя операции, так что nonce одной операции не подходит к другой.

**Порядок блокировок** (как в E3-5d): `pc_lock(pc_id)` → строка `my_pcs` (`FOR KEY SHARE` в rotate, `FOR UPDATE` в consume, `DELETE` в release) → строки токенов (`FOR UPDATE`). Взаимной блокировки rotate ↔ consume ↔ release нет; из параллельных ротаций одного токена `ok` получает ровно одна.

Функции `000005` (`pc_login_consume`, `pc_revoke_refresh_tokens`, `cleanup`) ссылаются на объекты `000006`; plpgsql разрешает имена при вызове, миграции применяются вместе. Миграции ещё не выпущены, поэтому `000005` поправлена на месте (новый параметр consume).

Тесты: `tests/sql/60_pc_refresh.sql` (47 проверок) и `tests/race_tests.sh`, проверка 6: четыре сеанса ротируют один токен → ровно один `ok`, один `reuse.detected`, два `revoked`, семья отозвана. Проверки 5 и 6 **детерминированы**: на время теста `pc_lock` подменяется тестовой версией, которая после настоящей блокировки ждёт разделяемую advisory-блокировку 4242 (`nexus.barrier = on`); сеанс-контроллер держит её, пока нужное число сеансов не встанет в ожидание блокировки (`pg_stat_activity`), затем отпускает; в конце `pc_lock` восстанавливается. В проверке 5 оба порядка (consume первым и release первым) задаются принудительно в каждом прогоне.

## Токен прав (`supabase/functions/_shared/entitlement.ts`)

`signEntitlement({kid, privateKeyPkcs8B64, iss, sub, pcId, plan, features, now, ttlSec, graceSec})` → `kid "." b64u(payload) "." b64u(sig)` по `docs/API.md` («Токен прав»). Только WebCrypto (`crypto.subtle`), без пакетов; работает в Deno и Node 22. Payload — ровно 12 полей схемы, `iat = now`, `exp = iat + ttlSec`, `graceUntil = exp + graceSec`, `jti = crypto.randomUUID()`. Подпись ES256 (P1363, 64 байта) над ASCII `kid "." payload_b64`. Исключение при `ttlSec` вне 1..86400, `graceSec` вне 0..604800, `kid` не `[A-Za-z0-9_-]{1,64}`, полях не по схеме, токене длиннее 4096 символов; текст исключения ключ не содержит. Free тоже получает токен (`plan: "free"`, `features: []`). Рабочий `iss` задаётся после выбора домена; в тестах — `nexus-test-issuer`.

Эталонная проверка — `tests/py/verify_tokens.py` (Python, `cryptography`): порядок проверок из `docs/API.md`, ключ A выводится из публичной строки, как в `tools/gen_entitlement_fixtures.py`; приватных ключей в репозитории нет.

`state` согласован с `entitlementState` из `docs/API.md` («Планы»): `active` — сервер выдаёт действующий токен (Pro или Free); `revoked` — токен Pro не выдаётся, агент сразу переходит на Free. `grace`, `expired`, `none` вычисляет агент, сервер их не возвращает. `unlinked` — внутреннее значение сервера, агенту не передаётся (токен не выдаётся).

## Как запустить тесты

```
bash backend/tests/run_sql_tests.sh
```

Нужен только PostgreSQL 16 (бинарники; путь меняется через `PG_BIN`, по умолчанию `/usr/lib/postgresql/16/bin`). Сеть и Docker не нужны. Скрипт создаёт кластер (UTF-8, `--locale=C`) в `mktemp -d` вне репозитория, слушает только Unix-сокет на свободном порту, применяет заглушку, миграции и тесты с `ON_ERROR_STOP`, печатает число пройденных проверок и удаляет кластер при выходе. Под root запускает PostgreSQL от пользователя `postgres`.

Код выхода 0 — все проверки прошли; иначе — ненулевой код и имя упавшей проверки.

```
bash backend/tests/run_ts_tests.sh
```

Нужны Node 22 (`node --test`, `.ts` без сборки) и Python 3 с `cryptography` (ставится во временный venv вне репозитория; путь к интерпретатору — `PYTHON`). Сначала `verify_tokens.py --fixtures` сверяет все кейсы `shared/protocol/fixtures/entitlements.v1.json` с ожидаемыми `ok`/`reason`, затем `tests/ts/entitlement.test.ts` подписывает токены и отдаёт 12 из них на Python-проверку.

## Переменные окружения

Имена (значения — только вне репозитория): `SUPABASE_URL`, `SUPABASE_SERVICE_ROLE_KEY`, `ENT_SIGNING_KEY_PKCS8_B64`, `ENT_KID`, `ENT_ISS`.
Для входа ПК (E3-7) нужна ещё `CODE_HASH_PEPPER` — секрет HMAC для хэшей кодов; в `.env.example` её добавляет главная сессия.
