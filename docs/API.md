# Nexus PC — Протокол v1

Источник правды: [`shared/schemas/request.v1.schema.json`](../shared/schemas/request.v1.schema.json).
Контрольные примеры: [`shared/protocol/fixtures/requests.v1.json`](../shared/protocol/fixtures/requests.v1.json), их прогоняют реализации на C# и на Swift.
Реализация на C#: `windows/Nexus.Core/Protocol`.

Статус: транспорт, сопряжение, вход и питание проверены на реальном ПК (P1b). Приложения, профили и блокировка (P1c) работают через Nexus.Tray и проверены тестами; на реальном ПК — ещё нет (docs/TESTING.md T1–T12). Мониторинг (`metrics.*`) — P4a.

## Транспорт

- `wss://<адрес>:47800/v1/ws` — TLS 1.2/1.3, самоподписанный сертификат ECDSA P-256.
- Клиент подключается **напрямую**, без системного прокси.
- Клиент **не проверяет цепочку сертификатов**, а сравнивает SHA-256 сертификата с отпечатком `fp` из ссылки сопряжения (pinning). Другой сертификат — отказ соединения.
- Агент принимает подключения только на `127.0.0.1`, частные адреса LAN (10/8, 172.16/12, 192.168/16): слушает все IPv4-адреса, а остальные подключения обрывает до TLS. Адреса в ссылке сопряжения — текущие адреса интерфейсов с выходом в сеть на момент открытия сопряжения.
- Запросы с заголовком `Origin` (браузеры) отклоняются.
- Одно текстовое сообщение — один JSON-конверт, не больше 64 КиБ (иначе соединение закрывается с кодом 1009). Бинарные кадры закрывают соединение.
- Лимит: 20 сообщений/с, всплеск до 40, иначе соединение закрывается.
- Анонимное соединение должно войти за 30 секунд (окно продлевается после успешного сопряжения), иначе оно закрывается.
- Три неудачных `auth.prove` подряд закрывают соединение.

## Сопряжение

1. На ПК в консоли администратора: `Nexus.Agent pair`. Открывается сессия на 2 минуты, печатаются код и ссылка:
   `nexuspc://pair?v=1&pc=<pcId>&name=<имя>&h=<адреса через запятую>&p=<порт>&fp=<SHA-256 сертификата>&c=<8 цифр>`
   (позже то же самое будет в QR-коде в Nexus.Tray).
2. Устройство создаёт ключ P-256 (на iPhone — в Secure Enclave), подключается с pinning по `fp` и отправляет `pair.request { code, deviceName, publicKey }`. `publicKey` — base64 DER SubjectPublicKeyInfo.
3. Обе стороны показывают **код проверки** из 6 цифр: `SHA-256("nexuspc-sas-v1\n" ‖ publicKey ‖ fp)`, первые 4 байта big-endian mod 1 000 000. Человек сверяет коды и подтверждает на ПК.
4. Ответ: `{ deviceId, pcId, permissions }`. По умолчанию: `power`, `apps`, `monitoring`.

Ограничения: одна сессия за раз; 5 неверных кодов закрывают сессию; если окно администратора закрыто или телефон отключился, ожидающий запрос отменяется.

## Вход (каждое соединение)

1. `auth.begin { deviceId }` → `{ nonce }` (32 байта base64). Ответ одинаков для существующих и несуществующих устройств.
2. Устройство подписывает (ECDSA P-256, SHA-256, подпись DER/X9.62 — формат iOS SecKey) строку UTF-8:
   ```
   nexuspc-auth-v1\n<pcId>\n<deviceId>\n<nonce base64>\n<fp заглавными>
   ```
   Отпечаток сертификата в подписи привязывает вход к TLS-соединению с этим агентом.
3. `auth.prove { signature }` → `{ deviceId, permissions, expiresAt }`. Один nonce — одна попытка.
4. Вход действует 1 час на этом соединении. Запись устройства перечитывается на каждом сообщении: отзыв устройства или изменение разрешений действуют сразу.

## Запрос

```json
{
  "v": 1,
  "id": "4f0c7a8e-2b1d-4e5f-9a3c-1d2e3f4a5b6c",
  "kind": "request",
  "type": "apps.launch",
  "ts": "2026-11-14T10:00:00Z",
  "pcId": "7d1c3b0a-5f7e-4c1e-9a52-2f0d9c4e8b11",
  "confirmed": false,
  "payload": { "appId": "steam" }
}
```

| Поле | Обязательно | Правило |
|------|-------------|---------|
| `v` | да | целое число, сейчас `1` |
| `id` | да | UUID, уникален для устройства (защита от повторов) |
| `kind` | да | `"request"` |
| `type` | да | команда из каталога ниже |
| `ts` | да | RFC 3339; расхождение с часами ПК не больше 5 минут |
| `pcId` | нет | UUID целевого ПК |
| `confirmed` | нет | `true` после подтверждения пользователем; обязателен для `critical` |
| `payload` | да | объект; неизвестные поля запрещены |

Сообщение длиннее 64 КиБ, неизвестные поля верхнего уровня и **повторяющиеся ключи** отклоняются.

## Каталог команд v1

| Команда | Разрешение | Риск | Payload |
|---------|-----------|------|---------|
| `system.hello` | — (без авторизации) | read | `{}` → `{ protocol, pcId, pcName, agentVersion, authenticated, capabilities[] }`; после входа — ещё необязательные поля плана (см. «Планы») |
| `account.refresh` | — (только после входа) | read | `{}` → `{ plan, features[], entitlementState, validUntil? }` (см. «Планы»). Агент отвечает текущим локальным состоянием (сервера ещё нет, E4); не чаще 1 раза в минуту на ПК и по одному, иначе `rate.limited` с `retryAfter`. В сборке без закреплённых ключей — `command.notImplemented` |
| `pair.request` | — (без авторизации) | normal | `{ code: 8 цифр, deviceName, publicKey }` |
| `auth.begin` | — (без авторизации) | read | `{ deviceId }` |
| `auth.prove` | — (без авторизации) | read | `{ signature }` |
| `pc.status` | monitoring | read | `{}` → `{ pcId, pcName, os, uptimeSeconds, agentVersion, userSession }` |
| `metrics.snapshot` | monitoring | read | `{}` → снимок, см. «Мониторинг» |
| `metrics.history` | monitoring | read | `{ range: "10m" \| "1h" \| "24h" }` |
| `power.shutdown` | power | **critical** | `{ delaySeconds?: 0..86400, force?: bool }` |
| `power.restart` | power | **critical** | `{ delaySeconds?: 0..86400, force?: bool }` |
| `power.sleep` | power | normal | `{}` |
| `power.lock` | power | normal | `{}` |
| `power.cancel` | power | normal | `{}` (отмена отложенного выключения) |
| `apps.list` | apps | read | `{}` |
| `apps.launch` | apps | normal | `{ appId: slug }` |
| `apps.close` | apps | normal | `{ appId: slug }` |
| `profiles.list` | apps | read | `{}` |
| `profiles.run` | apps | normal | `{ profileId: slug }` |
| `profiles.upsert` | apps | normal | `{ profile: { id: slug, name: 1..64 символа, appIds: slug[] ≤ 32, без повторов } }` |
| `profiles.delete` | apps | normal | `{ profileId: slug }` |
| `input.move` | input | normal | `{ dx, dy }` — целые ±2000, относительное смещение курсора |
| `input.button` | input | normal | `{ button: left\|right\|middle, action: click\|double\|down\|up }` |
| `input.scroll` | input | normal | `{ dx?, dy? }` — ±2400, 120 = один щелчок колеса; `dy > 0` — вверх |
| `input.text` | input | normal | `{ text: 1..256 символов }` — печать Unicode; `\n` → Enter, `\t` → Tab; управляющие символы запрещены |
| `input.key` | input | normal | `{ key, modifiers?: [ctrl, alt, shift, win] }` — только клавиши из списка (см. «Тачпад») |
| `input.media` | input | normal | `{ action: playPause\|next\|previous\|mute\|volumeDown\|volumeUp }` |
| `files.list` | files | read | `{}` → `{ folder, freeBytes, files: [{ name, size, modified }] }` |
| `files.upload.begin` | files | normal | `{ name, size, sha256 }` → `{ uploadId, offset }` |
| `files.upload.chunk` | files | normal | `{ uploadId, offset, data: base64 ≤ 512 КиБ }` → `{ received }` |
| `files.upload.finish` | files | normal | `{ uploadId }` → `{ name, size }` |
| `files.upload.cancel` | files | normal | `{ uploadId }` |
| `files.read` | files | read | `{ name, offset, length ≤ 524288 }` → `{ data, size, eof }` |
| `files.hash` | files | read | `{ name }` → `{ sha256, size }` |
| `clipboard.get` | clipboard | normal | `{}` → `{ text }` (`null`, если в буфере не текст) |
| `clipboard.set` | clipboard | normal | `{ text: 1..100 000 символов }` |
| `automations.list` | automation | read | `{}` → `{ automations: [ …, running, lastRun?, createdByName ], timeZone }` — только правила вызывающего устройства |
| `automations.upsert` | automation (+ разрешения действий) | normal (с выключением — `confirmed`) | `{ automation: { id, name, enabled?, trigger, onlyBetween?, actions[1..8] } }` |
| `automations.delete` / `automations.run` / `automations.stop` | automation (`run` — + разрешения действий правила) | normal (`run` с выключением — `confirmed`) | `{ automationId }`; чужое или ничейное правило — `automations.notFound` |
| `history.list` | monitoring | read | `{ limit?: 1..200, before?: ISO-время }` → `{ entries: [{ ts, kind, title, ok, detail?, device? }] }` |
| `screen.start` | screen | normal | `{}` — ПК спрашивает владельца при первом показе для устройства (до 2 мин) |
| `screen.frame` | screen | read | `{ maxWidth?: 320..1920, quality?: 20..95, known?: hash }` → `{ width, height, hash, jpeg? , unchanged? }` |
| `screen.stop` | screen | normal | `{}` |
| `screen.video` | screen | read | `{ after?: -1.., keyframe?, maxWidth?: 320..1920, bitrateKbps?: 300..20000 }` → `{ width, height, encoder, frames: [{ seq, key, data }] }` (H.264 Annex-B, ждёт до 250 мс) |
| `input.moveTo` | input | normal | `{ x, y }` — 0..65535 по основному монитору (картинка экрана) |
| `rgb.status` | rgb | read | `{}` → `{ adapter, available, reason?, state?, devices[{ index, name, type, leds, modes[] }], profiles[] }` |
| `rgb.apply` | rgb | normal | `{ color: "#RRGGBB", brightness: 0..100, effect: 1..64 символа, devices?: int[] }` |
| `rgb.profiles.upsert` | rgb | normal | `{ profile: { id: slug, name, color, brightness, effect } }` (≤ 32 профилей) |
| `rgb.profiles.delete` | rgb | normal | `{ profileId: slug }` |
| `rgb.profiles.apply` | rgb | normal | `{ profileId: slug }` |

`slug` — `^[a-z0-9][a-z0-9-]{0,63}$`. Это **никогда не путь**: путь к программе задаётся только на ПК.

`capabilities` в `system.hello` перечисляет команды, которые **работают прямо сейчас**: `power.lock`, `apps.*`, `profiles.*`, `input.*` — только пока Nexus.Tray запущен в сеансе человека за ПК (`pc.status.userSession = "available"`). Без трея они отвечают `session.unavailable`. Клиент скрывает остальные.

**Приложения и профили** выполняет Nexus.Tray от имени пользователя. `apps.list` → `{ apps: [{ id, name, canClose }] }` — **без путей к файлам**. Добавлять программы можно только на ПК. `apps.close` просит окна закрыться (как кнопка ×) и никогда не завершает процесс принудительно. `profiles.upsert` с телефона может ссылаться только на программы из списка ПК (иначе `profiles.invalid`). `profiles.run` → `{ started, results: [{ appId, ok, error? }] }`.

**Питание.** Windows показывает своё уведомление с именем устройства, выключение отменяется через `power.cancel`. Ответ содержит фактическую `delaySeconds` и `via`:
- `via: "userSession"` — без `force` и при запущенном трее: выключение из сеанса пользователя, программы могут спросить о сохранении; задержка — как запрошено.
- `via: "service"` — с `force` или без трея. Служба работает в сеансе SYSTEM и обязана выключать с `SHUTDOWN_FORCE_OTHERS` (иначе ошибка 1191 — найдено на реальном ПК), поэтому без `force` задержка не меньше 30 секунд. Мгновенно (0 с) — только с `force: true`: агент сначала отвечает, потом через 1 секунду действует; так же работает `power.sleep`. Режим Fast Startup (hybrid shutdown) не используется: с ним Wake-on-LAN работает хуже.
Wake-on-LAN — не команда агента (ПК выключен); его отправляет iPhone или wake proxy.

## Порядок проверок и коды ошибок

Проверки идут строго в этом порядке. Первая сработавшая определяет код ошибки.

1. `protocol.tooLarge`, `protocol.malformed`, `protocol.unsupportedVersion` — разбор конверта.
2. `command.unknown` — команды нет в каталоге.
3. `auth.required` — нужна авторизация.
4. `auth.permissionDenied` — у устройства нет разрешения.
5. `auth.clockSkew` — `ts` вне окна ±5 минут.
6. `auth.replay` — `id` уже использован (`rate.limited` при переполнении журнала).
7. `confirm.required` — команда `critical` без `confirmed: true`.
8. `command.invalidPayload` — payload не соответствует команде.

Отклонение на шагах 2–5 не «расходует» `id`.

После шага 8 обработчик проверяет план: `plan.required` / `plan.limit`. Эти коды никогда не выдаются раньше проверки разрешений устройства: устройство без разрешения получает `auth.permissionDenied`, даже если плана тоже не хватает.

Ошибки обработчиков (после проверок):

| Код | Когда |
|-----|-------|
| `auth.failed` | неверная подпись, неизвестное устройство, `auth.prove` без `auth.begin` |
| `pair.noActiveSession` / `pair.invalidCode` / `pair.busy` / `pair.rejected` / `pair.expired` / `pair.deviceLimit` | сопряжение |
| `power.failed` / `power.nothingToCancel` / `power.unsupported` | питание (`unsupported` — агент запущен не на Windows) |
| `command.notImplemented` | команда есть в протоколе, но не в этой сборке |
| `session.unavailable` | нужен Nexus.Tray, а он не запущен в сеансе человека за ПК |
| `apps.notFound` / `apps.launchFailed` / `apps.closeNotAllowed` | приложения |
| `profiles.notFound` / `profiles.invalid` / `profiles.limit` | профили (`invalid` — ссылка на программу не из списка ПК) |
| `power.lockFailed` | блокировка не удалась |
| `rgb.unavailable` | OpenRGB не запущен или SDK Server выключен |
| `files.notFound` / `files.offsetMismatch` / `files.hashMismatch` / `files.noSpace` / `files.uploadNotFound` / `files.failed` | файлы |
| `clipboard.failed` | буфер обмена занят другой программой |
| `automations.notFound` / `automations.limit` / `automations.running` | автоматизации |
| `screen.videoUnavailable` | на ПК нет кодировщика H.264 — телефон переходит на кадры JPEG |
| `screen.denied` / `screen.blocked` / `screen.notStarted` | показ экрана: отказ на ПК; запрещён в трее или экран заблокирован; сеанс не начат |
| `input.blocked` | экран ПК заблокирован, открыт UAC, или управление запрещено в меню трея |
| `plan.required` | функции нет в плане ПК; `error.details?: { feature, state? }` |
| `plan.limit` | превышен лимит Free; `error.details?: { feature, limit }` |
| `rate.limited` | также при частых `account.refresh`; `error.details?: { retryAfter }` (секунды) |
| `internal` | ошибка агента |

## Ответы

Схема: [`shared/schemas/response.v1.schema.json`](../shared/schemas/response.v1.schema.json).

```json
{ "v": 1, "id": "<id запроса>", "kind": "response", "ok": true,  "payload": { } }
{ "v": 1, "id": "<id запроса>", "kind": "response", "ok": false, "error": { "code": "auth.permissionDenied", "message": "…" } }
```
`error.details` — необязательный объект: `feature`, `state`, `limit`, `retryAfter`, другие поля запрещены; старые клиенты его игнорируют.

`id` равен `null`, если запрос не удалось разобрать. События (`kind: "event"`) появятся вместе с первым их источником.

## Планы (Free/Pro, ENT-1)

Решения — `docs/COMMERCIAL_PLAN.md`, раздел 0; безопасность — `SECURITY.md`, «Аккаунты и права». ENT-2: агент проверяет план, только если в сборке закреплены ключи сервера; до выпуска ключей поведение прежнее (нет маркера, нет полей плана, нет `plan.*`). `features[]` — функции из действующего токена (`active`/`grace`); для Free без токена список пуст. Идентификаторы: `profiles`, `rgb.sync`, `automations`, `monitoring.extended`, `files.unlimited`; `plan.limit` — `feature: "files"`. Действия правил `profile` и `rgb` требуют `profiles` и `rgb.sync`, в том числе при `automations.run` (иначе `plan.required`).

- Расширение аддитивное: `v: 1`; существующие команды, коды, порядок проверок 1–8, `auth.*` и сопряжение не меняются. Ни одна схема `auth.*` / `pair.*` не содержит полей аккаунта.
- Маркер `entitlements.v1` в `capabilities` значит, что агент понимает поля плана, `account.refresh` и `plan.*`. Может отдаваться и до входа: сообщает только о версии агента.
- Поля плана необязательны и бывают только в `hello` вошедшего соединения и в ответе `account.refresh`:
  - `plan`: `free` или `pro`; при `none`, `expired`, `revoked` — `free`;
  - `features[]` — функции, разрешённые сейчас (список идентификаторов утверждается вместе с матрицей Free/Pro);
  - `entitlementState`: `none` (токена нет, Free без сервера), `active`, `grace`, `expired`, `revoked` (сразу Free, без льготы);
  - `validUntil` (RFC 3339) — `exp` или `graceUntil`; при `none` и `revoked` поля нет.
  - `plan`, `features` и `entitlementState` идут вместе. До `auth.prove` `hello` не меняется. Email, `accountId` и `sub` в ответы не попадают никогда.
- `account.refresh` `{}`: служба сама запрашивает токен у сервера (телефон токенов не передаёт). Частые вызовы — `rate.limited` с `retryAfter`. Без связи — текущее состояние без ошибки.
- Список отозванных `kid` выпускается вместе с обновлением агента. В агенте закреплены рабочий и два запасных ключа.

### Токен прав

Схема payload: [`shared/schemas/entitlement.v1.schema.json`](../shared/schemas/entitlement.v1.schema.json). Примеры: [`shared/protocol/fixtures/entitlements.v1.json`](../shared/protocol/fixtures/entitlements.v1.json) — **ключи только тестовые**, приватный ключ нигде, кроме фикстур, не используется. По WebSocket токен не передаётся: это внутренний формат «сервер → служба».

```
<token> = kid "." base64url(payload) "." base64url(signature)
```

- Сначала, до любого разбора, проверяется длина: токен не длиннее 4096 символов. Затем — ровно три непустых сегмента.
- `kid` — символы `[A-Za-z0-9_-]`, длина 1..64; стоит только в первом сегменте; с таблицей ключей сравнивается побайтно, с учётом регистра (`Test-A` и `test-a` — разные `kid`). Поле `kid` в payload — лишнее поле, отказ.
- base64url строгий: только `[A-Za-z0-9_-]`. Отказ при `=`, пробелах, переносах, `+` или `/`, длине ≡ 1 (mod 4), ненулевых хвостовых битах (повторное кодирование должно дать ту же строку).
- `signature` — ES256: ECDSA P-256 + SHA-256, формат IEEE P1363 `r‖s`, ровно 64 байта (86 символов); 63, 65, 96 байт или DER — отказ.
- Подписываются ASCII-байты `kid "." payload_b64` — первые два сегмента с точкой, как получены; повторной сериализации и канонизации нет. Подменить `kid` нельзя: он покрыт подписью.
- Алгоритм определяется только по `kid` из таблицы ключей, закреплённой в агенте (сейчас только ES256). Полей с алгоритмом нет: `alg` — лишнее поле.
- **До проверки подписи JSON не разбирается.**

**Строгий JSON** — после подписи, до схемы, одинаково для всех реализаций:
- дубликат любого ключа на любом уровне вложенности — отказ;
- числа — только десятичные целые литералы `-?[0-9]+`, без ведущих нулей и без `-0`; дробная часть, экспонента, `x.0` — отказ; строка вместо числа и отрицательное время — отказ по схеме;
- реализация: .NET 10 — `JsonDocumentOptions { AllowDuplicateProperties = false }` или ручной обход `Utf8JsonReader` (числа — по сырым байтам `ValueSpan`); Python — `json.loads` с `object_pairs_hook`, `parse_float`, `parse_int` (отказ на `-0`), `parse_constant`; JavaScript и Swift — встроенные `JSON.parse` / `JSONSerialization` молча берут последний дубликат и не различают `1` и `1.0`, поэтому нужен собственный потоковый разбор с теми же правилами.

Поля payload — все обязательны, неизвестные — отказ, время — целые секунды Unix: `v` (`1`), `typ` (`"nexus-ent+v1"`), `iss` (точно равен значению, закреплённому в сборке агента; в фикстурах — `nexus-test-issuer`), `aud` (`"nexus-agent"`), `sub` (непрозрачный, клиентам не передаётся), `pcId` (UUID этого ПК), `plan` (`"free"`/`"pro"`), `features[]`, `iat`, `exp`, `graceUntil` (`0 < exp−iat ≤ 86400`, `0 ≤ graceUntil−exp ≤ 604800`, `iat ≤ now+300`), `jti` (UUID).

**Порядок проверки** (ENT-2; первая неудача — отказ, причина — `reason` фикстур): `format` → `revokedKid` → `unknownKid` → `algorithm` → `signature` → `json` → `schema` → `issuer` → `pcId` → `lifetime` → `grace` → `notYetValid` → `expired` (`now > graceUntil`).

После всех шагов агент принимает токен только с `iat` ≥ максимального принятого (иначе `stale`); после отзыва или перевода часов назад — только с более новым `iat`.

Сроки длиннее допустимых не принимаются, даже если подпись верна. Состояние: `now ≤ exp` — `active`; `exp < now ≤ graceUntil` — `grace`; позже — `expired`; отзыв, сообщённый сервером на связи, — сразу `revoked`, без льготы. При переводе часов назад льгота считается истёкшей до связи с сервером.

**Ключи.** В агенте по `kid` закреплены рабочий и два запасных открытых ключа; список отозванных `kid` выходит вместе с обновлением агента.

## Сервер прав (HTTPS, служба ПК и приложение)

Это не команды WebSocket: протокол v1, конверт, `capabilities`, `auth.*` и `pair.*` не меняются. Служба ПК и приложение обращаются к серверу аккаунтов (Supabase Edge Functions) по HTTPS. Телефон токены прав и refresh не получает и не передаёт. Устройство сервера — `docs/BACKEND.md`.

**Общие правила**
- `POST /functions/v1/<имя>`, `Content-Type: application/json`, тело — объект JSON в UTF-8, не больше 8 КБ. Неизвестное или отсутствующее поле, неверный тип, дубликат ключа — `bad.request`; другой метод — `405 bad.request`; тело больше 8 КБ — `413 bad.request`.
- Успех — `200` и объект ниже. Ошибка — `{"error":"<код>"}`; у `rate.limited` и `slow_down` ещё `"retryAfter": <секунды>` и заголовок `Retry-After`. Клиент ориентируется на `error`, HTTP-статус вторичен. `pending` — `202`.
- `b64u` — base64url без `=`. Время — секунды Unix.
- **Подпись ПК** — ECDSA P-256 + SHA-256, формат P1363 `r‖s` (64 байта, поле `sig` в b64u), над ASCII-строкой. Сервер собирает строку сам из полей запроса и проверяет подпись **до** обращения к базе:
  ```
  nexus-pc-login-v1|start|{pcId}|{spkiNewB64u}|{sha256hex(name)}|{nonceB64u}|{ts}
  nexus-pc-login-v1|consume|{pcId}|{sha256hex(deviceCode)}|{nonceB64u}|{ts}
  nexus-pc-login-v1|refresh|{pcId}|{sha256hex(refreshToken)}|{nonceB64u}|{ts}
  ```
  `pcId` — UUID в нижнем регистре; `nonce` — 16..32 случайных байт, новые на каждый запрос (и на повтор); `ts` — в пределах ±300 с от времени сервера; `sha256hex` — нижний hex; `spkiNew` — SPKI DER ключа P-256 (несжатая точка). Подпись другим ключом, сжатая точка, лишние байты — отказ.
- Запросы приложения — с `Authorization: Bearer <JWT Supabase>`; без JWT или с недействительным — `401 auth.required`.

### Служба ПК

**`pc-login-start`** — запросить код входа. Тело `{pcId, spki, name, nonce, ts, sig}`; `name` — 1..64 символа без Cc/Cf/Zl/Zp. Ключ подписи: если ПК уже привязан — старый ключ ПК, при первой привязке — `spki`.
Ответ `{deviceCode, userCode, expiresIn: 300, interval: 5}`: `userCode` — 8 символов из алфавита в 31 символ `23456789ABCDEFGHJKMNPQRSTUVWXYZ` (без 0, O, 1, I, L), показывается пользователю; `deviceCode` — секрет ПК, b64u, ≥ 128 бит.
Ошибки: `bad.request`, `pc.key_required`, `request.stale`, `nonce.replay`, `rate.limited`.

**`pc-login-poll`** — опрос после показа кода. Тело `{pcId, deviceCode, nonce, ts, sig}`, подпись новым ключом из start.
Ответ `{refreshToken, entitlement, entitlementState}`: `refreshToken` — b64u, 256 бит, 90 дней скользящий, служба хранит под DPAPI с ACL; `entitlement` — токен прав (см. «Токен прав»); `entitlementState` — `active` или `revoked`.
Ошибки: `pending` (ждать `interval`), `slow_down` (+`retryAfter`), `code.invalid` (код истёк, аннулирован или использован — заново start), `pcs.limit`, `pc.key_required`, `request.stale`, `nonce.replay`, `rate.limited`, `bad.request`.

**`pc-token`** — ротация refresh и новый токен прав (так служба выполняет `account.refresh`). Тело `{pcId, refreshToken, nonce, ts, sig}`, подпись ключом ПК.
Ответ `{refreshToken, entitlement, entitlementState}`; новый refresh заменяет старый. Повтор после потерянного ответа — тот же старый `refreshToken` с новыми `nonce`/`ts`/`sig` не позже 60 с: сервер вернёт тот же новый refresh.
Ошибки: `revoked` (без причины: отозван, истёк, неизвестен, ПК отвязан, подпись не тем ключом), `reuse.detected` (клон: семья отозвана; событие в журнал безопасности), `rate.limited`, `request.stale`, `nonce.replay`, `bad.request`. Служба: на `rate.limited`, `request.stale`, `nonce.replay` — повтор позже с новым nonce; на любой другой отказ — стирает refresh, переходит на Free и ждёт нового входа.

`entitlementState: "revoked"` — Pro отозван (возврат/отзыв): сервер выдаёт токен `plan:"free"`, агент сразу переходит в `revoked`, без льготы. Внутреннее состояние `unlinked` наружу не выходит (клиент получает `revoked`). `user_id`, email ПК не получает; `sub` в токене непрозрачен.

### Приложение (вошедший пользователь)

**`pc-login-describe`** — шаг 1. Тело `{userCode}` (регистр, пробелы и дефисы нормализует сервер). Ответ `{displayName, requestedAt}` (`requestedAt` — RFC 3339); `pcId` и ключей в ответе нет. Ошибки: `code.invalid` (неизвестный, просроченный, чужой, уже одобренный — неразличимы), `rate.limited`, `auth.required`, `bad.request`.

**`pc-login-approve`** — шаг 2, после того как пользователь сверил имя ПК. Тело `{userCode}`. Ответ `{}`. Ошибки: `code.invalid` (в том числе без describe этим же пользователем), `rate.limited`, `auth.required`, `bad.request`. describe и approve делят лимит: 10 за 10 мин на аккаунт плюс лимит по IP.

**`pc-unlink`** — отпустить ПК: отзывает его refresh и живые коды; следующий вход ПК — первая привязка. Тело `{pcId}`. Ответ `{}`. Ошибки: `pc.not_found` (и для чужого ПК), `rate.limited`, `auth.required`, `bad.request`.

**`delete-account`** (приложение/сайт). `Authorization: Bearer <JWT пользователя>`, тело `{}`. Удаляет аккаунт: все ПК пользователя отвязываются (их refresh отозваны, `pc-token` отвечает `revoked`), удаляются профиль и подписки, затем пользователь удаляется из Auth. Сопряжения ПК с телефонами не затрагиваются. Перед вызовом клиент заново входит паролем или кодом из письма: в `amr` проверенного JWT должен быть элемент с method `password` или `otp` не старше 300 с (не позже чем через 60 с), а `iat` и `last_sign_in_at` — не старше 300 с. Ответ `{}`. Ошибки: `bad.request` (тело не `{}`), `auth.required` (нет или недействительный JWT, anon), `reauth.required` (401: вход старше 5 минут — запросить пароль снова), `rate.limited` (`retryAfter`; 3 в час на пользователя плюс лимит IP), `internal` (сервер делает 3 попытки удаления в Auth за ~1 с; если не вышло, попытка не засчитывается в лимит 3/ч; повтор безопасен).

**Коды сервера прав:** `bad.request`, `auth.required`, `reauth.required`, `code.invalid`, `pending`, `slow_down`, `rate.limited`, `pcs.limit`, `pc.key_required`, `request.stale`, `nonce.replay`, `revoked`, `reuse.detected`, `pc.not_found`, `internal` (500, без подробностей). Это пространство отдельно от кодов WebSocket.

## Мониторинг (P4a)

`metrics.snapshot` → (единицы: %, °C, МГц, байты, байты/с; `null` — значение недоступно, причина в `unavailable`):

```json
{
  "ts": "2026-11-16T15:20:00.0000000+00:00",
  "cpu": { "usage": 12.5, "logicalProcessors": 16, "temperature": null, "clockMhz": null },
  "memory": { "usage": 41.2, "usedBytes": 13958643712, "totalBytes": 34359738368 },
  "gpus": [ { "name": "NVIDIA GeForce RTX 4070", "usage": 30, "temperature": 54,
              "memoryUsedBytes": 2147483648, "memoryTotalBytes": 12884901888,
              "coreClockMhz": 2475, "memoryClockMhz": 10501 } ],
  "disks": [ { "name": "C:\\", "totalBytes": 1000202039296, "freeBytes": 250050510848, "usage": 75 } ],
  "network": { "receiveBytesPerSecond": 1200.5, "sendBytesPerSecond": 300 },
  "unavailable": { "cpu.temperature": "Windows не отдаёт температуру процессора без драйвера датчиков…" }
}
```

Источники (без дополнительных пакетов): `GetSystemTimes` (загрузка CPU), `GlobalMemoryStatusEx` (память), NVML из драйвера NVIDIA (`nvml.dll`: загрузка, температура, видеопамять, частоты GPU), `DriveInfo` (диски), счётчики сетевых интерфейсов (скорость сети). Температура и частота процессора — через LibreHardwareMonitorLib (пакет процессора, у AMD — Tctl; частота — среднее по ядрам); без драйвера PawnIO значений нет — отдаются как недоступные с причиной. Видеокарты AMD/Intel — позже.

`metrics.history { range }` → `{ range, intervalSeconds, points: [{ t, cpu, memory, gpu, gpuTemperature, rx, tx }] }`. `10m` — точки каждые 2 с; `1h`, `24h` — средние за минуту. История хранится в памяти службы и начинается заново после её перезапуска (постоянная история — P6).

## Предупреждения и уведомления (P4b) — убраны

Команды `alerts.get`, `alerts.set`, `alerts.test` и шаг автоматизации `notify` удалены (2026-11-18). Старые правила с шагом `notify` загружаются без него; правило, где кроме него ничего не было, не загружается.

## Подсветка (P10)

Агент управляет подсветкой через **OpenRGB** (отдельная бесплатная программа на ПК) по его SDK-протоколу на `127.0.0.1:6742` — только локально, наружу ничего не открывается. Короткое соединение на каждую команду. Если OpenRGB не запущен или SDK Server выключен: `rgb.status` → `available: false` с `reason`, остальные `rgb.*` → `rgb.unavailable`.

**Только подтверждённый OpenRGB (T2).** Агент подключается только к своей копии OpenRGB (служба запустила её сама после проверки SHA-256 и держит файл заблокированным от записи и удаления) или к окну настройки OpenRGB, открытому администратором. Для каждого соединения, до отправки первого байта, проверяется процесс — владелец серверной стороны этого соединения по таблице TCP (на практике процесс, создавший слушающий сокет; для OpenRGB это тот же процесс, что принимает соединение). Если порт 6742 занят другой программой: `rgb.status` → `available: false`, `state: "conflict"`, `reason: "Подсветка недоступна: её перехватила другая программа на ПК."`; остальные `rgb.*` → `rgb.unavailable` с тем же текстом. Поле `state` присутствует только со значением `"conflict"`; старые клиенты видят текст в `reason`.

`effect`: `"static"` — ровный цвет на всех светодиодах (яркость умножается на цвет), `"off"` — чёрный, иначе — **имя режима устройства** из `modes[]` (без учёта регистра; цвет передаётся режиму, если он принимает цвета). Если у устройства такого режима нет — для него `ok: false` с объяснением, остальные применяются.

`rgb.apply` и `rgb.profiles.apply` → `{ applied, results: [{ index, name, ok, error? }] }`. `devices` — индексы из `rgb.status`, без него — все устройства.

**Связь с профилями программ.** Если есть профиль подсветки с тем же `id`, что у профиля программ (например, `gaming`), то после успешного `profiles.run` агент применяет и его и добавляет в ответ `lighting`: `"applied"`, `"noDevices"` (ни одно устройство не приняло) или `"unavailable"` (OpenRGB не запущен). Только для устройств с разрешением `rgb`; без него поле отсутствует и подсветка не меняется.

Профили подсветки хранятся в `C:\ProgramData\NexusPC\rgb.json`.

## Тачпад и клавиатура (P5)

Выполняет Nexus.Tray в сеансе пользователя (`SendInput`): без трея — `session.unavailable`. Разрешение `input` **никогда не выдаётся при сопряжении** — его включает администратор ПК (`Nexus.Agent.exe devices permissions <deviceId> …,input`).

Клавиши `input.key`: `a`–`z`, `0`–`9`, `f1`–`f12`, `enter`, `tab`, `escape`, `backspace`, `space`, `delete`, `insert`, стрелки `left/up/right/down`, `home`, `end`, `pageUp`, `pageDown`, `printScreen`, `win`. Сочетание нажимается и отпускается целиком в одном вызове — клавиши не могут «залипнуть». Ctrl+Alt+Del Windows программам не разрешает.

`input.button` с `down`/`up` — для перетаскивания. Если после `down` от телефона 5 секунд ничего не приходит (обрыв связи), трей сам отпускает кнопку.

Ответ — `{}` или ошибка `input.blocked`: экран ПК заблокирован, открыт запрос UAC или владелец запретил управление в меню трея. Ввод в окна программ, запущенных от имени администратора, Windows молча отбрасывает (UIPI) — агент не может это обнаружить.

Клиент отправляет ввод по одному запросу: движения, накопленные за время ожидания ответа, объединяются (NexusKit `InputQueue`). В журнал аудита успешный ввод пишется не чаще раза в минуту на устройство, ошибки — всегда; набранный текст в журнал не попадает.

## Файлы и буфер обмена (P7)

Размер сообщения: до **1 МиБ** после входа (кусок файла 512 КиБ в base64), до 64 КиБ до входа. Частота: до 100 запросов в секунду после входа (20 — до входа).

**Папка.** Телефон видит одну папку на ПК — `Загрузки\Nexus PC` (папка «Загрузки» берётся из настроек Windows). Только файлы прямо в ней: без подпапок, скрытых и системных файлов, ссылок. `name` — точное имя из `files.list`, никогда не путь. Выполняет Nexus.Tray от имени пользователя (без трея — `session.unavailable`).

**С телефона на ПК.** `begin` с SHA-256 и размером → куски по порядку (`offset` должен совпадать с уже полученным, иначе `files.offsetMismatch`) → `finish`: ПК проверяет SHA-256 (не совпало — файл удаляется, `files.hashMismatch`), даёт безопасное имя (только последняя часть пути, без `<>:"|?*`, зарезервированных имён `CON`, `NUL`…; до 120 символов) и не перезаписывает: `photo (2).jpg`. Пока файл не проверен, он лежит в `%LOCALAPPDATA%\NexusPC\incoming` (невидим для телефона). Докачка: недокачанный файл хранится 7 дней под именем своего SHA-256, повторный `begin` того же файла возвращает `offset`, с которого продолжать. Нужно свободное место: размер + 256 МБ. Полученный файл помечается как скачанный из интернета (Mark of the Web), чтобы Windows предупреждала перед запуском `.exe` и открывала документы в защищённом режиме.

**С ПК на телефон.** `files.read` кусками, затем `files.hash` — телефон сверяет SHA-256 и только потом показывает файл.

**Буфер обмена** — только текст и только по нажатию на телефоне; автоматической синхронизации нет. ПК показывает уведомление, когда телефон читает или меняет буфер.

В журнал аудита не попадают имена и содержимое файлов и текст буфера; успешные `files.upload.chunk` и `files.read` — не чаще раза в минуту на устройство.

## Автоматизации и история (P6)

Автоматизации выполняет агент на ПК — телефон для этого не нужен. Хранятся в `C:\ProgramData\NexusPC\automations.json` (до 32 правил, до 8 шагов).

**Когда (`trigger`)**
- `{ type: "time", time: "HH:mm", days?: [1..7] }` — по времени ПК (1 = понедельник); срабатывает один раз в день в течение 2 минут после указанного времени. Если ПК в это время спал или был выключен, правило пропускается (не догоняет).
- `{ type: "metric", metric: "gpuTemperature" | "cpuTemperature" | "cpuUsage" | "memoryUsage", above, forSeconds? }` — значение держится на уровне `above` или выше `forSeconds` секунд; повторно — только после снижения на 5 единиц. Недоступный датчик ничего не запускает.
- `{ type: "pcStarted" }` — через 15 с после запуска службы (до входа в Windows программы и профили не запускаются).
- `{ type: "userSignedIn" }` — когда подключается Nexus.Tray (вход в Windows), не чаще раза в 10 минут.

`onlyBetween: { from, to }` — дополнительное условие по времени, может переходить через полночь (22:00–07:00).

**Что сделать (`actions`, по порядку)**: `profile` (profileId; связанный профиль подсветки тоже включается), `app` (appId), `rgb` (color, brightness, effect), `rgbProfile` (profileId), `lock`, `sleep`, `shutdown` / `restart` (`delaySeconds` ≥ 60 — Windows показывает отсчёт, отмена `power.cancel`), `wait` (seconds ≤ 4 ч). Других действий нет: автоматизация не может запустить командную строку или путь.

**Безопасность.** Сохранить правило может только устройство, у которого есть `automation` **и** разрешения на все его действия (`apps`, `rgb`, `power`, `monitoring` для уведомлений) — иначе `auth.permissionDenied` с перечнем недостающих. Правило с выключением/перезагрузкой требует `confirmed`. Правило запоминает создавшее устройство; перед каждым шагом агент проверяет, что устройство не отозвано и разрешения не отняты, — иначе шаг не выполняется, а в истории остаётся объяснение. Одно правило не запускается повторно, пока выполняется (`automations.running`); `automations.stop` прерывает ожидание.

**Только владелец (T3).** Правилом управляет только подтверждённый владелец — сопряжённое устройство, которое его создало (id сравнивается как GUID, запись устройства есть, ключ действителен). Для любого другого устройства `list` правило не показывает, а `run`, `stop`, `delete` и `upsert` с его id отвечают `automations.notFound` — через `list`, `run`, `stop` и `delete` существование правила не раскрывается (`upsert` с занятым id тоже отвечает `notFound`, поэтому факт занятости id по нему узнать можно; id создаёт клиент, полная изоляция пространства id — вместе с T4); существующее правило не перезаписывается. `run` дополнительно требует у вызывающего разрешения на все действия правила (`auth.permissionDenied` с перечнем) и `confirmed` для выключения/перезагрузки (`auth.confirmationRequired`); владелец проверяется первым. Правило без подтверждённого владельца (устройство отозвано, старое правило без `createdBy`) не выполняется ни по какому триггеру — в истории «пропущена» — и не удаляется; в лимит 32 оно не входит. Владельца возвращает администратор на ПК: `Nexus.Agent.exe automations owners` и `automations assign <automationId> <deviceId> <fingerprint>` (см. `docs/SETUP.md`); если устройство в этот момент отзывается, назначение отклоняется с ошибкой `device is being revoked`, ничего не меняется, а попытка записывается в журнал аудита как неуспешная. Отзыв устройства и смена его разрешений синхронизированы с выполнением шагов: опасный шаг начинается только после свежей проверки под общей блокировкой, изменение ждёт завершения уже начатого шага. При отзыве устройства (`devices.revoke`, канал администратора и трея) его выполняющиеся правила отменяются ещё до ожидания блокировки, кроме шага, который уже идёт; новые запуски его правил — вручную и по триггерам — на время отзыва запрещены (триггер пишет в историю «Пропущена: устройство отзывается»). Если у устройства есть правила с выключением или перезагрузкой, успешный ответ `devices.revoke` содержит необязательное поле `warning`: уже запланированное ими выключение отзыв не отменяет.

**История** — `C:\ProgramData\NexusPC\history.log` (до 2 МБ + предыдущий файл): команды с телефона, которые что-то меняют (название устройства и действие, без содержимого: без имён файлов, текста буфера и набранного текста; тачпад — не чаще раза в 10 минут), запуски автоматизаций с итогом, предупреждения и события питания. Чтение (`metrics.*`, списки) в историю не пишется.

## Удалённый экран (P5)

Первый вариант без сторонних библиотек: трей снимает основной монитор средствами Windows (GDI, с указателем мыши), уменьшает до `maxWidth`, кодирует в JPEG (`quality`) и отдаёт кадр в ответ на `screen.frame`. Телефон запрашивает следующий кадр, только получив предыдущий, — при медленной сети падает частота кадров, а не растёт задержка. Если картинка не изменилась (`known` совпал с хэшем пикселей), возвращается `unchanged` без JPEG.

Разрешение `screen` не выдаётся при сопряжении. `screen.start`: агент добавляет `deviceId` и имя устройства из сеанса (телефон их передать не может), трей при первом показе для этого устройства спрашивает человека за ПК («Разрешить?»; ответ запоминается в профиле пользователя). Пока экран смотрят, трей показывает уведомление и имя смотрящего; «Запретить показ экрана» в меню трея сразу прекращает сеанс и не даёт начать новый. Сеанс заканчивается сам через 10 с без запросов кадров.

Управление — `input.moveTo` (абсолютная точка) + `input.button` / `input.scroll` / `input.text` / `input.key`, нужно разрешение `input`.

Ограничения: только основной монитор; некоторые полноэкранные игры и защищённое видео получаются чёрными (Windows скрывает их от GDI); экран блокировки и запрос UAC недоступны (`screen.blocked`); звук не передаётся. Это не видеопоток: частота кадров зависит от сети и изменений на экране — приложение показывает к/с, задержку и размер кадра. Если этого мало, следующий шаг — видеокодек H.264 (встроен в Windows и iPhone) или WebRTC (сторонние библиотеки — только с разрешения владельца).

### Видео H.264

По умолчанию экран идёт видеопотоком: трей снимает экран до 30 раз в секунду, переводит в NV12 и кодирует встроенным кодировщиком Windows (Media Foundation, базовый профиль, низкая задержка; кадры без изменений не кодируются). `screen.video { after }` возвращает все кадры после номера `after`, ожидая до 250 мс, если новых нет. Каждый ключевой кадр содержит SPS/PPS. Телефон запрашивает видео по **второму** соединению, чтобы касания и клавиши не ждали за кадрами, и декодирует аппаратно (AVSampleBufferDisplayLayer). Если декодер сбился или телефон отстал — `keyframe: true`. Нет кодировщика (`screen.videoUnavailable`) — переход на `screen.frame` (JPEG). «Быстро / Обычно / Чётко»: 960 / 1280 / 1920 точек по ширине, 1,5 / 3 / 6 Мбит/с.

