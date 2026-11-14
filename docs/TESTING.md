# Nexus PC — Тестирование

## Что проверяется и где

| Набор | Команда | Где запускается |
|-------|---------|-----------------|
| `Nexus.Core` + общие примеры сообщений | `dotnet test windows/Nexus.slnx` | Любая ОС с .NET 10 SDK (облачная сессия, Windows-ПК, CI) |
| Согласованность схемы и примеров | `python3 -I tools/check_fixtures.py` | Любая ОС с Python 3 и пакетом `jsonschema` 4.x |
| Windows-компоненты (Agent, Tray) | — | Пока нет (P1b/P1c), ручные сценарии на ПК |
| iOS (`NexusKit`, приложение) | — | Пока нет (P2): `swift test` / `xcodebuild` на macOS-раннере GitHub Actions |

## Правила
- Каждая новая команда протокола: запись в схеме, в `CommandCatalog`, хотя бы один успешный и один ошибочный пример в `requests.v1.json`. Тест `Catalog_matches_schema_command_enum` упадёт, если схема и каталог разойдутся. Тест `Every_catalog_command_is_covered_by_a_successful_fixture` упадёт, если для команды нет успешного примера.
- Статус ✅ в FEATURES.md ставится только после проверки на реальном устройстве.
