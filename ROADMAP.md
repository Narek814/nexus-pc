# Nexus PC — Roadmap

Порядок следует ТЗ (P0–P11) с обоснованными изменениями, отмеченными **[изм.]**.

| Этап | Содержание | Критерий готовности |
|------|-----------|---------------------|
| **P0** ✅ | Архитектура, безопасность, протокол v1 (схемы + фикстуры), план | Документы утверждены владельцем. |
| **P1a** ✅ | `Nexus.Core` (модели, каталог команд, разрешения, валидатор) + `Nexus.Tests` | Тесты зелёные (можно на Linux). |
| **P1b** 🟨 | `Nexus.Agent`: служба, WSS, сопряжение (код/ссылка; QR — в P1c), вход, power control, аудит; `Nexus.DevClient`; CI | Код и тесты готовы; ждёт ручной проверки на Windows (docs/TESTING.md W1–W13). |
| **P1c** | `Nexus.Tray`: IPC, запуск приложений, профили, блокировка | Запуск профиля Gaming с тестового клиента. |
| **P1d** | **[изм.]** Базовые метрики CPU/RAM/диски/сеть через Performance Counters | Нужны Command Center уже на P2; полноценный Monitoring остаётся на P4. |
| **P2** | iOS: `NexusKit`, дизайн-система, сопряжение, Command Center, Devices, Profiles, Power | На iPhone: сопряжение, запуск профиля, выключение. |
| **P2.0** | **[изм.]** CI GitHub Actions: Windows (`dotnet test`) и macOS (`swift test`, `xcodebuild` без подписи → .ipa-артефакт) | Без Mac это единственный способ собрать iOS (D12). |
| **P3** | Siri, Widgets, Live Activities, Notifications | **[изм.]** Пуши в фоне — через ntfy/Telegram (D11), не APNs. |
| **P4** | Monitoring (GPU, температуры, частоты, история, графики), Smart Alerts | Недоступные датчики честно помечены. |
| **P5** | Remote Touchpad, затем спайк WebRTC → Remote Screen | Задержка и качество измерены. |
| **P6** | Automation Studio (движок на ПК), PC History | |
| **P7** | File Transfer, Clipboard Sync | Проверка SHA-256, отмена, докачка. |
| **P8** | Multi-PC UI и переключение | **[изм.]** Модель данных multi-PC закладывается с P1. |
| **P9** | Nexus AI: интерфейс провайдера, разбор по правилам, адаптер Ollama на ПК | AI не может выйти за каталог команд. |
| **P10** | RGB Sync (OpenRGB, Dynamic Lighting) | Поддержка подтверждена на реальном оборудовании. |
| **P11** | Установщик Windows, автообновление, инструкции Tailscale/ntfy/Sideloadly | **[изм.]** Gateway, Relay и TestFlight — только если владелец когда-либо решит платить (D11). |

Удалённый доступ до P11 — через пользовательский VPN (Tailscale/WireGuard): работает без своей инфраструктуры и без открытых портов.
