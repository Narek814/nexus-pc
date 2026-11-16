# Nexus PC — Протокол v1

Источник правды: [`shared/schemas/request.v1.schema.json`](../shared/schemas/request.v1.schema.json).
Контрольные примеры: [`shared/protocol/fixtures/requests.v1.json`](../shared/protocol/fixtures/requests.v1.json), их прогоняют реализации на C# и на Swift.
Реализация на C#: `windows/Nexus.Core/Protocol`.

Статус: транспорт, сопряжение, вход и питание проверены на реальном ПК (P1b). Приложения, профили и блокировка (P1c) работают через Nexus.Tray и проверены тестами; на реальном ПК — ещё нет (docs/TESTING.md T1–T12). Мониторинг (`metrics.*`) — P4a.

## Транспорт

- `wss://<адрес>:47800/v1/ws` — TLS 1.2/1.3, самоподписанный сертификат ECDSA P-256.
- Клиент подключается **напрямую**, без системного прокси.
- Клиент **не проверяет цепочку сертификатов**, а сравнивает SHA-256 сертификата с отпечатком `fp` из ссылки сопряжения (pinning). Другой сертификат — отказ соединения.
- Агент принимает подключения только на `127.0.0.1`, частные адреса LAN (10/8, 172.16/12, 192.168/16) и диапазон Tailscale (100.64/10): слушает все IPv4-адреса, а остальные подключения обрывает до TLS. Адреса в ссылке сопряжения — текущие адреса интерфейсов с выходом в сеть (и Tailscale) на момент открытия сопряжения.
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
| `system.hello` | — (без авторизации) | read | `{}` → `{ protocol, pcId, pcName, agentVersion, authenticated, capabilities[] }` |
| `pair.request` | — (без авторизации) | normal | `{ code: 8 цифр, deviceName, publicKey }` |
| `auth.begin` | — (без авторизации) | read | `{ deviceId }` |
| `auth.prove` | — (без авторизации) | read | `{ signature }` |
| `pc.status` | monitoring | read | `{}` → `{ pcId, pcName, os, uptimeSeconds, agentVersion, userSession }` |
| `metrics.snapshot` | monitoring | read | `{}` → снимок, см. «Мониторинг» |
| `alerts.get` | monitoring | read | `{}` → пороги, `ntfy { enabled, server, topic }`, `recent` |
| `alerts.set` | monitoring | normal | любые из: `gpuTemperatureEnabled`, `gpuTemperatureC` (50–110), `cpuUsageEnabled`, `cpuUsagePercent` (50–100), `memoryUsageEnabled`, `memoryUsagePercent` (50–100), `diskFreeEnabled`, `diskFreePercent` (1–50), `powerEventsEnabled`, `cooldownMinutes` (5–1440), `ntfyEnabled`, `ntfyServer` (https), `regenerateTopic` |
| `alerts.test` | monitoring | normal | `{}` → `{ delivered, ntfyEnabled }` |
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
| `rgb.status` | rgb | read | `{}` → `{ adapter, available, reason?, devices[{ index, name, type, leds, modes[] }], profiles[] }` |
| `rgb.apply` | rgb | normal | `{ color: "#RRGGBB", brightness: 0..100, effect: 1..64 символа, devices?: int[] }` |
| `rgb.profiles.upsert` | rgb | normal | `{ profile: { id: slug, name, color, brightness, effect } }` (≤ 32 профилей) |
| `rgb.profiles.delete` | rgb | normal | `{ profileId: slug }` |
| `rgb.profiles.apply` | rgb | normal | `{ profileId: slug }` |

`slug` — `^[a-z0-9][a-z0-9-]{0,63}$`. Это **никогда не путь**: путь к программе задаётся только на ПК.

`capabilities` в `system.hello` перечисляет команды, которые **работают прямо сейчас**: `power.lock`, `apps.*`, `profiles.*` — только пока Nexus.Tray запущен в сеансе человека за ПК (`pc.status.userSession = "available"`). Без трея они отвечают `session.unavailable`. Клиент скрывает остальные.

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
| `internal` | ошибка агента |

## Ответы

Схема: [`shared/schemas/response.v1.schema.json`](../shared/schemas/response.v1.schema.json).

```json
{ "v": 1, "id": "<id запроса>", "kind": "response", "ok": true,  "payload": { } }
{ "v": 1, "id": "<id запроса>", "kind": "response", "ok": false, "error": { "code": "auth.permissionDenied", "message": "…" } }
```
`id` равен `null`, если запрос не удалось разобрать. События (`kind: "event"`) появятся вместе с первым их источником.

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

Источники (без дополнительных пакетов): `GetSystemTimes` (загрузка CPU), `GlobalMemoryStatusEx` (память), NVML из драйвера NVIDIA (`nvml.dll`: загрузка, температура, видеопамять, частоты GPU), `DriveInfo` (диски), счётчики сетевых интерфейсов (скорость сети). Температура и реальная частота процессора требуют драйвера датчиков — отдаются как недоступные с причиной. Видеокарты AMD/Intel — позже.

`metrics.history { range }` → `{ range, intervalSeconds, points: [{ t, cpu, memory, gpu, gpuTemperature, rx, tx }] }`. `10m` — точки каждые 2 с; `1h`, `24h` — средние за минуту. История хранится в памяти службы и начинается заново после её перезапуска (постоянная история — P6).

## Предупреждения и уведомления (P4b)

Правила (на ПК, `Nexus.Notifications.AlertEngine`): видеокарта ≥ порога **30 с**; CPU и память ≥ порога **5 мин**; свободное место на диске < порога — сразу. Повторно то же предупреждение — только после явного спада (на 5 единиц ниже порога) **и** не раньше, чем через `cooldownMinutes`. Недоступные значения ничего не включают и не сбрасывают.

События питания: «ПК включился» (старт службы), «ПК выключается» (остановка службы, лучшая попытка), «ПК был выключен неожиданно» — если при старте остался файл-метка прошлого запуска (обновляется раз в минуту, удаляется при чистой остановке).

Доставка: **ntfy** (`POST https://ntfy.sh/<канал>`, заголовки Title, Priority, Tags). Выключено по умолчанию; при включении создаётся случайный канал `nexuspc-<32 hex>`. Текст — только имя ПК, событие и числа.

## Подсветка (P10)

Агент управляет подсветкой через **OpenRGB** (отдельная бесплатная программа на ПК) по его SDK-протоколу на `127.0.0.1:6742` — только локально, наружу ничего не открывается. Короткое соединение на каждую команду. Если OpenRGB не запущен или SDK Server выключен: `rgb.status` → `available: false` с `reason`, остальные `rgb.*` → `rgb.unavailable`.

`effect`: `"static"` — ровный цвет на всех светодиодах (яркость умножается на цвет), `"off"` — чёрный, иначе — **имя режима устройства** из `modes[]` (без учёта регистра; цвет передаётся режиму, если он принимает цвета). Если у устройства такого режима нет — для него `ok: false` с объяснением, остальные применяются.

`rgb.apply` и `rgb.profiles.apply` → `{ applied, results: [{ index, name, ok, error? }] }`. `devices` — индексы из `rgb.status`, без него — все устройства.

**Связь с профилями программ.** Если есть профиль подсветки с тем же `id`, что у профиля программ (например, `gaming`), то после успешного `profiles.run` агент применяет и его и добавляет в ответ `lighting`: `"applied"`, `"noDevices"` (ни одно устройство не приняло) или `"unavailable"` (OpenRGB не запущен). Только для устройств с разрешением `rgb`; без него поле отсутствует и подсветка не меняется.

Профили подсветки хранятся в `C:\ProgramData\NexusPC\rgb.json`.

