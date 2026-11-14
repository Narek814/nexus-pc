# Nexus PC

Экосистема управления Windows-компьютерами с iPhone: питание, профили приложений, Siri, мониторинг, тачпад, удалённый экран, файлы, автоматизации, AI-помощник, RGB.

**Статус:** P0, P1a выполнены; P1b (служба Windows `Nexus.Agent`: сопряжение, вход, питание) написан и проверен тестами, ждёт проверки на реальном ПК. Приложения для iPhone пока нет — см. [FEATURES.md](FEATURES.md).

Бюджет проекта — ноль: только бесплатные инструменты и сервисы (ARCHITECTURE.md, D11–D12).

- [ARCHITECTURE.md](ARCHITECTURE.md) — компоненты и ключевые решения
- [SECURITY.md](SECURITY.md) — модель угроз и требования
- [FEATURES.md](FEATURES.md) — функции и их реальный статус
- [ROADMAP.md](ROADMAP.md) — этапы
- [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) — план ближайших этапов
- [docs/API.md](docs/API.md) — протокол v1
- [docs/TESTING.md](docs/TESTING.md) — как запускать тесты и план ручной проверки
- [docs/SETUP.md](docs/SETUP.md) — установка агента на Windows

## Структура

```
shared/schemas/          JSON Schema протокола (источник правды)
shared/protocol/fixtures контрольные примеры для C# и Swift
windows/Nexus.Core       модели, каталог команд, разрешения, проверка, сопряжение (кроссплатформенно)
windows/Nexus.Agent      служба Windows: WSS, вход, питание, канал администратора
windows/Nexus.DevClient  тестовый клиент вместо iPhone
windows/Nexus.Tests      xUnit: юнит, контракт и интеграционные тесты
.github/workflows        CI: тесты на Windows и Linux, проверка схем
tools/                   проверка схем
```

## Тесты

```bash
dotnet test windows/Nexus.slnx
```

```bash
python3 -I tools/check_fixtures.py
```
