# Nexus PC

Экосистема управления Windows-компьютерами с iPhone: питание, профили приложений, Siri, мониторинг, тачпад, удалённый экран, файлы, автоматизации, AI-помощник, RGB.

**Статус:** P0 (архитектура, безопасность, протокол v1) и P1a (`Nexus.Core`) выполнены. Приложений для Windows и iPhone пока нет — см. [FEATURES.md](FEATURES.md).

Бюджет проекта — ноль: только бесплатные инструменты и сервисы (ARCHITECTURE.md, D11–D12).

- [ARCHITECTURE.md](ARCHITECTURE.md) — компоненты и ключевые решения
- [SECURITY.md](SECURITY.md) — модель угроз и требования
- [FEATURES.md](FEATURES.md) — функции и их реальный статус
- [ROADMAP.md](ROADMAP.md) — этапы
- [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) — план ближайших этапов
- [docs/API.md](docs/API.md) — протокол v1
- [docs/TESTING.md](docs/TESTING.md) — как запускать тесты

## Структура

```
shared/schemas/          JSON Schema протокола (источник правды)
shared/protocol/fixtures контрольные примеры для C# и Swift
windows/Nexus.Core       модели, каталог команд, разрешения, проверка (кроссплатформенно)
windows/Nexus.Tests      xUnit
tools/                   проверка схем
```

## Тесты

```bash
dotnet test windows/Nexus.slnx
```

```bash
python3 -I tools/check_fixtures.py
```
