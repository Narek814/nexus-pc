# Nexus PC — Тестирование

## Что проверяется и где

| Набор | Команда | Где запускается |
|-------|---------|-----------------|
| `Nexus.Core`, `Nexus.Agent` (сеть, сопряжение, вход, команды), DevClient | `dotnet test windows/Nexus.slnx` | Любая ОС с .NET 10 SDK; CI — `ubuntu-latest` и `windows-latest` |
| Согласованность схем и примеров (запросы и ответы) | `python3 -I tools/check_fixtures.py` | Любая ОС с Python 3 и пакетом `jsonschema` 4.x; CI |
| Реальное выключение/сон Windows, служба, брандмауэр | ручной план ниже | Только ваш Windows-ПК |
| Nexus.Tray | — | P1c |
| iOS (`NexusKit`, приложение) | — | Пока нет (P2): `swift test` / `xcodebuild` на macOS-раннере GitHub Actions |

## Правила
- Каждая новая команда протокола: запись в схеме, в `CommandCatalog`, хотя бы один успешный и один ошибочный пример в `requests.v1.json`. Тест `Catalog_matches_schema_command_enum` упадёт, если схема и каталог разойдутся. Тест `Every_catalog_command_is_covered_by_a_successful_fixture` упадёт, если для команды нет успешного примера.
- Статус ✅ в FEATURES.md ставится только после проверки на реальном устройстве.

## Интеграционные тесты агента

`AgentIntegrationTests` поднимают настоящий агент (Kestrel, TLS, WebSocket) на `127.0.0.1` со
случайным портом и временной папкой данных и работают с ним через `Nexus.DevClient.NexusClient`
— тот же код, что у консольного клиента. Питание подменено записывающей заглушкой
(`FakePowerController`): тесты проверяют, *что* агент попросил Windows сделать, а не выключают машину.

Тесты защит проверены «мутациями»: защиту временно ломали и убеждались, что соответствующий тест
падает (проверка Origin, отзыв устройства, одноразовость nonce).

## Ручная проверка на Windows (P1b)

Выполняется по docs/SETUP.md. Отметьте результат каждого пункта.

| # | Действие | Ожидается |
|---|----------|-----------|
| W1 | `sc.exe query NexusPCAgent` | `STATE: RUNNING` |
| W2 | `icacls C:\ProgramData\NexusPC` | только `NT AUTHORITY\SYSTEM` и `BUILTIN\Administrators` |
| W3 | `Nexus.Agent.exe status` в **обычном** (не админ) PowerShell | отказ: «Cannot reach the agent…» |
| W4 | `Nexus.Agent.exe status` от администратора | JSON с `pcId`, `endpoints`, `fingerprint` |
| W5 | Сопряжение (SETUP §6), коды проверки совпадают | `Paired. Device id: …` |
| W6 | `send pc.status` | `ok: true`, `os` — Windows |
| W7 | `send power.shutdown --delay 600 --confirm` | уведомление Windows о выключении через 10 мин с именем устройства |
| W8 | `send power.cancel` | уведомление пропало, выключения нет |
| W9 | `send power.cancel` ещё раз | `power.nothingToCancel` |
| W10 | `send power.shutdown --delay 600` без `--confirm` | `confirm.required`, уведомления нет |
| W11 | `devices revoke <id>`, затем `send pc.status` | `auth.failed` |
| W12 | С другого устройства в той же Wi-Fi: `--host <IP ПК>` | соединение работает (сеть «частная») |
| W13 | `send power.sleep` (после повторного сопряжения) | ПК уходит в сон через ~1 с |
