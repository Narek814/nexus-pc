# Nexus PC — Тестирование

## Что проверяется и где

| Набор | Команда | Где запускается |
|-------|---------|-----------------|
| `Nexus.Core`, `Nexus.Agent` (сеть, сопряжение, вход, команды), DevClient | `dotnet test windows/Nexus.slnx` | Любая ОС с .NET 10 SDK; CI — `ubuntu-latest` и `windows-latest` |
| Согласованность схем и примеров (запросы и ответы) | `python3 -I tools/check_fixtures.py` | Любая ОС с Python 3 и пакетом `jsonschema` 4.x; CI |
| Реальное выключение/сон Windows, служба, брандмауэр | ручной план ниже | Только ваш Windows-ПК |
| Nexus.Tray: логика команд, хранилище, канал | `dotnet test` (`TrayIntegrationTests`, `SessionTests`) | Любая ОС; окна и вызовы Windows — ручной план T1–T12 |
| QR-кодировщик | `python3 -I tools/check_qr.py` (декодирует все 40 версий через zxing-cpp) | Любая ОС с Python, `zxing-cpp`, `pillow`; CI |
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

Выполняется по docs/SETUP.md.

**Результаты на ПК владельца (Windows 10.0.26300, 2026-11-14):** ✅ W1, W5, W6, W7 (ответ агента и уведомление Windows), W8, W9, W10. Найдена и исправлена ошибка: выключение из службы падало с Win32 1191 (нужен `SHUTDOWN_FORCE_OTHERS`). Найдена и исправлена ошибка инструкции: `sc.exe create` с кавычками ломается в PowerShell → `New-Service`. Не проверено: W2–W4, W11–W14.

| # | Действие | Ожидается |
|---|----------|-----------|
| W1 | `sc.exe query NexusPCAgent` | `STATE: RUNNING` |
| W2 | `icacls C:\ProgramData\NexusPC` | только `NT AUTHORITY\SYSTEM` и `BUILTIN\Administrators` |
| W3 | `Nexus.Agent.exe status` в **обычном** (не админ) PowerShell | отказ: «Cannot reach the agent…» |
| W4 | `Nexus.Agent.exe status` от администратора | JSON с `pcId`, `endpoints`, `fingerprint` |
| W5 | Сопряжение (SETUP §6), коды проверки совпадают | `Paired. Device id: …` |
| W6 | `send pc.status` | `ok: true`, `os` — Windows |
| W7 | `send power.shutdown --delay 600 --confirm` | уведомление Windows о выключении через 10 мин с именем устройства (первая попытка дала ошибку 1191 — исправлено) |
| W8 | `send power.cancel` | уведомление пропало, выключения нет |
| W9 | `send power.cancel` ещё раз | `power.nothingToCancel` |
| W10 | `send power.shutdown --delay 600` без `--confirm` | `confirm.required`, уведомления нет |
| W11 | `devices revoke <id>`, затем `send pc.status` | `auth.failed` |
| W12 | С другого устройства в той же Wi-Fi: `--host <IP ПК>` | соединение работает (сеть «частная») |
| W13 | `send power.sleep` (после повторного сопряжения) | ПК уходит в сон через ~1 с |
| W14 | `send power.restart --confirm` (без задержки) | ответ `delaySeconds: 30`, уведомление Windows; отменить `send power.cancel` |

## Ручная проверка Nexus.Tray (P1c)

| # | Действие | Ожидается |
|---|----------|-----------|
| T1 | Запуск трея через `explorer.exe` (SETUP §7) | цветной значок «N», в меню «Служба подключена» |
| T2 | Запуск трея из окна администратора напрямую | предупреждение о правах администратора, трей не запускается |
| T3 | «Приложения и профили» → «Из меню Пуск…» → добавить 2 программы | появились в списке |
| T4 | `send apps.list` | названия и id, **без путей** к файлам |
| T5 | `send apps.launch --app <id>` | программа открылась, у значка всплыло «Started …» |
| T6 | Профиль: «Создать стандартные» → Gaming, отметить программы → «Сохранить»; `send profiles.run --profile gaming` | все программы профиля открылись, в ответе `started` |
| T7 | `send apps.launch --app powershell` (если не в списке) | `apps.notFound` |
| T8 | `send power.lock` | экран заблокирован |
| T9 | `send power.shutdown --delay 600 --confirm` при запущенном трее | ответ `"via": "userSession"`, уведомление Windows; затем `send power.cancel` |
| T10 | «Подключить iPhone…» | QR-код, код, отсчёт; «Копировать ссылку» копирует |
| T11 | Сопряжение DevClient по ссылке из окна трея | в окне трея имя устройства и код проверки, «Подключить» → «Устройство подключено» |
| T12 | Выйти из трея, `send apps.list` | `session.unavailable`; `pc.status` → `userSession: unavailable` |
