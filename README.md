# Nexus PC

Экосистема управления Windows-компьютерами с iPhone: питание, профили приложений, Siri, мониторинг, тачпад, удалённый экран, файлы и буфер обмена, автоматизации, подсветка.

**Статус (2026-11-17):** работает и проверено на реальном ПК и iPhone владельца — питание и включение ПК по Wi-Fi, программы и профили, Siri, виджеты и Live Activity, мониторинг, подсветка (OpenRGB), тачпад и клавиатура, файлы и буфер обмена, автоматизации и история, установщик для Windows, окно разрешений в трее. Экран ПК (кадры JPEG) показан на iPhone; видео H.264, температура процессора, новая иконка и новый вид окон ПК — готовы и проверены тестами (кодировщик H.264 — на настоящей Windows в CI), ждут проверки на устройствах. Не делаются по решению владельца: несколько ПК (P8), AI-помощник (P9). Подробно — [FEATURES.md](FEATURES.md).

**Начать пользоваться:** [docs/USER_GUIDE.md](docs/USER_GUIDE.md).

Бюджет проекта — ноль: только бесплатные инструменты и сервисы (ARCHITECTURE.md, D11–D12).

- [docs/USER_GUIDE.md](docs/USER_GUIDE.md) — инструкция пользователя (установка, разрешения, функции, если что-то не работает)
- [ARCHITECTURE.md](ARCHITECTURE.md) — компоненты и ключевые решения
- [SECURITY.md](SECURITY.md) — модель угроз и требования
- [FEATURES.md](FEATURES.md) — функции и их реальный статус
- [ROADMAP.md](ROADMAP.md) — этапы
- [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) — план ближайших этапов
- [docs/API.md](docs/API.md) — протокол v1
- [docs/TESTING.md](docs/TESTING.md) — как запускать тесты и план ручной проверки
- [docs/SETUP.md](docs/SETUP.md) — установка на Windows: установщик и ручная сборка
- [docs/IOS.md](docs/IOS.md) — сборка iPhone-приложения без Mac и установка
- [docs/REMOTE.md](docs/REMOTE.md) — доступ вне дома (Tailscale) и включение ПК (Wake-on-LAN)

## Структура

```
shared/schemas/          JSON Schema протокола (источник правды)
shared/protocol/fixtures контрольные примеры для C# и Swift
windows/Nexus.Core       протокол, команды, разрешения, сопряжение, ввод, файлы, экран, автоматизации (кроссплатформенно)
windows/Nexus.Agent      служба Windows: WSS, вход, питание, мониторинг, подсветка, автоматизации, история
windows/Nexus.Tray       приложение в трее (WinForms): QR, программы, профили, ввод, экран, файлы, буфер, разрешения
windows/Nexus.Windows    вызовы Windows: питание, сеансы, кодировщик H.264 (Media Foundation)
windows/Nexus.Monitoring датчики: CPU, память, NVIDIA (NVML), CPU-температура (LibreHardwareMonitor), диски, сеть
windows/Nexus.RGB        клиент OpenRGB SDK
windows/Nexus.Setup      установщик NexusPC-Setup.exe
windows/Nexus.DevClient  тестовый клиент вместо iPhone
windows/Nexus.Tests      xUnit: юнит, контракт и интеграционные тесты
ios/NexusKit             Swift-пакет: протокол, сопряжение, криптография, очередь ввода, файлы, H.264
ios/NexusPC              приложение SwiftUI; ios/project.yml — спецификация XcodeGen
ios/NexusWidgets         виджеты и Live Activity
.github/workflows        CI: тесты Windows/Linux, установщик на Windows, сборка iPhone-приложения на macOS
tools/                   проверка схем и QR, рисование иконки
```

## Тесты

```bash
dotnet test windows/Nexus.slnx
```

```bash
python3 -I tools/check_fixtures.py
```

```bash
python3 -I tools/check_qr.py
```
