# Nexus PC — Протокол v1

Источник правды: [`shared/schemas/request.v1.schema.json`](../shared/schemas/request.v1.schema.json).
Контрольные примеры: [`shared/protocol/fixtures/requests.v1.json`](../shared/protocol/fixtures/requests.v1.json), их прогоняют реализации на C# и на Swift.
Реализация на C#: `windows/Nexus.Core/Protocol`.

Статус: **разбор и проверка запросов реализованы и покрыты тестами** (`Nexus.Core`). Транспорт (WSS), сопряжение и обработчики команд пока **не реализованы** (P1b/P1c). Формат ответов и событий описан ниже как план и будет закреплён схемой на P1b.

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
| `system.hello` | — (без авторизации) | read | `{}` |
| `pc.status` | monitoring | read | `{}` |
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

## Ответы и события (план, P1b)

```json
{ "v": 1, "id": "<id запроса>", "kind": "response", "ok": true,  "payload": { } }
{ "v": 1, "id": "<id запроса>", "kind": "response", "ok": false, "error": { "code": "auth.permissionDenied", "message": "…" } }
{ "v": 1, "id": "<новый uuid>", "kind": "event", "type": "pc.state", "ts": "…", "payload": { "state": "online" } }
```
