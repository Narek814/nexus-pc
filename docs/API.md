# Nexus PC — Протокол v1

Источник правды: [`shared/schemas/request.v1.schema.json`](../shared/schemas/request.v1.schema.json).
Контрольные примеры: [`shared/protocol/fixtures/requests.v1.json`](../shared/protocol/fixtures/requests.v1.json), их прогоняют реализации на C# и на Swift.
Реализация на C#: `windows/Nexus.Core/Protocol`.

Статус (P1b): транспорт WSS, сопряжение, вход по подписи и команды питания **реализованы в `Nexus.Agent` и проверены тестами на Linux**. На реальном Windows-ПК ещё **не проверены** (см. docs/TESTING.md). Команды приложений и профилей отвечают `command.notImplemented` до P1c.

## Транспорт

- `wss://<адрес>:47800/v1/ws` — TLS 1.2/1.3, самоподписанный сертификат ECDSA P-256.
- Клиент **не проверяет цепочку сертификатов**, а сравнивает SHA-256 сертификата с отпечатком `fp` из ссылки сопряжения (pinning). Другой сертификат — отказ соединения.
- Агент слушает только `127.0.0.1`, частные адреса LAN (10/8, 172.16/12, 192.168/16) и диапазон Tailscale (100.64/10).
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
| `metrics.snapshot` | monitoring | read | `{}` |
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

`slug` — `^[a-z0-9][a-z0-9-]{0,63}$`. Это **никогда не путь**: путь к программе задаётся только на ПК.

`capabilities` в `system.hello` перечисляет команды, у которых **есть рабочий обработчик** в этой сборке агента. Клиент скрывает остальные.

**Питание.** С `delaySeconds > 0` Windows показывает своё уведомление с именем устройства, выключение отменяется через `power.cancel`. С задержкой 0 и для `power.sleep` агент сначала отвечает, потом через 1 секунду действует; доступность действия проверяется до ответа. Режим Fast Startup (hybrid shutdown) не используется: с ним Wake-on-LAN работает хуже.
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
| `internal` | ошибка агента |

## Ответы

Схема: [`shared/schemas/response.v1.schema.json`](../shared/schemas/response.v1.schema.json).

```json
{ "v": 1, "id": "<id запроса>", "kind": "response", "ok": true,  "payload": { } }
{ "v": 1, "id": "<id запроса>", "kind": "response", "ok": false, "error": { "code": "auth.permissionDenied", "message": "…" } }
```
`id` равен `null`, если запрос не удалось разобрать. События (`kind: "event"`) появятся вместе с первым их источником.
