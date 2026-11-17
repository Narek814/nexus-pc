# Nexus PC — Установка на Windows (этапы P1b–P1c)

> **Статус:** шаги 1–6 (служба) проверены на реальном ПК. Шаг 7 (Nexus.Tray) ещё не выполнялся на
> реальном ПК: приложение собрано, его логика проверена тестами, Windows-ветки — CI на `windows-latest`.
> Если какой-то шаг не сработает, пришлите вывод команды целиком — исправим.

Всё бесплатно. Нужны Windows 10 22H2+ или Windows 11 и права администратора.

## 1. Инструменты (один раз)

Откройте **PowerShell от имени администратора** (Пуск → PowerShell → правой кнопкой → «Запуск от имени администратора»):

```powershell
winget install Microsoft.DotNet.SDK.10
```

```powershell
winget install Git.Git
```

Закройте и снова откройте PowerShell от имени администратора, чтобы команды `dotnet` и `git` стали доступны.

## 2. Код

```powershell
cd $env:USERPROFILE
```

```powershell
git clone https://github.com/Narek814/nexus-pc.git
```

Если репозиторий приватный, Git попросит войти в GitHub в браузере.

Проверка, что всё собирается и тесты проходят на вашем ПК:

```powershell
dotnet test nexus-pc\windows\Nexus.slnx
```

## 3. Сборка агента

```powershell
dotnet publish nexus-pc\windows\Nexus.Agent -c Release -o "C:\Program Files\NexusPC\Agent"
```

## 4. Служба Windows

Проверка, что сборка на месте (должно быть `True`):

```powershell
Test-Path "C:\Program Files\NexusPC\Agent\Nexus.Agent.exe"
```

Создание службы. Используется `New-Service`, а не `sc.exe create`: в PowerShell кавычки внутри аргументов `sc.exe` искажаются, и путь с пробелом ломается (ошибка 1060 на следующих шагах).

```powershell
New-Service -Name NexusPCAgent -BinaryPathName '"C:\Program Files\NexusPC\Agent\Nexus.Agent.exe"' -DisplayName "Nexus PC Agent" -StartupType Automatic
```

Перезапуск при сбое:

```powershell
sc.exe failure NexusPCAgent reset= 86400 actions= restart/5000/restart/5000/restart/60000
```

```powershell
sc.exe start NexusPCAgent
```

```powershell
sc.exe query NexusPCAgent
```

Ожидается `STATE : 4 RUNNING`. Служба работает от `LocalSystem` (см. SECURITY.md §4). Данные: `C:\ProgramData\NexusPC` (доступ только у SYSTEM и администраторов). Сертификат: хранилище «Локальный компьютер → Личное», имя `Nexus PC Agent <pcId>`.

## 5. Брандмауэр — только частная сеть

```powershell
New-NetFirewallRule -DisplayName "Nexus PC Agent" -Direction Inbound -Program "C:\Program Files\NexusPC\Agent\Nexus.Agent.exe" -Protocol TCP -LocalPort 47800 -Profile Private -Action Allow
```

Проверьте, что домашняя сеть отмечена как **частная** — иначе iPhone получит «ПК не ответил вовремя» (так было на реальном ПК: Windows отметила домашний Wi-Fi как Public):

```powershell
Get-NetConnectionProfile
```

Если у вашей сети `NetworkCategory : Public`, сделайте её частной (окно администратора; `9` — ваш `InterfaceIndex` из вывода выше):

```powershell
Set-NetConnectionProfile -InterfaceIndex 9 -NetworkCategory Private
```

В общественных сетях (кафе и т. п.) оставляйте Public — там агент недоступен.

Порт в роутере **не открывайте**. Для доступа из интернета — Tailscale (этап P2/P11).

## 6. Проверка с тестовым клиентом

Окно 1 — PowerShell **от имени администратора**:

```powershell
& "C:\Program Files\NexusPC\Agent\Nexus.Agent.exe" pair
```

Появятся код и ссылка `nexuspc://pair?...`. Не закрывайте окно.

Окно 2 — обычный PowerShell (подставьте ссылку из окна 1 в кавычках):

```powershell
dotnet run --project $env:USERPROFILE\nexus-pc\windows\Nexus.DevClient -- pair "nexuspc://pair?..." --host 127.0.0.1
```

В обоих окнах появится одинаковый 6-значный код проверки. Если совпадает — в окне 1 нажмите `y` и Enter.

Дальше в окне 2:

```powershell
dotnet run --project $env:USERPROFILE\nexus-pc\windows\Nexus.DevClient -- send pc.status --host 127.0.0.1
```

Выключение через 10 минут (Windows покажет уведомление), затем отмена:

```powershell
dotnet run --project $env:USERPROFILE\nexus-pc\windows\Nexus.DevClient -- send power.shutdown --delay 600 --confirm --host 127.0.0.1
```

```powershell
dotnet run --project $env:USERPROFILE\nexus-pc\windows\Nexus.DevClient -- send power.cancel --host 127.0.0.1
```

Список устройств и отзыв (окно администратора):

```powershell
& "C:\Program Files\NexusPC\Agent\Nexus.Agent.exe" devices
```

```powershell
& "C:\Program Files\NexusPC\Agent\Nexus.Agent.exe" devices revoke <deviceId>
```

## 7. Nexus.Tray — приложение в трее (P1c)

Трей работает от вашего имени (не от администратора): запускает программы, блокирует экран, показывает QR-код для сопряжения. Служба и трей проверяют друг друга по пути установки, поэтому пути ниже менять нельзя.

Сборка (окно администратора):

```powershell
dotnet publish $env:USERPROFILE\nexus-pc\windows\Nexus.Tray -c Release -o "C:\Program Files\NexusPC\Tray"
```

Запуск **без** прав администратора (через Проводник — так он стартует с обычными правами, даже из окна администратора):

```powershell
explorer.exe "C:\Program Files\NexusPC\Tray\Nexus.Tray.exe"
```

Справа внизу, у часов, появится круглый значок «N» (возможно, под стрелкой «^»). Цветной — служба подключена, серый — нет связи.

Правой кнопкой по значку:
- **Запускать при входе в Windows** — включите, чтобы не запускать вручную.
- **Приложения и профили…** — добавьте программы (из меню «Пуск» или `.exe`), затем профили. Кнопка «Создать стандартные» добавит Gaming, Developer, Creator, Night — отметьте в каждом нужные программы и нажмите «Сохранить».
- **Подключить iPhone…** — QR-код и кнопка «Копировать ссылку». Пока iPhone-приложения нет, ссылку используйте с DevClient (как в шаге 6); подтверждение с кодом проверки появится в этом окне.

Проверка с DevClient (обычный PowerShell, после сопряжения):

```powershell
$nx = "$env:USERPROFILE\nexus-pc\windows\Nexus.DevClient\bin\Debug\net10.0\Nexus.DevClient.exe"
```

```powershell
& $nx send apps.list --host 127.0.0.1
```

Запуск программы по её `id` из `apps.list` (например, `steam`):

```powershell
& $nx send apps.launch --app steam --host 127.0.0.1
```

```powershell
& $nx send profiles.run --profile gaming --host 127.0.0.1
```

```powershell
& $nx send power.lock --host 127.0.0.1
```

## 8. Подсветка через OpenRGB (P10, по желанию)

Nexus PC управляет подсветкой через бесплатную программу **OpenRGB** (openrgb.org, версия 1.0 или новее). Сама программа ставится и запускается вами; агент подключается к ней только внутри ПК (`127.0.0.1:6742`).

1. **Armoury Crate** перехватывает подсветку ASUS и мешает OpenRGB. Закройте его, а в Armoury Crate → Настройки отключите запуск при старте (или удалите Aura/Lighting-службу, если подсветка всё равно сбрасывается).
2. Запустите OpenRGB **от имени администратора** (нужно для подсветки платы). Проверка, что SDK-сервер слушает (в OpenRGB 1.0 отдельной галочки нет, сервер был включён сразу): `Get-NetTCPConnection -LocalPort 6742 -State Listen` → строка `127.0.0.1 6742 Listen`. Если плата не появляется, установите драйвер **PawnIO** по инструкции на openrgb.org — с версии 1.0 OpenRGB использует его для доступа к SMBus.
3. Вкладка **SDK Server** → **Start Server**.
4. В настройках OpenRGB (**Settings → General**) включите запуск при входе, свёрнутый запуск и запуск сервера (названия пунктов — *Start At Login*, *Start Minimized*, *Start Server*; если в вашей версии они называются иначе, пришлите снимок экрана).
5. **Вентиляторы и кулер на разъёмах ARGB платы** (у ASUS — зоны «Addressable RGB Header 1/2/3»): OpenRGB не знает длину подключённых лент и считает её 0, поэтому они не горят. Вкладка **Devices** → плата → **Zone** → каждый «Addressable RGB Header» → **Edit Device** → число светодиодов (например, 60) → сохранить. Проверено на ASUS PRIME B660M-A WIFI D4: после этого вентиляторы и кулер управляются и из OpenRGB, и с iPhone.
6. На iPhone: Home → «Подсветка».

Подсветку монитора **Skydimo** OpenRGB не поддерживает — она остаётся в приложении Skydimo.

Порт 6742 в брандмауэре **не открывайте** — он нужен только внутри ПК.

## Обновление

```powershell
sc.exe stop NexusPCAgent
```

```powershell
git -C $env:USERPROFILE\nexus-pc pull
```

```powershell
dotnet publish $env:USERPROFILE\nexus-pc\windows\Nexus.Agent -c Release -o "C:\Program Files\NexusPC\Agent"
```

```powershell
sc.exe start NexusPCAgent
```

Трей: сначала закройте его (иначе сборка не сможет заменить файлы — «блокирует этот файл»). Проверить, что трей обновился: `(Get-Item "C:\Program Files\NexusPC\Tray\Nexus.Core.dll").LastWriteTime` — должно быть текущее время (на реальном ПК трей однажды остался старым, и тачпад отвечал `Tray does not handle 'input.move'`):

```powershell
Stop-Process -Name Nexus.Tray -ErrorAction SilentlyContinue
```

```powershell
dotnet publish $env:USERPROFILE\nexus-pc\windows\Nexus.Tray -c Release -o "C:\Program Files\NexusPC\Tray"
```

```powershell
explorer.exe "C:\Program Files\NexusPC\Tray\Nexus.Tray.exe"
```

## Удаление

```powershell
sc.exe stop NexusPCAgent
```

```powershell
sc.exe delete NexusPCAgent
```

```powershell
Remove-NetFirewallRule -DisplayName "Nexus PC Agent"
```

Выйдите из трея и отключите «Запускать при входе». Затем удалите `C:\Program Files\NexusPC`, `C:\ProgramData\NexusPC`, `%LOCALAPPDATA%\NexusPC` (ваш список программ и профили) и сертификат `Nexus PC Agent …` в `certlm.msc` → Личное → Сертификаты.

## Известные ограничения

- Поиска ПК в сети (Bonjour) пока нет (P2).
- Без запущенного трея блокировка, приложения и профили отвечают `session.unavailable`, а обычное выключение идёт через службу с отсчётом 30 секунд.
- Приложения из Microsoft Store пока не добавляются (только ярлыки «Пуск» и `.exe`).
