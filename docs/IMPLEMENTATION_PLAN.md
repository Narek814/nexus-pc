# Nexus PC — Детальный план ближайших этапов

## P0 — выполнено
1. Документы: ARCHITECTURE, SECURITY, FEATURES, ROADMAP (с решениями D11 «нулевой бюджет» и D12 «без Mac»).
2. `shared/schemas/request.v1.schema.json`: `system.hello`, `pc.status`, `power.*`, `apps.*`, `profiles.*`, `metrics.snapshot`. Сообщения сопряжения и авторизации (`pair.*`, `auth.*`) — на P1b вместе с их реализацией.
3. `shared/protocol/fixtures/*.json` — валидные и невалидные примеры (контракт-тесты для C# и Swift).
4. `docs/API.md` — описание каждого сообщения, кодов ошибок, классов риска.

## P1a — Nexus.Core — выполнено (97 тестов)
- `Envelope`, `CommandType`, `RiskClass`, `Permission`, `PcId`, `DeviceId`.
- `CommandCatalog` — единственный список допустимых команд и их требований.
- `CommandValidator` — схема + разрешения + риск + replay-окно.
- `AppRegistry` и `Profile` (модели и правила, без запуска процессов).
- Тесты: валидатор отклоняет неизвестные команды, команды без разрешения, `critical` без подтверждения, устаревшие `ts`, повторные `id`; все фикстуры `shared/` проходят.

## P1b — Nexus.Agent (нужен Windows)
- Хост службы, конфигурация в `%ProgramData%\NexusPC`.
- Генерация ключа и самоподписанного сертификата (DPAPI).
- Kestrel WSS на LAN-интерфейсах; mDNS-анонс `_nexuspc._tcp`.
- Сопряжение: QR в Tray, подтверждение на ПК, сохранение публичного ключа устройства.
- Power: shutdown/restart/sleep с задержкой и отменой; события `pc.state`.
- Аудит в SQLite.

## P1c — Nexus.Tray
- Автозапуск при входе, иконка в трее, окно сопряжения/подтверждений.
- Named Pipe клиент с проверкой подлинности сервера.
- Запуск приложений из реестра (ShellExecute по сохранённому пути/AUMID), закрытие (WM_CLOSE → таймаут → по разрешению Terminate).
- `LockWorkStation`.

## P2 — iOS
- Xcode-проект + Swift Package `NexusKit`.
- Дизайн-система, Command Center, Devices (сопряжение по QR через камеру), Profiles, Power.
- Reconnect с экспоненциальной задержкой, офлайн-кэш последнего состояния.

## Как проверяем
| Что | Где |
|-----|-----|
| `Nexus.Core`, схемы, фикстуры | Облачный контейнер (.NET 10 SDK установлен) + CI |
| Agent/Tray | Ваш Windows-ПК (я даю команды сборки и сценарий ручной проверки) |
| iOS | macOS-раннер GitHub Actions (сборка и `swift test`) → установка на iPhone через Sideloadly с Windows |
| CI (предложение) | GitHub Actions: `windows-latest` для .NET, `macos-latest` для `swift test` и `xcodebuild` без подписи |
