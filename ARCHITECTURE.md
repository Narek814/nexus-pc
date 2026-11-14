# Nexus PC — Архитектура

Статус документа: **v0.1, этап P0**. Код пока не написан; здесь зафиксированы решения, на которые будут опираться все следующие этапы. Изменение любого решения из раздела «Ключевые решения» — через обновление этого файла.

---

## 1. Обзор системы

```
┌──────────────────────── iPhone ────────────────────────┐
│  NexusPC.app (SwiftUI)                                 │
│   ├─ Views / ViewModels (Observation)                  │
│   ├─ NexusKit (общий Swift-пакет: протокол, сеть,      │
│   │            модели, Keychain, кэш App Group)        │
│   ├─ App Intents (Siri / Shortcuts)                    │
│   ├─ Widget Extension (WidgetKit, интерактивные)       │
│   └─ Live Activities (ActivityKit)                     │
└───────────────┬────────────────────────────────────────┘
                │  WSS (TLS 1.3, pinned cert) + HTTPS (файлы)
                │  WebRTC (экран)  — LAN / VPN / Relay
┌───────────────▼──────────────── Windows PC ────────────┐
│  Nexus.Agent  (Windows Service, Session 0)             │
│   ├─ API: WebSocket + HTTPS (Kestrel)                  │
│   ├─ Auth/Pairing, Permissions, Command Validator      │
│   ├─ Nexus.Monitoring, Nexus.Automation,               │
│   │  Nexus.Notifications, Nexus.Sync (файлы), History  │
│   └─ IPC-сервер: Named Pipe (ACL: только SID польз.)   │
│                │                                       │
│  Nexus.Tray  (приложение в сеансе пользователя)        │
│   ├─ Запуск/закрытие приложений, LockWorkStation       │
│   ├─ Буфер обмена, ввод (SendInput), громкость         │
│   ├─ Nexus.Remote (захват экрана + WebRTC)             │
│   └─ UI: трей, подтверждения, QR для сопряжения        │
│                                                        │
│  Nexus.RGB → адаптеры (OpenRGB SDK, Dynamic Lighting)  │
└────────────────────────────────────────────────────────┘
                │ (только исходящие соединения)
┌───────────────▼──── Облако (позже, P3/P11) ────────────┐
│  Nexus.Gateway — регистрация устройств, APNs-пуши,     │
│                  heartbeat-наблюдатель                 │
│  Nexus.Relay   — пересылка E2E-зашифрованных кадров    │
│  Nexus.AI      — прокси к AI-провайдеру (ключи на сервере)│
└────────────────────────────────────────────────────────┘
```

## 2. Ключевые решения

| # | Решение | Обоснование |
|---|---------|-------------|
| D1 | **Служба + приложение в сеансе пользователя.** Agent работает в Session 0 и *никогда* не запускает GUI-процессы сам. Всё, что касается рабочего стола, выполняет Tray по запросу через Named Pipe. | Session 0 isolation: процессы службы не видят рабочий стол пользователя. `CreateProcessAsUser` из службы — источник уязвимостей и ошибок с токенами. |
| D2 | **Белый список команд.** Протокол содержит только типизированные команды (`power.shutdown`, `apps.launch{appId}`…). Нет команды «выполнить строку». Приложения запускаются только по `appId` из реестра, который пользователь подтвердил на ПК. | Требование безопасности; та же модель используется AI и автоматизациями. |
| D3 | **Один протокол для всех клиентов.** JSON-сообщения в конверте с версией поверх WebSocket; JSON Schema в `shared/schemas` — единственный источник правды; обе стороны прогоняют одинаковые фикстуры (`shared/protocol/fixtures`). | Swift и C# не делят код; контракт-тесты ловят расхождения. |
| D4 | **Сопряжение по QR + взаимная аутентификация ключами.** TLS-сертификат агента закрепляется (pinning) при сопряжении; iPhone хранит ключ P-256 в Secure Enclave и подписывает challenge. Паролей нет. | Самоподписанный сертификат в LAN без CA; Secure Enclave поддерживает только P-256. |
| D5 | **Удалённый доступ без открытых портов.** Этап 1: LAN + пользовательский VPN (Tailscale/WireGuard) — агент слушает только LAN/VPN-интерфейсы. Этап 2: Nexus.Relay — агент держит *исходящее* соединение, кадры зашифрованы end-to-end (relay их не читает). UPnP/проброс портов запрещены. | Требование SECURITY; VPN даёт удалённый доступ без своей инфраструктуры. |
| D6 | **Multi-PC в модели данных с первого дня.** Каждая сущность (профиль, история, настройки, разрешения) привязана к `pcId`. UI переключения — P8. | Дешево сейчас, дорого переделывать потом. |
| D7 | **Автоматизации исполняются на ПК** (Nexus.Automation), а не на iPhone. iPhone — редактор и получатель уведомлений. | iOS не даёт приложению работать постоянно в фоне; триггеры «ПК включился», «запущена игра», «высокая температура» возникают на ПК. |
| D8 | **AI только планирует, исполняет Agent.** AI возвращает план из шагов каталога команд (D2); план валидируется детерминированным кодом и показывается пользователю. Провайдер подключается через интерфейс `IAIProvider`/`AIProvider`. | Защита от prompt injection и «галлюцинированных» команд. |
| D9 | **Недоступные датчики = `null` + причина.** Никаких подставных значений. | Требование ТЗ. |
| D10 | **Минимум зависимостей.** Добавление каждой внешней библиотеки — отдельное решение с проверкой лицензии и совместимости, фиксируется в разделе 9. | Требование ТЗ. |

## 3. Платформы и версии (предлагаемые)

| Компонент | Выбор | Примечание |
|-----------|-------|------------|
| iOS | Swift 6, SwiftUI, **iOS 17+** | iOS 17 — интерактивные виджеты, Observation, App Intents в виджетах. |
| Windows | **.NET 10 (LTS)**, C# | Windows 10 22H2+ / Windows 11. `Microsoft.Extensions.Hosting.WindowsServices`. |
| Tray UI | WinForms/WPF трей (решение в P1) | WinUI 3 тяжелее в упаковке; для трея и окна подтверждений достаточно WPF. |
| Хранилище ПК | SQLite (`Microsoft.Data.Sqlite`) | История, реестр устройств, автоматизации. |
| Хранилище iOS | SwiftData или SQLite; Keychain для секретов; App Group для кэша виджетов | |

## 4. Компоненты Windows

| Проект | Тип | Ответственность |
|--------|-----|-----------------|
| `Nexus.Core` | библиотека (кроссплатформенная) | Модели протокола, каталог команд, разрешения, валидатор команд, политика подтверждений. Без Windows-API → тестируется на любой ОС. |
| `Nexus.Agent` | Windows Service | Kestrel (WSS/HTTPS), сопряжение, сессии, маршрутизация команд, power control (`InitiateShutdownW`, `SetSuspendState`), IPC-сервер, Bonjour/mDNS-анонс. |
| `Nexus.Tray` | WinExe, автозапуск в сеансе пользователя | Запуск/закрытие приложений из реестра, `LockWorkStation`, буфер обмена, `SendInput`, Core Audio, захват экрана, окна подтверждения. |
| `Nexus.Monitoring` | библиотека | Провайдеры телеметрии за интерфейсом `ISensorProvider`: Performance Counters / PDH (CPU, RAM, диски, сеть, GPU Engine), NVML (NVIDIA), LibreHardwareMonitorLib (температуры, частоты — опционально, см. §8). |
| `Nexus.Remote` | библиотека (в Tray) | Захват (Windows.Graphics.Capture / DXGI Desktop Duplication), кодирование, WebRTC, ввод. |
| `Nexus.RGB` | библиотека | `IRgbAdapter`; первые адаптеры — OpenRGB SDK и Windows Dynamic Lighting. |
| `Nexus.Automation` | библиотека (в Agent) | Движок «Триггер → Условие → Действие», планировщик. |
| `Nexus.Sync` | библиотека | Передача файлов (чанки, докачка, SHA-256), песочница папок; буфер обмена (через Tray). |
| `Nexus.Notifications` | библиотека | Шина событий, дедупликация/троттлинг Smart Alerts, доставка (WS → iPhone; позже через Gateway → APNs). |
| `Nexus.Tests` | xUnit | Юнит + контракт-тесты по фикстурам `shared/`. |

### 4.1 IPC Agent ↔ Tray
- Named Pipe `\\.\pipe\NexusPC.<SID>`; ACL: `SYSTEM` + конкретный SID интерактивного пользователя. Агент проверяет клиента через `GetNamedPipeClientProcessId` → путь и подпись исполняемого файла Tray.
- Сообщения — те же JSON-конверты, что во внешнем протоколе (подмножество `ipc.*`).
- Если Tray не запущен (никто не вошёл в систему), команды, требующие сеанса, возвращают `error.sessionUnavailable` — честно, без попытки запустить GUI из службы.

## 5. Протокол (кратко; подробно — `shared/protocol/`, `docs/API.md`)

```json
{ "v": 1, "id": "uuid", "type": "apps.launch", "ts": "2026-11-14T10:00:00Z",
  "pcId": "uuid", "payload": { "appId": "steam" } }
```
- Типы: `request` / `response` / `event`; ответы связаны по `id`.
- Классы риска команд: `read` (метрики), `normal` (запуск приложений), `critical` (выключение, перезагрузка, удалённый экран, буфер обмена) — для `critical` нужен явный флаг подтверждения, а для части — подтверждение на самом ПК.
- Транспорт: WSS для команд/событий, HTTPS для файлов (докачка через Range), WebRTC для экрана (сигнализация через WSS).
- Версионирование: поле `v`; агент объявляет поддерживаемые возможности в `hello` (`capabilities`), клиент скрывает недоступные функции.

## 6. iOS-приложение

```
ios/NexusPC/
  App/            — точка входа, навигация, DI
  Views/          — экраны (10 разделов) + DesignSystem/
  ViewModels/     — @Observable модели экранов
  Services/       — PCService, ProfilesService, MonitoringService…
  Models/         — доменные модели
  Networking/     — NexusClient (URLSessionWebSocketTask), TLS pinning, reconnect
  Siri/           — AppIntents, AppShortcutsProvider
  Widgets/        — WidgetKit extension
  LiveActivities/ — ActivityAttributes + UI для Dynamic Island
```
- Сетевой слой и модели — в Swift Package `NexusKit`, чтобы его использовали app, widget и intents.
- Виджеты/Siri не держат постоянное соединение: открывают короткое соединение, выполняют одну команду, пишут результат в кэш App Group.

## 7. Дизайн-система (iOS)
Токены: цвета (`bg.base #07070B`, `bg.elevated` графит, `accent.violet`, `accent.cyan`, градиент RGB), типографика (SF Pro / SF Mono для цифр), радиусы, glow-тени. Компоненты: `GlassCard`, `NeonButton`, `PowerButton`, `MetricGauge`, `SparklineChart` (Swift Charts), `StatusPill`, `ProfileTile`, `SectionHeader`. Уважение `Reduce Motion`, `Reduce Transparency`, Dynamic Type, VoiceOver.

## 8. Технические ограничения (обнаружены на P0)

1. **Wake-on-LAN с iPhone.** Широковещательные UDP-пакеты в iOS требуют entitlement `com.apple.developer.networking.multicast`, которое выдаёт Apple по заявке. Из интернета WoL невозможен без устройства в той же сети. Решение: (а) заявка на entitlement для LAN; (б) «Wake proxy» — любое всегда включённое устройство в LAN (второй ПК с Agent, Raspberry Pi, роутер с WoL) получает команду через Relay/VPN и шлёт magic packet. Также нужна поддержка WoL в BIOS/сетевой карте; по Wi-Fi WoL обычно не работает.
2. **Фоновая работа iOS.** Приложение не может держать соединение в фоне. Уведомления «ПК выключился», Smart Alerts при закрытом приложении и удалённые обновления Live Activities требуют **APNs через сервер** (Nexus.Gateway). До его появления — только локальные уведомления при активном приложении.
3. **Виджеты.** Обновление по бюджету системы (не реальное время); кнопки работают через App Intents с ограниченным временем выполнения.
4. **Live Activities** — до 8 часов активного состояния, частота push-обновлений ограничена системой.
5. **LibreHardwareMonitor** требует прав администратора и драйвера ядра. Старый драйвер WinRing0 помечается Microsoft Defender как уязвимый; новые версии LHM переходят на PawnIO. Нужна проверка актуальной версии перед подключением; без неё температуры CPU будут «недоступны».
6. **Безопасный рабочий стол** (UAC, экран входа, Ctrl+Alt+Del) недоступен для захвата и `SendInput` из обычного процесса — удалённый экран это честно покажет.
7. **WebRTC** на Windows/.NET не имеет «официального» SDK: кандидаты — SIPSorcery (BSD-3, чистый C#) + аппаратное H.264 через Media Foundation, либо нативный libwebrtc. На iOS — сборка Google WebRTC. Решение — после технического спайка на P5.
8. **Среда разработки.** Облачный контейнер (Linux) не может собрать iOS-приложение (нужен Xcode на Mac) и не может запускать Windows-специфичный код. Здесь можно собирать и тестировать `Nexus.Core`, протокол и схемы (после установки .NET SDK с вашего разрешения).

## 9. Реестр внешних зависимостей
Пока пуст. Кандидаты (не добавлены, требуют проверки и одобрения):

| Зависимость | Где | Лицензия | Этап |
|-------------|-----|----------|------|
| Microsoft.Extensions.Hosting.WindowsServices | Agent | MIT | P1 |
| Microsoft.Data.Sqlite | Agent | MIT | P1 |
| xUnit | Tests | Apache-2.0 | P1 |
| LibreHardwareMonitorLib | Monitoring | MPL-2.0 | P4 |
| NVML (через P/Invoke, без пакета) | Monitoring | NVIDIA EULA (DLL с драйвером) | P4 |
| SIPSorcery / libwebrtc | Remote | BSD-3 / BSD | P5 (спайк) |
| WebRTC (iOS) | iOS | BSD | P5 (спайк) |
| OpenRGB SDK (сетевой протокол, без пакета) | RGB | протокол OpenRGB, GPL-2 у самого OpenRGB (отдельный процесс) | P10 |
