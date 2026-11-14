# Nexus PC — Установка на Windows (этап P1b)

> **Статус:** эти шаги ещё не выполнялись на реальном Windows-ПК. Код агента собран и проверен
> тестами на Linux, а Windows-ветки проверяет CI на `windows-latest`. Если какой-то шаг не
> сработает, пришлите вывод команды целиком — исправим.

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

```powershell
sc.exe create NexusPCAgent binPath= "\"C:\Program Files\NexusPC\Agent\Nexus.Agent.exe\"" start= auto DisplayName= "Nexus PC Agent"
```

Перезапуск при сбое:

```powershell
sc.exe failure NexusPCAgent reset= 86400 actions= restart/5000/restart/5000/restart/60000
```

```powershell
sc.exe start NexusPCAgent
```

Служба работает от `LocalSystem` (см. SECURITY.md §4). Данные: `C:\ProgramData\NexusPC` (доступ только у SYSTEM и администраторов). Сертификат: хранилище «Локальный компьютер → Личное», имя `Nexus PC Agent <pcId>`.

## 5. Брандмауэр — только частная сеть

```powershell
New-NetFirewallRule -DisplayName "Nexus PC Agent" -Direction Inbound -Program "C:\Program Files\NexusPC\Agent\Nexus.Agent.exe" -Protocol TCP -LocalPort 47800 -Profile Private -Action Allow
```

Проверьте, что домашняя сеть отмечена как **частная**: Параметры → Сеть и Интернет → ваша сеть → «Частная сеть». В публичных сетях агент недоступен.

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

Затем удалите `C:\Program Files\NexusPC`, `C:\ProgramData\NexusPC` и сертификат `Nexus PC Agent …` в `certlm.msc` → Личное → Сертификаты.

## Известные ограничения P1b

- Адреса для прослушивания выбираются при запуске службы. Если IP компьютера сменился (DHCP), перезапустите службу: `sc.exe stop NexusPCAgent`, затем `sc.exe start NexusPCAgent`. Совет: закрепите IP за ПК в настройках роутера.
- QR-кода пока нет (появится с Nexus.Tray), поиска ПК в сети (Bonjour) тоже нет (P2).
- Блокировка, запуск приложений и профили отвечают `command.notImplemented` (P1c).
