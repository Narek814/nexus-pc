# Windows — трей, окна, установщик

Задание E5-4. Связанные документы: `foundations.md` (раздел 8 — правила текстов и названия разрешений, раздел 9 — Windows), `error-texts.md` (коды агента и сервера аккаунтов), `ios-account.md` (A7 «Войти на ПК», A8 «Мои устройства»), `README.md` (заглушки).

Источник текущего состояния — код (только чтение): `windows/Nexus.Tray/TrayContext.cs`, `TrayText.cs`, `Strings.resx`, `Strings.ru.resx`, `AccountForm.cs`, `ElevatedAccount.cs`, `PairingForm.cs`, `DevicesForm.cs`, `AppsForm.cs`, `Program.cs`, `AgentConnection.cs`; `windows/Nexus.Setup/Program.cs`, `Installer.cs`.

Опора: Windows App design — https://learn.microsoft.com/windows/apps/design/ ; уведомления и область уведомлений — https://learn.microsoft.com/windows/win32/shell/notification-area ; UAC и «щит» на кнопках — https://learn.microsoft.com/windows/win32/uxguide/winenv-uac ; мастера — https://learn.microsoft.com/windows/win32/uxguide/win-wizards ; доступность WinForms — https://learn.microsoft.com/dotnet/desktop/winforms/advanced/walkthrough-creating-an-accessible-windows-based-application ; WCAG 2.2 1.4.3.

## 1. Цель и пользователи

Трей — единственная точка входа на ПК для человека, который сидит за ним: подключить iPhone, решить, что iPhone можно делать, остановить показ экрана и управление, привязать ПК к аккаунту. Пользователь — владелец ПК, не разработчик: в тексте нет служб, каналов, кодов выхода, GUID, PowerShell, имён модулей и этапов.

Общие правила (дополняют `foundations.md`, 8):

- Значок трея называется «значок N» (EN: «N icon») — так же, как в текстах iPhone.
- Все строки трея — в `Strings.resx` / `Strings.ru.resx` через `TrayText`; литералов в `*.cs` не остаётся. Язык — по настройке «Язык» (раздел 6), по умолчанию — язык интерфейса Windows (как сейчас в `TrayText.cs:16`).
- Подробности ошибок (сообщение службы, Win32-код, тип исключения) — только в `%LOCALAPPDATA%\NexusPC\tray.log`. На экране — понятный текст по коду ошибки (`error-texts.md`), иначе общий `TrayErrorGeneric`.
- Действия, требующие прав администратора, помечаются щитом UAC на кнопке (`Button.FlatStyle = System` + `BCM_SETSHIELD` или значок щита рядом) и строкой `AccountAdminNote`/`AdminNote`.
- Подсказка значка (`NotifyIcon.Text`) — не длиннее 63 символов, проверено для EN и RU.

## 2. Меню трея

### 2.1 Структура

```
┌───────────────────────────────────────────────┐
│ ● Nexus PC готов                     (неактивн.)│  статус (TrayStatus*)
│   Повторить                    (только ключ ПК) │  KeyRetry, щит UAC
├───────────────────────────────────────────────┤
│   Подключить iPhone…                          │  → PairingForm
│   Устройства и разрешения…                    │  → DevicesForm
│   Программы и профили…                        │  → AppsForm
│   Подсветка (OpenRGB)…                        │  → окно OpenRGB, щит UAC
│   Открыть папку файлов с iPhone               │  только если папка доступна
├───────────────────────────────────────────────┤
│ ✓ Запретить управление мышью и клавиатурой    │  переключатель
│ ✓ Запретить показ экрана                      │  переключатель (+ кто смотрит)
├───────────────────────────────────────────────┤
│   Аккаунт…                                    │  → AccountForm
│   Настройки…                                  │  → SettingsForm (новое)
│   Выход                                       │
└───────────────────────────────────────────────┘
```

Изменения порядка относительно `TrayContext.cs:101-132`: «Устройства и разрешения» поднимается вторым (к нему ведут все тексты iPhone о разрешениях); запреты собираются в отдельную группу; «Запускать при входе в Windows» переносится в «Настройки» (раздел 6); «Аккаунт» и «Настройки» — в нижнюю группу. Новых функций нет: «Настройки» объединяет автозапуск, язык и проверку обновлений (пункт плана E5-4).

Двойной щелчок по значку — открывает «Устройства и разрешения» вместо «Программы и профили» (`TrayContext.cs:142`): это главное окно для решений о доступе. Если владелец хочет оставить текущее поведение — не блокирует остальное.

### 2.2 Состояния строки статуса и подсказки значка

| Состояние | Условие в коде | Строка меню (ключ) | Подсказка значка (ключ) | Значок |
|-----------|----------------|--------------------|-------------------------|--------|
| Готов | агент подключён | `TrayStatusReady` | `TrayTipReady` | цветной N |
| Подключение | старт, до первого ответа | `TrayStatusConnecting` | `TrayTipConnecting` | серый N |
| Служба не запущена | `AgentConnection` не нашёл/не дождался службу | `TrayStatusServiceDown` | `TrayTipServiceDown` | серый N |
| Служба не прошла проверку | проверка процесса/пути/владельца канала не прошла (`AgentConnection.cs:66`, `127-142`) | `TrayStatusServiceUntrusted` | `TrayTipServiceDown` | серый N |
| Ключ ПК недоступен | `state = keyUnavailable` | `KeyUnavailable` | `KeyUnavailableTip` | серый N + пункт `KeyRetry` |
| Экран показывается | `ScreenSharingChanged` с именем | без изменений | `TrayTipScreenViewer` | цветной N |

Внутренние тексты `AgentConnection.cs` («канал открыт другим процессом…», «служба NexusPC.Agent не найдена…», имя типа исключения) в меню **не показываются** (`TrayContext.cs:90-96` сейчас выводит их обрезанными до 90 символов) — только в журнал.

### 2.3 Тексты меню и уведомлений

| Ключ | en | ru | Где / замена |
|------|----|----|--------------|
| `TrayStatusReady` | Nexus PC is ready | Nexus PC готов | статус; было «Служба подключена» |
| `TrayStatusConnecting` | Starting Nexus PC… | Запуск Nexus PC… | статус |
| `TrayStatusServiceDown` | Nexus PC isn't running. Restart the PC; if it repeats, reinstall Nexus PC. | Nexus PC не работает. Перезагрузите ПК; если повторится — переустановите Nexus PC. | статус; было «Нет связи со службой» |
| `TrayStatusServiceUntrusted` | Nexus PC is damaged. Reinstall it. | Nexus PC повреждён. Переустановите его. | статус |
| `TrayTipReady` | Nexus PC — ready | Nexus PC — готов | подсказка |
| `TrayTipConnecting` | Nexus PC — starting… | Nexus PC — запуск… | подсказка |
| `TrayTipServiceDown` | Nexus PC — not running | Nexus PC — не работает | подсказка; было «нет связи со службой» |
| `TrayTipScreenViewer` | Nexus PC — "{0}" sees the screen | Nexus PC — экран видит «{0}» | подсказка; имя устройства обрезать так, чтобы вся строка ≤ 63 |
| `KeyUnavailable` | Phone connections are off: Nexus PC can't open its security key | Подключение телефонов выключено: Nexus PC не может открыть свой ключ защиты | статус, уведомление (уточнение существующего) |
| `KeyUnavailableTip` | Nexus PC — phone connections are off | Nexus PC — подключение телефонов выключено | без изменений |
| `KeyRetry` | Try Again | Повторить | без изменений, щит UAC |
| `MenuPair` | Connect iPhone… | Подключить iPhone… | без изменений по смыслу |
| `MenuDevices` | Devices and Permissions… | Устройства и разрешения… | без изменений |
| `MenuApps` | Apps and Profiles… | Программы и профили… | было «Приложения и профили» — как на iPhone («Программы», `permission.name.apps`) |
| `MenuLighting` | Lighting (OpenRGB)… | Подсветка (OpenRGB)… | было «Настроить подсветку (OpenRGB)…» |
| `MenuFiles` | Open Folder of Files from iPhone | Открыть папку файлов с iPhone | было «…с телефона» |
| `MenuStopInput` | Block Mouse and Keyboard Control | Запретить управление мышью и клавиатурой | переключатель; «с телефона» убрано — понятно из контекста |
| `MenuStopScreen` | Block Screen Sharing | Запретить показ экрана | переключатель |
| `MenuStopScreenViewer` | Block Screen Sharing ("{0}" is watching) | Запретить показ экрана (сейчас смотрит «{0}») | переключатель во время показа |
| `AccountMenu` | Account… | Аккаунт… | без изменений |
| `MenuSettings` | Settings… | Настройки… | новый пункт |
| `MenuExit` | Exit | Выход | без изменений |
| `NoticeScreenViewer` | "{0}" can see this PC's screen. To stop: right-click the N icon → Block Screen Sharing. | «{0}» видит экран этого ПК. Остановить: правой кнопкой по значку N → «Запретить показ экрана». | уведомление (`TrayContext.cs:168`) |
| `NoticeOpenRgbMissing` | OpenRGB wasn't installed with Nexus PC. Run the Nexus PC installer again. | OpenRGB не установлен вместе с Nexus PC. Запустите установщик Nexus PC ещё раз. | уведомление (`TrayContext.cs:211`); имя файла установщика убрано |
| `NoticeLightingConflict` | Another program on the PC has taken over the lighting. | Подсветкой управляет другая программа на ПК. | уведомление (`TrayContext.cs:270`), совпадает с `rgb.busy` на iPhone |
| `NoticeAdminDeclined` | Cancelled: this needs administrator permission. | Отменено: для этого нужно разрешение администратора. | уведомление, если отказ UAC в `RunAdminCommand` (сейчас молча, `TrayContext.cs:229-232`) |
| `TrayErrorGeneric` | Something went wrong. Try again. If it repeats, restart the PC. | Что-то пошло не так. Повторите. Если повторяется — перезагрузите ПК. | общий текст ошибки в окнах |

Заголовок всех уведомлений — `Nexus PC`. Уведомления `ActionPerformed` (тексты приходят из `TrayCommandHandler`) в этом документе не пересматривались — сверка при E5-5.

### 2.4 Запрос показа экрана (системное окно)

`TrayContext.cs:56-59`: остаётся `MessageBox` поверх всего с кнопкой «Нет» по умолчанию.

| Ключ | en | ru |
|------|----|----|
| `ScreenAskTitle` | Nexus PC — screen sharing | Nexus PC — показ экрана |
| `ScreenAskBody` | "{0}" wants to see this PC's screen.\n\nAllow? The answer is remembered for this device. You can block screen sharing later in the N icon menu. | «{0}» хочет видеть экран этого ПК.\n\nРазрешить? Ответ запомнится для этого устройства. Запретить показ потом можно в меню значка N. |

### 2.5 Запуск трея от имени администратора (`Program.cs:33-34`)

| Ключ | en | ru |
|------|----|----|
| `ElevatedTrayTitle` | Nexus PC | Nexus PC |
| `ElevatedTrayBody` | Nexus PC was started as administrator. Apps started from iPhone would get these rights too.\n\nStart it the usual way, from the Start menu or File Explorer, without "Run as administrator". | Nexus PC запущен с правами администратора. Тогда программы, запущенные с iPhone, тоже получили бы эти права.\n\nЗапустите его обычным способом — из меню «Пуск» или Проводника, без «Запуск от имени администратора». |

## 3. Окно «Аккаунт» (`AccountForm.cs`)

### 3.1 Решения владельца (сохраняются дословно)

1. userCode показывает обычное окно «Аккаунт», повышенный процесс без UI.
2. Отказ UAC = «Отменено».
3. Отдельный текст «не удалось запросить права администратора» для прочих ошибок (после E5-0).
4. Почту аккаунта НЕ показывать (показывать факт привязки и план).
5. Отвязка локальная — текст поясняет, что запись ПК в аккаунте удаляется на iPhone/сайте.
6. Вход только через трей, опрос пока окно открыто (без фоновой задачи).

Следствия для дизайна:
- (1) Код показывает только `AccountForm`, полученный через `login.status` обычного трея. Повышенный `Nexus.Tray.exe` окон не открывает и ничего не выводит (как сейчас, `ElevatedAccount.cs:11-16`).
- (2) `AdminDeclined` (код 1223, E5-0) → `ProblemAdminDeclined`.
- (3) Ошибка запуска повышенного процесса (не 1223, `ElevatedAccount.cs:49-56`; `Process.Start` вернул null; нет пути к exe) → новый `ProblemElevationFailed`. Сейчас она сводится к `ExitCodeOfError("failed")` и показывает тот же текст, что отказ службы (`ProblemFailed`), — нужно разделить (ЗАДАНИЕ W-ACC, раздел 8).
- (4) Ни в одном состоянии нет почты и идентификатора аккаунта; только «привязан» и тариф.
- (5) «Отвязать» удаляет привязку на этом ПК; текст подтверждения говорит, где удалить запись ПК из аккаунта.
- (6) Опрос `login.status` каждые 5 с — только пока окно открыто. Сейчас `AccountController` опрашивает и при закрытом окне (`AccountForm.cs:8-9`, `_poll.Enabled = Model.ShouldPoll`) — привести к решению. Разовая проверка при старте трея (`CheckAsync(atStartup: true)`) — не фоновая задача и остаётся: она показывает `NoticeResuming`/`NoticeSignedIn`/`NoticeRetry`.

### 3.2 Схема и состояния

```
┌ Nexus PC — аккаунт ─────────────────────────────────┐
│ Аккаунт Nexus PC                                    │  AccountHeading
│                                                     │
│ [текст состояния]                                   │  _text
│                                                     │
│            K7Q2-M9XD                                │  _code (только Pending)
│                                                     │
│ [подробность: срок кода / тариф / заметка UAC]      │  _detail
│ [проблема]                                          │  _problem
│                                                     │
│ [🛡 Основная кнопка]            [Вторая кнопка]     │
│ Удалить аккаунт на сайте ↗                          │  только SignedIn
└─────────────────────────────────────────────────────┘
```

| Состояние (`AccountPhase`) | Текст | Код | Подробность | Основная | Вторая |
|----------------------------|-------|-----|-------------|----------|--------|
| `Unknown` | `AccountChecking` | — | — | скрыта | `AccountClose` |
| `SignedOut` | `AccountSignedOut` | — | `AccountAdminNote` | `AccountSignIn` 🛡 | `AccountClose` |
| `Starting` | `AccountStarting` | — | — | `AccountSignIn`, выключена | `AccountClose` |
| `Pending` | `AccountCodeHint` | userCode, моноширинный, крупно | `AccountExpiresIn` | скрыта | `AccountCancel` |
| `SignedIn` | `AccountLinked` | — | `AccountPlan` + `AccountAdminNote` | `AccountUnlink` 🛡 | `AccountClose` |
| `Unavailable` | текст проблемы в `_text` (не пустой `_text`, как сейчас `AccountForm.cs:282-285`) | — | — | скрыта | `AccountClose` |

Проблема (`_problem`) показывается в любом состоянии, кроме `Unavailable` (там она — основной текст). Цвет проблемы — `Theme.Violet` (контраст 5.2:1 на `Background` — проходит AA) + значок предупреждения слева, чтобы цвет не был единственным признаком.

Переходы:
- `SignedOut` → «Войти» → `Starting` → UAC: отказ → `SignedOut` + `ProblemAdminDeclined`; ошибка запуска → `SignedOut` + `ProblemElevationFailed`; служба ответила `admin.required` → `SignedOut` + `ProblemAdminRequired`; успех → `login.status` → `Pending`.
- `Pending` → подтверждено на iPhone → `SignedIn` + уведомление `NoticeSignedIn`; срок истёк → `SignedOut` + `ProblemExpired`; «Отмена» → `SignedOut`.
- Закрытие окна в `Pending` — опрос останавливается, попытка входа на стороне службы не отменяется. При повторном открытии окно запрашивает `login.status` и снова показывает код (если не истёк) или результат.
- `SignedIn` → «Отвязать» → подтверждение `AccountUnlinkConfirm` (кнопка по умолчанию «Нет») → UAC → `SignedOut`.
- «Удалить аккаунт на сайте» — ссылка (`LinkLabel`), открывает `{ACCOUNT_URL}` в браузере по умолчанию. Окно ничего не удаляет само.

Вход в аккаунт на ПК начинается только здесь (решение 6); других точек входа (установщик, уведомления) нет.

### 3.3 Тексты

Существующие ключи `Strings.resx` — с уточнениями там, где столбец «Изм.» отмечен.

| Ключ | en | ru | Изм. |
|------|----|----|------|
| `AccountMenu` | Account… | Аккаунт… | — |
| `AccountTitle` | Nexus PC — Account | Nexus PC — аккаунт | регистр EN |
| `AccountHeading` | Nexus PC Account | Аккаунт Nexus PC | регистр EN |
| `AccountChecking` | Checking… | Проверяем… | — |
| `AccountSignedOut` | This PC isn't linked to a Nexus PC account. Sign in to use your Pro plan on this PC. | Этот ПК не привязан к аккаунту Nexus PC. Войдите, чтобы на этом ПК действовал ваш тариф Pro. | да |
| `AccountSignIn` | Sign In | Войти | регистр EN |
| `AccountStarting` | Getting a code… | Получаем код… | — |
| `AccountCodeHint` | On your iPhone, open Nexus PC → More → Account → Sign In on PC and enter this code: | На iPhone откройте Nexus PC → «Ещё» → «Аккаунт» → «Войти на ПК» и введите этот код: | да, путь как в `ios-account.md` A7 |
| `AccountExpiresIn` | The code is valid for {0} more. Waiting for confirmation on iPhone… | Код действует ещё {0}. Ждём подтверждения на iPhone… | — |
| `AccountCancel` | Cancel | Отмена | — |
| `AccountClose` | Close | Закрыть | — |
| `AccountLinked` | This PC is linked to the Nexus PC account that confirmed the sign-in on iPhone. The account's email isn't stored on this PC; you can see it in the app on iPhone. | Этот ПК привязан к аккаунту Nexus PC, который подтвердил вход на iPhone. Почта аккаунта на ПК не хранится — её видно в приложении на iPhone. | пунктуация |
| `AccountPlan` | Plan: {0} | Тариф: {0} | ru «План» → «Тариф» (как на iPhone) |
| `PlanFree` | Free | Бесплатный | — |
| `PlanPro` | Pro | Pro | — |
| `AccountUnlink` | Unlink | Отвязать | — |
| `AccountUnlinkConfirm` | Unlink this PC from the account? Pro stops working on this PC; free features keep working.\n\nThe PC stays in your account list. To remove it there, use the app on iPhone (Account → My Devices) or the website. | Отвязать этот ПК от аккаунта? Pro на этом ПК перестанет действовать, бесплатные функции продолжат работать.\n\nПК останется в списке аккаунта. Удалить его оттуда можно в приложении на iPhone («Аккаунт» → «Мои устройства») или на сайте. | да, решение 5 |
| `AccountDeleteLink` | Delete Account on Website | Удалить аккаунт на сайте | новый; открывает `{ACCOUNT_URL}` |
| `AccountAdminNote` | Windows will ask for administrator permission. | Windows запросит разрешение администратора. | — |
| `ProblemRateLimited` | Too many attempts. Wait a minute and try again. | Слишком много попыток. Подождите минуту и попробуйте снова. | — |
| `ProblemPcsLimit` | Your account already has the maximum number of PCs. Remove one in the app on iPhone (Account → My Devices) and try again. | В аккаунте уже максимальное число ПК. Удалите один в приложении на iPhone («Аккаунт» → «Мои устройства») и попробуйте снова. | путь |
| `ProblemNetwork` | Can't reach the Nexus PC server. Check the internet connection and try again. | Нет связи с сервером Nexus PC. Проверьте подключение к интернету и попробуйте снова. | — |
| `ProblemNotConfigured` | Signing in to an account isn't available in this version of Nexus PC. | Вход в аккаунт недоступен в этой версии Nexus PC. | — |
| `ProblemServiceUnavailable` | Nexus PC isn't running. Restart the PC and try again. | Nexus PC не работает. Перезагрузите ПК и попробуйте снова. | да, без слова «служба» |
| `ProblemExpired` | Sign-in wasn't finished in time. Click Sign In to get a new code. | Вход не завершён вовремя. Нажмите «Войти», чтобы получить новый код. | — |
| `ProblemFailed` | Sign-in failed. Try again. | Не удалось войти. Попробуйте снова. | — |
| `ProblemAdminRequired` | Only a Windows administrator can sign this PC in or unlink it. Sign in to Windows with an administrator account, or ask an administrator to do it. | Войти или отвязать этот ПК может только администратор Windows. Войдите в Windows под учётной записью администратора или попросите администратора. | да; текст для кода `admin.required` (E4-6) |
| `ProblemAdminDeclined` | Cancelled: signing in and unlinking need administrator permission. Click the button again and allow the Windows prompt. | Отменено: для входа и отвязки нужно разрешение администратора. Нажмите кнопку ещё раз и разрешите запрос Windows. | «права» → «разрешение» (как в `AccountAdminNote`) |
| `ProblemElevationFailed` | Couldn't request administrator permission. Try again. If it repeats, restart the PC. | Не удалось запросить права администратора. Попробуйте снова. Если повторяется — перезагрузите ПК. | **новый**, решение 3 |
| `ProblemRevoked` | This PC is no longer linked to an account. Sign in again. | ПК больше не привязан к аккаунту. Войдите снова. | новый, для `revoked`/`reuse.detected` (`error-texts.md` 4), если служба передаёт их трею; иначе не заводить |
| `ProblemClock` | Check that the date and time on this PC are correct. | Проверьте дату и время на этом ПК. | новый, для `request.stale` после автоповтора; условие как выше |
| `NoticeResuming` | Sign-in wasn't finished. Finishing it now… | Вход не завершён. Завершаем… | пунктуация |
| `NoticeSignedIn` | This PC is now linked to your Nexus PC account. | Этот ПК привязан к вашему аккаунту Nexus PC. | — |
| `NoticeRetry` | Sign-in wasn't finished. Open Account in the N icon menu and sign in again. | Вход не завершён. Откройте «Аккаунт» в меню значка N и войдите снова. | «меню Nexus PC» → «меню значка N» |

Сопоставление кодов службы трею: `admin.required` → `ProblemAdminRequired`; отказ UAC (1223) → `ProblemAdminDeclined`; прочая ошибка запуска повышенного процесса → `ProblemElevationFailed`; `session.unavailable` → `ProblemServiceUnavailable`; `rate.limited` → `ProblemRateLimited`; `pcs.limit` → `ProblemPcsLimit`; `pc.key_required` → `account.error.pcKey` из `error-texts.md` (ключ `ProblemPcKey`: en «This PC couldn't be verified. Reinstall Nexus PC.» / ru «Не удалось проверить этот ПК. Переустановите Nexus PC.»); `internal` сервера → `ProblemServiceDown`: en «The Nexus PC service is temporarily unavailable. Try again later. Free features keep working.» / ru «Сервис Nexus PC временно недоступен. Повторите позже. Бесплатные функции продолжают работать.»; `pending`/`slow_down` — без показа ошибки. Если модель пока не различает `pc.key_required`/`internal` (`AccountProblem` в `AccountLoginModel.cs:25-42`), они остаются `ProblemFailed`; расширение перечисления — только по решению E5-5.

## 4. Окно «Устройства и разрешения» (`DevicesForm.cs`)

### 4.1 Схема

```
┌ Nexus PC — устройства и разрешения ───────────────────────────┐
│ Подключённые устройства                                       │
│ ┌──────────────┐  iPhone Анны                                  │
│ │ iPhone Анны  │  Отметьте, что этому устройству можно делать. │
│ │ iPad         │  ☑ Питание — выключение, сон, блокировка      │
│ │              │  ☑ Программы — запуск и профили               │
│ │              │  ☑ Мониторинг — показатели и история          │
│ │              │  ☐ Подсветка                                  │
│ │              │  ☐ Мышь и клавиатура — полный контроль ПК     │
│ │              │  ☐ Экран — ПК спросит при первом показе       │
│ │              │  ☐ Файлы — папка «Загрузки\Nexus PC»          │
│ │              │  ☐ Буфер обмена                               │
│ │              │  ☐ Автоматизации                              │
│ └──────────────┘  [🛡 Сохранить разрешения] [Отключить устр.]  │
│ [статус]                                                      │
└───────────────────────────────────────────────────────────────┘
```

Названия разрешений — **первая часть** строки совпадает с `permission.name.*` iPhone (`foundations.md`, 8), потому что тексты iPhone ведут сюда: «значок N → Устройства и разрешения → Мышь и клавиатура». Пояснение после тире — только в трее.

Состояния: загрузка (`LoadingText`, кнопки выключены); нет связи с Nexus PC (`DevicesServiceDown` + кнопка `CommonRetry`); нет устройств (`DevicesEmpty`, кнопки выключены); выбрано устройство; несохранённые изменения (кнопка «Сохранить» активна, иначе выключена); сохранение (UAC); отказ UAC; ошибка.

### 4.2 Тексты

| Ключ | en | ru | Замена |
|------|----|----|--------|
| `DevicesTitle` | Nexus PC — Devices and Permissions | Nexus PC — устройства и разрешения | `DevicesForm.cs:42` |
| `DevicesHeading` | Connected Devices | Подключённые устройства | было «Сопряжённые устройства» |
| `DevicesHint` | Choose what this device is allowed to do. | Отметьте, что этому устройству можно делать. | `:105` |
| `PermPower` | Power — shut down, sleep, lock | Питание — выключение, сон, блокировка | `:17` |
| `PermApps` | Apps — start apps and profiles | Программы — запуск и профили | `:18` |
| `PermMonitoring` | Monitoring — stats and history | Мониторинг — показатели и история | `:19` |
| `PermRgb` | Lighting | Подсветка | `:20` |
| `PermInput` | Mouse and Keyboard — full control of the PC | Мышь и клавиатура — полный контроль ПК | `:21`, было «Тачпад и клавиатура» |
| `PermScreen` | Screen — the PC asks again the first time | Экран — ПК спросит при первом показе | `:22` |
| `PermFiles` | Files — the Downloads\Nexus PC folder | Файлы — папка «Загрузки\Nexus PC» | `:23` |
| `PermClipboard` | Clipboard | Буфер обмена | `:24` |
| `PermAutomation` | Automations | Автоматизации | `:25` |
| `DevicesSave` | Save Permissions | Сохранить разрешения | `:33`, щит UAC |
| `DevicesRevoke` | Disconnect Device | Отключить устройство | `:34` |
| `DevicesEmpty` | No devices yet. Connect iPhone: N icon → Connect iPhone. | Устройств пока нет. Подключите iPhone: значок N → «Подключить iPhone». | `:93` |
| `DevicesServiceDown` | Nexus PC isn't running. Restart the PC and try again. | Nexus PC не работает. Перезагрузите ПК и попробуйте снова. | `:74`, без `result.Message` |
| `DevicesSaved` | Saved for "{0}". Restart the app on iPhone to see the changes. | Сохранено для «{0}». Перезапустите приложение на iPhone, чтобы увидеть изменения. | `:137` |
| `DevicesSaveFailed` | Couldn't save permissions. Try again. If it repeats, restart the PC. | Не удалось сохранить разрешения. Попробуйте снова. Если повторяется — перезагрузите ПК. | `:137`, **без «(код N)»**; код — в журнал |
| `DevicesAdminDeclined` | Cancelled: changing permissions needs administrator permission. | Отменено: для изменения разрешений нужно разрешение администратора. | `:141`; при отказе UAC (1223) |
| `DevicesElevationFailed` | Couldn't request administrator permission. Try again. | Не удалось запросить права администратора. Попробуйте снова. | новое: прочие `Win32Exception` (сейчас все считаются отказом, `:139`) |
| `DevicesRevokeConfirm` | Disconnect "{0}"? It loses access right away, and its automations stop working. You can connect it again with a QR code. | Отключить «{0}»? Оно сразу потеряет доступ, а его автоматизации перестанут работать. Подключить снова можно по QR-коду. | `:153` |
| `DevicesRevoked` | "{0}" is disconnected. | «{0}» отключено. | `:160` |
| `DevicesRevokePending` | "{0}" is still being disconnected. Check the list again in a moment. | «{0}» ещё отключается. Обновите список чуть позже. | `:161` |
| `DevicesRevokeFailed` | Couldn't disconnect the device. Try again. | Не удалось отключить устройство. Попробуйте снова. | `:162`, без `result.Message` |
| `LoadingText` | Loading… | Загрузка… | общий |
| `CommonRetry` | Try Again | Повторить | общий |

Предупреждение после отключения (`warning` из ответа службы, `:164-166`) — текст службы; сверить в E5-5, что он без внутренних терминов.

## 5. Окно «Подключить iPhone» (`PairingForm.cs`)

Убирается разработческий текст `PairingForm.cs:94-96` («Пока приложения для iPhone нет… Nexus.DevClient (см. docs/SETUP.md)»). Кнопка «Копировать ссылку» — только в Debug-сборке (`#if DEBUG`): она нужна только DevClient.

```
┌ Nexus PC — подключить iPhone ───────────────────────────┐
│ Отсканируйте QR-код в приложении Nexus PC               │
│ ┌──────────┐  На iPhone откройте Nexus PC и нажмите     │
│ │  QR      │  «Подключить ПК». Наведите камеру на код.  │
│ │          │                                            │
│ └──────────┘  Окно можно не закрывать: подтверждение    │
│               появится здесь.                           │
│               Осталось 2:41                             │
│                              [Подключить] [Отклонить]   │
└─────────────────────────────────────────────────────────┘
```

Состояния: открытие → QR → подтверждение устройства (код проверки крупно, «Подключить» в фокусе) → подключено / отклонено / ошибка (+ «Начать заново»). Ошибки — по коду `error-texts.md` 3.3 (тексты для трея ниже), а не `started.Message`/`result.Message`.

| Ключ | en | ru | Замена |
|------|----|----|--------|
| `PairTitle` | Nexus PC — Connect iPhone | Nexus PC — подключить iPhone | `:31` |
| `PairScanTitle` | Scan the QR code in the Nexus PC app | Отсканируйте QR-код в приложении Nexus PC | `:79` |
| `PairOpening` | Getting ready… | Подготовка… | `:80`, было «Открываю сопряжение…» |
| `PairScanHint` | On iPhone, open Nexus PC and tap Connect PC. Point the camera at the code.\n\nYou can keep this window open: the confirmation appears here. | На iPhone откройте Nexus PC и нажмите «Подключить ПК». Наведите камеру на код.\n\nОкно можно не закрывать: подтверждение появится здесь. | `:94-96`; «Подключить ПК» = `empty.noPC.action` |
| `PairCountdown` | {0} left | Осталось {0} | `:152` |
| `PairConfirmTitle` | Confirm the Device | Подтвердите устройство | `:112` |
| `PairConfirmBody` | "{0}" wants to connect to this PC.\n\nCheck code: {1}\n\nConnect only if the same code is shown on your device. | «{0}» хочет подключиться к этому ПК.\n\nКод проверки: {1}\n\nПодключайте, только если такой же код показан на вашем устройстве. | `:113-115` |
| `PairApprove` | Connect | Подключить | `:20` |
| `PairReject` | Decline | Отклонить | `:21` |
| `PairRestart` | Start Over | Начать заново | `:22` |
| `PairDone` | Device Connected | Устройство подключено | `:131` |
| `PairDoneBody` | Allowed by default: power, apps, monitoring. Turn on the rest in Devices and Permissions. | По умолчанию разрешены питание, программы и мониторинг. Остальное включается в «Устройствах и разрешениях». | `:133` |
| `PairDeclined` | Connection Declined | Подключение отклонено | `:131` |
| `PairDeclinedBody` | Nothing was saved. | Ничего не сохранено. | `:134` |
| `PairFailedTitle` | Couldn't Connect | Подключение не выполнено | `:141` |
| `PairTimeout` | Time ran out and no device connected. Click Start Over to show a new code. | Время вышло, устройство не подключилось. Нажмите «Начать заново», чтобы показать новый код. | `:107` |
| `PairBusy` | Another device is being connected. Wait a minute and try again. | Подключается другое устройство. Подождите минуту и попробуйте снова. | `pair.busy` |
| `PairDeviceLimit` | Too many devices are connected. Disconnect one in Devices and Permissions. | Подключено слишком много устройств. Отключите лишнее в «Устройствах и разрешениях». | `pair.deviceLimit` |
| `PairFailed` | Couldn't connect the device. Try again. | Не удалось подключить устройство. Попробуйте снова. | прочие коды, `:85`, `:128` |
| `PairCopyLink` | Copy Link (Debug) | Копировать ссылку (Debug) | только `#if DEBUG` |

## 6. Окно «Настройки» (новое, `SettingsForm`)

Объединяет то, что уже есть: автозапуск (`TrayContext.cs:98`, `197-247`), язык (сейчас только по Windows, `TrayText.cs:16`), проверку обновлений (пункт плана E5-4).

```
┌ Nexus PC — настройки ─────────────────────────────┐
│ Настройки                                         │
│                                                   │
│ ☑ Запускать Nexus PC при входе в Windows          │
│   Без этого iPhone не сможет запускать программы, │
│   блокировать ПК и показывать экран.              │
│                                                   │
│ Язык   [ Как в Windows        ▾ ]                 │
│        Как в Windows / English / Русский          │
│        Изменение применится после перезапуска.    │
│        [Перезапустить Nexus PC]  (если изменён)   │
│                                                   │
│ Версия 1.2.0                                      │
│ [Проверить обновления]   [статус проверки]        │
│                                                   │
│ Лицензии и сторонние компоненты ↗                 │
│                                        [Закрыть]  │
└───────────────────────────────────────────────────┘
```

- Автозапуск применяется сразу (как сейчас переключатель в меню). Ошибка записи в реестр → `SettingsAutostartFailed`.
- Язык хранится локально у пользователя (`%LOCALAPPDATA%\NexusPC`), читается при старте трея. Перезапуск — перезапуск только трея; служба и подключения iPhone не прерываются.
- **Проверка обновлений — НУЖНО РЕШЕНИЕ.** В продукте нет механизма обновлений (источника версии нет: `{DOWNLOAD_URL}` — заглушка). Варианты: (а) кнопка открывает `{DOWNLOAD_URL}` в браузере; (б) трей запрашивает номер последней версии с сайта (сетевой запрос — нужна оценка `security-auditor` и решение по домену). Рекомендация: (а) до появления домена; до решения строка «Проверить обновления» не реализуется (не показывается выключенной).
- «Лицензии и сторонние компоненты» открывает установленный файл третьих сторон (раздел 7.3) в программе просмотра по умолчанию.

| Ключ | en | ru |
|------|----|----|
| `SettingsTitle` | Nexus PC — Settings | Nexus PC — настройки |
| `SettingsHeading` | Settings | Настройки |
| `SettingsAutostart` | Start Nexus PC when I sign in to Windows | Запускать Nexus PC при входе в Windows |
| `SettingsAutostartHint` | Without it, iPhone can't start apps, lock the PC or see the screen. | Без этого iPhone не сможет запускать программы, блокировать ПК и показывать экран. |
| `SettingsAutostartFailed` | Couldn't change this setting. Try again. | Не удалось изменить настройку. Попробуйте снова. |
| `SettingsLanguage` | Language | Язык |
| `SettingsLanguageSystem` | Same as Windows | Как в Windows |
| `SettingsLanguageEn` | English | English |
| `SettingsLanguageRu` | Русский | Русский |
| `SettingsLanguageRestart` | The change applies after Nexus PC restarts. | Изменение применится после перезапуска Nexus PC. |
| `SettingsRestart` | Restart Nexus PC | Перезапустить Nexus PC |
| `SettingsVersion` | Version {0} | Версия {0} |
| `SettingsCheckUpdates` | Check for Updates | Проверить обновления |
| `SettingsLicenses` | Licenses and Third-Party Components | Лицензии и сторонние компоненты |
| `SettingsClose` | Close | Закрыть |

Названия языков в списке — на своём языке (English, Русский), независимо от текущего.

## 7. Окно «Программы и профили» (`AppsForm.cs`) — тексты

Структура не меняется (две вкладки). Только тексты и «Store» → «Microsoft Store».

| Ключ | en | ru | Замена |
|------|----|----|--------|
| `AppsTitle` | Nexus PC — Apps and Profiles | Nexus PC — программы и профили | `:28` |
| `AppsTab` | Apps | Программы | `:49` |
| `AppsColName` | Name | Название | `:50` |
| `AppsColType` | Type | Тип | `:51` |
| `AppsColClose` | Close from iPhone | Закрытие с iPhone | `:52` |
| `AppsHint` | iPhone can start only the apps in this list. You can add them only here, on the PC. | iPhone может запускать только программы из этого списка. Добавлять их можно только здесь, на ПК. | `:56` |
| `AppsFromStart` | From Start Menu… | Из меню «Пуск»… | `:59` |
| `AppsFromStore` | Microsoft Store Apps… | Программы Microsoft Store… | `:60` |
| `AppsFromExe` | Choose a Program… | Выбрать программу… | `:61`, без «.exe» в кнопке |
| `AppsRemove` | Remove | Удалить | `:62` |
| `AppsAllowClose` | Allow closing from iPhone | Разрешать закрывать с iPhone | `:18` |
| `AppsKindShortcut` | Shortcut | Ярлык | `:147` |
| `AppsKindStore` | Microsoft Store app | Программа Microsoft Store | `:148` |
| `AppsKindProgram` | Program | Программа | `:149` |
| `AppsCloseAllowed` | Allowed | Разрешено | `:151` |
| `AppsCloseNo` | No | Нет | `:151` |
| `AppsNoStoreApps` | No Microsoft Store apps found on this PC. | На этом ПК не найдены программы Microsoft Store. | `:241` |
| `AppsPickerStart` | Start Menu | Меню «Пуск» | `:218` |
| `AppsPickerStore` | Microsoft Store Apps | Программы Microsoft Store | `:244` |
| `AppsExeFilter` | Programs (*.exe)\|*.exe | Программы (*.exe)\|*.exe | `:255` |
| `AppsExeTitle` | Choose a Program | Выберите программу | `:255` |
| `AppsSearch` | Search… | Поиск… | `:291` |
| `AppsAdd` | Add | Добавить | `:300` |
| `ProfilesTab` | Profiles | Профили | `:83` |
| `ProfilesPresets` | Create Standard Profiles | Создать стандартные | `:85` |
| `ProfilesNew` | New Profile | Новый профиль | `:86` |
| `ProfilesSave` | Save | Сохранить | `:92` |
| `ProfilesDelete` | Delete Profile | Удалить профиль | `:94` |
| `ProfilesName` | Name | Название | `:97` |
| `ProfilesApps` | Apps in Profile | Программы профиля | `:98` |
| `ProfilesPresetsDone` | Created Gaming, Developer, Creator and Night. Check the apps you want in each. | Созданы Gaming, Developer, Creator, Night. Отметьте в каждом нужные программы. | `:124` |
| `ProfilesDeleted` | Profile deleted. | Профиль удалён. | `:133` |
| `ProfilesNameInvalid` | The name must be 1 to 64 characters. | Название — от 1 до 64 символов. | `:194` |
| `ProfilesTooManyApps` | A profile can have up to {0} apps. | В профиле может быть не больше {0} программ. | `:200` |
| `ProfilesSaved` | Saved. | Сохранено. | `:208` |

## 8. Установщик-мастер (`Nexus.Setup`)

Сейчас установщик — консольное окно с вопросом `[д/Н]` о сети (`Installer.cs:690`) и сообщениями с командами (`--datadir-*`, «Закройте окна PowerShell…», `Installer.cs:948`). Мастер — окно поверх той же логики `Installer`: шаги и тексты меняются, порядок установки и откаты (`InstallTransaction`) — нет.

### 8.1 Шаги

```
1 Приветствие → 2 Лицензии → 3 Что будет установлено → [установка] → 4 Сеть «Частная» → 5 Готово
```

```
┌ Установка Nexus PC ─────────────────────────────────────────┐
│ ●───○───○───○───○   Шаг 2 из 5: Лицензии                     │
│                                                             │
│ [содержимое шага]                                           │
│                                                             │
│                               [Назад]  [Далее]  [Отмена]    │
└─────────────────────────────────────────────────────────────┘
```

| Шаг | Содержимое | Кнопки |
|-----|-----------|--------|
| 1 Приветствие | `SetupWelcome` (новая установка) или `SetupWelcomeUpdate` с версиями (обновление: «Обновляю {old} → {new}…», `Installer.cs:166`) | Далее · Отмена |
| 2 Лицензии | лицензия Nexus PC (текст — по решению владельца «LICENSE», до него `{LICENSE_TEXT}`); список сторонних компонентов с лицензиями и ссылками на исходный код (7.3); флажок `SetupLicenseAccept` | Назад · Далее (выключена без флажка) · Отмена |
| 3 Что будет установлено | список компонентов (7.2), папка установки, «Windows запросит разрешение администратора» | Назад · Установить 🛡 · Отмена |
| — Установка | индикатор + текущий этап (`SetupStage*`); без «Отмена» после начала замены файлов (откат идёт сам) | — |
| 4 Сеть | только если есть сети «Общественная» (`CheckNetworks`, `Installer.cs:673`); для каждой — флажок «Это моя домашняя сеть — сделать частной», по умолчанию **снят**; иначе шаг пропускается | Далее |
| 5 Готово | `SetupDone` + «Дальше: на iPhone …»; флажок «Запустить Nexus PC» (по умолчанию отмечен, `--no-tray` снимает) | Готово |
| Ошибка | `SetupFailed*` (7.4) + «Открыть журнал» | Закрыть |

`--quiet` сохраняется: окна нет, лицензия считается принятой (тихая установка — ответственность администратора), сети не меняются (как сейчас, `Installer.cs:686-689`), код выхода — как в `docs/SETUP.md`. `--uninstall`, `--purge`, `--datadir-*` — без мастера (консоль/код выхода), тексты команд остаются в `docs/SETUP.md`.

Удаление: через «Параметры → Приложения» Windows (запуск `--uninstall`) — окно подтверждения `SetupUninstallConfirm` с флажком «Удалить также данные» (`--purge`).

### 8.2 Что будет установлено

| Ключ | en | ru |
|------|----|----|
| `SetupItemApp` | Nexus PC — background service and the N icon near the clock | Nexus PC — фоновая служба и значок N у часов |
| `SetupItemFirewall` | Windows Firewall rule: connections only from your home (private) network | Правило брандмауэра Windows: подключения только из домашней (частной) сети |
| `SetupItemOpenRgb` | OpenRGB — lighting control | OpenRGB — управление подсветкой |
| `SetupItemPawnIo` | PawnIO driver — CPU temperature and lighting of the motherboard and memory | Драйвер PawnIO — температура процессора, подсветка платы и памяти |
| `SetupItemStartMenu` | Start menu shortcut and start when you sign in to Windows | Ярлык в меню «Пуск» и запуск при входе в Windows |

### 8.3 Лицензии сторонних компонентов (S10)

Источник: `ARCHITECTURE.md`, раздел 9; `windows/ThirdParty/README.md`. Для GPL-компонентов — текст лицензии и ссылка на исходный код **точной** поставляемой версии (`{THIRD_PARTY_SOURCES_URL}` — заглушка, см. `README.md`). Тот же список устанавливается файлом и открывается из «Настроек».

| Компонент | Версия | Лицензия | Исходный код |
|-----------|--------|----------|--------------|
| OpenRGB | 1.1 | GPL-2.0 | `{THIRD_PARTY_SOURCES_URL}` (точная версия) |
| PawnIO setup | 2.2.0 | GPL-2.0 | `{THIRD_PARTY_SOURCES_URL}` (точная версия) |
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | ссылка на исходники версии |
| Зависимости LHM (HidSharp, DiskInfoToolkit, RAMSPDToolkit-NDD, BlackSharp.Core, System.Management, System.IO.Ports, Mono.Posix.NETStandard, System.CodeDom, System.IO.FileSystem.AccessControl) | по пакетам | MIT / Apache-2.0 / MPL | по пакетам |

Состав и обязательства определяет `security-auditor`, наличие в поставке проверяет `release-manager`; мастер только показывает.

### 8.4 Тексты мастера

| Ключ | en | ru |
|------|----|----|
| `SetupTitle` | Nexus PC Setup | Установка Nexus PC |
| `SetupStepOf` | Step {0} of {1}: {2} | Шаг {0} из {1}: {2} |
| `SetupWelcome` | Nexus PC lets you control this PC from iPhone on your home network. | Nexus PC позволяет управлять этим ПК с iPhone в домашней сети. |
| `SetupWelcomeUpdate` | Nexus PC {0} will be updated to {1}. Connected devices, automations and settings are kept. | Nexus PC {0} будет обновлён до {1}. Подключённые устройства, автоматизации и настройки сохранятся. |
| `SetupLicensesStep` | Licenses | Лицензии |
| `SetupLicenseAccept` | I accept the license terms | Я принимаю условия лицензии |
| `SetupThirdParty` | Nexus PC includes these components. Their licenses and links to the source code: | В Nexus PC входят эти компоненты. Их лицензии и ссылки на исходный код: |
| `SetupContentsStep` | What Will Be Installed | Что будет установлено |
| `SetupFolder` | Folder: {0} | Папка: {0} |
| `SetupAdminNote` | Windows will ask for administrator permission. | Windows запросит разрешение администратора. |
| `SetupInstall` | Install | Установить |
| `SetupInstalling` | Installing… | Установка… |
| `SetupStageFiles` | Checking files… | Проверка файлов… |
| `SetupStageService` | Setting up Nexus PC… | Настройка Nexus PC… |
| `SetupStagePawnIo` | Installing the PawnIO driver… | Установка драйвера PawnIO… |
| `SetupStageTray` | Starting Nexus PC… | Запуск Nexus PC… |
| `SetupNetworkStep` | Home Network | Домашняя сеть |
| `SetupNetworkBody` | Windows treats these networks as public. iPhone can't connect to the PC on them. | Windows считает эти сети общественными. В них iPhone не сможет подключиться к ПК. |
| `SetupNetworkItem` | {0} is my home network — make it private | {0} — моя домашняя сеть, сделать её частной |
| `SetupNetworkHint` | In cafés and other networks you don't trust, keep "Public": the PC stays closed to connections there. | В кафе и других чужих сетях оставляйте «Общественная» — там ПК закрыт для подключений. |
| `SetupNetworkFailed` | Couldn't change the network type. Change it in Windows Settings → Network & internet. | Не удалось изменить тип сети. Измените его в «Параметры Windows» → «Сеть и Интернет». |
| `SetupDoneStep` | Done | Готово |
| `SetupDone` | Nexus PC is installed. | Nexus PC установлен. |
| `SetupDoneNext` | Next: on iPhone, open Nexus PC and tap Connect PC. On this PC, click the N icon near the clock → Connect iPhone. | Дальше: на iPhone откройте Nexus PC и нажмите «Подключить ПК». На этом ПК нажмите значок N у часов → «Подключить iPhone». |
| `SetupLaunch` | Start Nexus PC | Запустить Nexus PC |
| `SetupBack` | Back | Назад |
| `SetupNext` | Next | Далее |
| `SetupCancel` | Cancel | Отмена |
| `SetupFinish` | Finish | Готово |
| `SetupClose` | Close | Закрыть |
| `SetupOpenLog` | Open Log | Открыть журнал |
| `SetupCancelConfirm` | Cancel the installation? Nothing will be changed on this PC. | Прервать установку? На этом ПК ничего не изменится. |
| `SetupUninstallConfirm` | Remove Nexus PC from this PC? iPhone will no longer be able to connect to it. | Удалить Nexus PC с этого ПК? iPhone больше не сможет к нему подключаться. |
| `SetupUninstallPurge` | Also delete connected devices, automations and history | Удалить также подключённые устройства, автоматизации и историю |
| `SetupWindowsOnly` | This installer works only on Windows. | Установщик работает только в Windows. |

### 8.5 Ошибки мастера (S9: без PowerShell и командной строки)

| Ключ | en | ru | Заменяет |
|------|----|----|----------|
| `SetupFailedRolledBack` | Installation didn't finish. Everything was returned to how it was. Restart the PC and run the installer again. | Установка не завершилась. Всё возвращено как было. Перезагрузите ПК и запустите установщик ещё раз. | `RolledBackText` |
| `SetupFailedRolledBackUpdate` | The update didn't finish. The previous version was restored and keeps working. Restart the PC and run the installer again. | Обновление не завершилось. Предыдущая версия восстановлена и работает. Перезагрузите ПК и запустите установщик ещё раз. | `RolledBackText` (обновление) |
| `SetupFailedFileBusy` | A Nexus PC file is in use by another program. Restart the PC and run the installer again. | Файл Nexus PC занят другой программой. Перезагрузите ПК и запустите установщик ещё раз. | `Installer.cs:948` («Закройте окна PowerShell…») |
| `SetupFailedDataFolder` | The Nexus PC data folder has wrong access settings, so installation was stopped to keep your data safe. See Help on the website: {SUPPORT_URL} | Доступ к папке данных Nexus PC настроен неправильно, поэтому установка остановлена, чтобы защитить данные. Инструкция на сайте: {SUPPORT_URL} | `Installer.cs:531-543` (команды `--datadir-*` остаются в `docs/SETUP.md`) |
| `SetupFailedRecovery` | Installation couldn't be finished or undone automatically. Don't delete the Nexus PC folder; see {SUPPORT_URL}. | Установку не удалось ни завершить, ни отменить автоматически. Не удаляйте папку Nexus PC; инструкция: {SUPPORT_URL}. | `RecoveryFailedText` |
| `SetupFailedUnsafePath` | Nexus PC can't be installed in this folder because its access settings aren't safe. | Nexus PC нельзя установить в эту папку: её права доступа небезопасны. | `UnsafeInstallPathText` |
| `SetupFailedPackage` | The installer file is damaged. Download it again: {DOWNLOAD_URL} | Файл установщика повреждён. Скачайте его заново: {DOWNLOAD_URL} | manifest/payload rejected |
| `SetupFailedGeneric` | Installation failed. Click Open Log and send the file to support if it repeats. | Установка не удалась. Если повторяется — нажмите «Открыть журнал» и пришлите файл в поддержку. | `Program.cs:49-51` |
| `SetupPawnIoSkipped` | The PawnIO driver wasn't installed. Nexus PC works, but CPU temperature and some lighting will be unavailable. | Драйвер PawnIO не установлен. Nexus PC работает, но температура процессора и часть подсветки будут недоступны. | `Installer.cs:376` (не фатально) |

## 9. Внешний вид и токены

Тема окна — существующая `Theme.cs` (тёмная, фиолетовый→голубой): цвета `Background`, `Surface`, `SurfaceHover`, `Text`, `Muted`, `Violet`, `Cyan`. Новых токенов не заводится. Контраст на `Background` (7,7,11): `Text` ≈ 17:1, `Muted` ≈ 7.7:1, `Violet` ≈ 5.2:1, `Cyan` ≈ 12:1 — все проходят AA для обычного текста. Текст на основной кнопке (градиент) — тёмный (10,8,20), как сейчас.

Режим высокой контрастности Windows (`SystemInformation.HighContrast`): `Theme.Apply` не красит окна и меню, используются системные цвета, градиенты и свечение отключаются.

Состояния кнопок (`Theme.PaintButton`): обычное, наведение (свечение), нажатое — **добавить** (сейчас не отличается от наведения: затемнение заливки на 15 %), выключенное (`Surface` + `Muted`), фокус с клавиатуры — **добавить** видимую рамку 2 px `Cyan` (сейчас фокус при `FlatStyle.Flat` и `BorderSize = 0` не виден). Загрузка — кнопка выключена, текст состояния рядом («Получаем код…», «Загрузка…»).

## 10. Доступность (Windows)

- Клавиатура: у каждого окна логичный `TabIndex` сверху вниз, слева направо; `AcceptButton` — основная кнопка, `CancelButton` — «Закрыть»/«Отмена»; клавиши доступа (`&`) у кнопок EN/RU без конфликтов внутри окна.
- Экранный диктор: `AccessibleName` у полей кода (`AccountForm._code`: «Код для входа: K 7 Q 2 M 9 X D» — посимвольно), у QR (`PairQrA11y`: en «QR code for connecting iPhone» / ru «QR-код для подключения iPhone»), у значка предупреждения (`ProblemA11y`: en «Problem» / ru «Проблема»). Изменение `_problem` и статуса окон объявляется (`AccessibleEvents.NameChange` / Live region через UIA).
- Масштаб: при 100–200 % DPI и «Увеличить текст» Windows до 225 % текст не обрезается. Фиксированные `Width/Height` у `Label` (`AccountForm.cs:177-180`, `DevicesForm.cs:32,35`) заменить на `TableLayoutPanel`/`FlowLayoutPanel` с `AutoSize`; английские строки длиннее русских не всегда — проверять оба языка.
- Цвет не единственный признак: проблемы — со значком; отмеченные пункты меню — с отметкой (сейчас голубая точка — достаточно).
- Уведомления-«шарики» исчезают через 3 с; всё важное из них продублировано в меню или окне (кто смотрит экран — в пункте меню и подсказке).

## 11. ЗАДАНИЯ

```
ЗАДАНИЕ W-ACC (черновик для E5-5): окно «Аккаунт» по решениям владельца
Исполнитель: windows-developer
Файлы: windows/Nexus.Tray/AccountForm.cs, ElevatedAccount.cs, TrayText.cs, Strings.resx, Strings.ru.resx; windows/Nexus.Core/Account/AccountLoginModel.cs (+ тесты)
Сделать: по docs/design/windows.md, раздел 3: (1) различить ошибку запуска повышенного процесса (не 1223, null, нет пути) и отказ службы: новый AccountProblem.ElevationFailed → ProblemElevationFailed; отказ UAC остаётся AdminDeclined; (2) опрос login.status только пока AccountForm открыт; разовая проверка при старте трея остаётся; при повторном открытии в Pending — login.status и показ кода; (3) состояние Unavailable показывает текст проблемы в основном тексте; (4) LinkLabel AccountDeleteLink → {ACCOUNT_URL} (константа-заглушка) в SignedIn; (5) тексты таблицы 3.3 (уточнения и новые ключи); (6) щит UAC на «Войти»/«Отвязать»; макет без фиксированных высот Label.
Не трогать: протокол службы, admin pipe, формат маркера; почту аккаунта не запрашивать и не показывать.
Критерии приёмки: тесты модели: 1223 → AdminDeclined, прочая Win32Exception → ElevationFailed, admin.required → AdminRequired; при закрытом окне нет вызовов login.status по таймеру; ни в одном состоянии нет почты; строки только из resx, EN и RU совпадают по набору ключей.
Тесты: dotnet test (Nexus.Core.Tests, Linux) + сборка Windows в CI.
Зависит от: E5-5
```

```
ЗАДАНИЕ W-TRAY (черновик для E5-5): меню трея, окна, Настройки, локализация
Исполнитель: windows-developer
Файлы: windows/Nexus.Tray/TrayContext.cs, TrayText.cs, Strings*.resx, PairingForm.cs, DevicesForm.cs, AppsForm.cs, Program.cs, Theme.cs, SettingsForm.cs (новый)
Сделать: по docs/design/windows.md, разделы 2, 4–7, 9–10: все литералы → resx (EN нейтральный, RU); порядок меню 2.1; статусы 2.2 без внутренних текстов AgentConnection (они — в tray.log); Настройки: автозапуск (перенос из меню), язык (Как в Windows / English / Русский, применяется после перезапуска трея), лицензии; «Проверить обновления» — не реализовывать до решения владельца; Devices: названия разрешений 4.2, без «(код N)» и result.Message, отказ UAC (1223) отдельно от прочих ошибок; Pairing: убрать текст про DevClient, «Копировать ссылку» только #if DEBUG, ошибки по коду; уведомление при отказе UAC в RunAdminCommand; видимый фокус и нажатое состояние кнопок, высокая контрастность.
Не трогать: протокол, Nexus.Agent, логику разрешений и сопряжения.
Критерии приёмки: в Nexus.Tray/*.cs нет пользовательских строк-литералов (кроме журнала и Debug); набор ключей Strings.resx = Strings.ru.resx; подсказка значка ≤ 63 символов на обоих языках; в UI нет слов «служба», «канал», GUID, PowerShell, кодов выхода; окна без обрезки текста при 200 % DPI.
Тесты: сборка Windows в CI; ручная проверка по чек-листу QA (EN/RU, 100/150/200 %, высокая контрастность, Narrator).
Зависит от: E5-5
```

```
ЗАДАНИЕ W-SETUP (черновик для E5-5): мастер установщика
Исполнитель: windows-developer
Файлы: windows/Nexus.Setup/** (новое окно мастера поверх Installer), ресурсы EN/RU; файл третьих сторон в поставке — вместе с release-manager
Сделать: по docs/design/windows.md, раздел 8: шаги Приветствие → Лицензии (принятие + сторонние компоненты с лицензиями и ссылками на исходники GPL) → Что будет установлено → установка → Сеть «Частная» (флажки, по умолчанию сняты, шаг только при общественных сетях) → Готово; ошибки 8.5 без команд PowerShell/--datadir; --quiet и прочие ключи без изменений поведения и кодов выхода.
Не трогать: InstallTransaction, порядок и откаты этапов, коды выхода (docs/SETUP.md).
Критерии приёмки: существующие тесты Setup проходят; --quiet не показывает окон и не меняет сети; ни один текст мастера не содержит команд; GPL-компоненты со ссылкой на исходники; лицензия Nexus PC — заглушка до решения владельца.
Тесты: dotnet test Setup-тестов; сборка установщика в CI.
Зависит от: E5-5; решение владельца «LICENSE»; {THIRD_PARTY_SOURCES_URL}
```

## 12. Нужно решение владельца

| Вопрос | Рекомендация |
|--------|--------------|
| Проверка обновлений: механизма нет | до домена — кнопка открывает `{DOWNLOAD_URL}`; до решения не реализовывать |
| Двойной щелчок по значку N | открывать «Устройства и разрешения» |
| Где размещать исходники GPL (`{THIRD_PARTY_SOURCES_URL}`) | на сайте рядом с `{DOWNLOAD_URL}`, архив точных версий |
| Текст лицензии Nexus PC в мастере | по решению «LICENSE» (`E5-PLAN.md`) |
