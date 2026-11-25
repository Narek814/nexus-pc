# Экраны iOS: разделы, состояния, Free/Pro

Задание E5-2. Опирается на `navigation.md` (вкладки, карта экранов), `foundations.md` (токены, компоненты, общие тексты), `error-texts.md` (тексты по кодам). Аккаунт, вход, My Devices, экран Pro — E5-3 (`ios-account.md`).

## 1. Цель и для кого

Владелец ПК и члены семьи с сопряжённым iPhone. Каждый из 14 разделов должен в любом состоянии объяснять, что происходит и что сделать: без пустых экранов, исчезающих кнопок и текстов с командами PowerShell. Pro-функции видны во Free и честно помечены.

## 2. Общие состояния раздела

Каждый раздел, которому нужен ПК, проходит одну и ту же проверку сверху вниз; первое сработавшее состояние заменяет содержимое:

| # | Условие | Что показываем | Компонент |
|---|---------|----------------|-----------|
| 1 | нет сопряжённого ПК | «ПК не подключён» + «Подключить ПК» | `EmptyState` (`empty.noPC.*`) |
| 2 | ПК не отвечает / нет Wi-Fi | «ПК не в сети» + «Повторить», «Включить ПК» (если есть WoL) | `OfflineCard` |
| 3 | подключение идёт | «Подключение…» | `LoadingState` |
| 4 | у iPhone нет разрешения раздела (нет в `capabilities` / `auth.permissionDenied`) | путь включения на ПК | `PermissionCard` |
| 5 | раздел требует трей, а он не запущен (`pc.status.userSession` ≠ `available` / `session.unavailable`) | «Приложение Nexus PC на ПК не запущено» | `TrayMissingCard` |
| 6 | функция Pro, плана нет (`features[]` без нужного id / `plan.required`) | «Доступно в Pro» | `ProCard` |
| 7 | первая загрузка данных | «Загрузка…» | `LoadingState` |
| 8 | ошибка запроса | текст по коду | `ErrorCard` (`error-texts.md`) |
| 9 | данных нет | пусто + действие создания | `EmptyState` |
| 10 | норма | содержимое | — |

Порядок 4 → 6 повторяет агент: `auth.permissionDenied` всегда раньше `plan.*` (`docs/API.md`, «Порядок проверок»). Если агент не отдаёт маркер `entitlements.v1`, состояние 6 не показывается никогда (до выпуска ключей всё работает как сейчас).

«Нет интернета» (iPhone без связи с сервером, но ПК в локальной сети доступен) **не** блокирует разделы: Free работает без интернета, Pro — пока действует токен на ПК. Интернет нужен только Account, экрану Pro и одобрению входа ПК (E5-3). В этих местах — `ErrorCard` с `account.offline` (раздел 6).

Переходы между состояниями — `anim.fade` (`foundations.md`, 5); при Reduce Motion — без анимации, мгновенная замена.

## 3. Free / Pro

### 3.1 Матрица (PROPOSED, COMMERCIAL_PLAN раздел 0 — не утверждена)

| Раздел | Free | Pro | `feature` | Как во Free |
|--------|------|-----|-----------|-------------|
| Home: статус, питание | да | да | — | — |
| Home: таймер выключения (`delaySeconds`) | **ждёт решения** | да | — | см. 3.3 |
| Apps | да | да | — | — |
| Profiles | просмотр превью — нет данных (`profiles.list` тоже Pro) | да | `profiles` | секция Profiles на Home и `ProfilesView` — `ProCard` |
| Screen: кадры JPEG | да | да | — | — |
| Screen: видео H.264 (`screen.video`) | **ждёт решения** | да | — | см. 3.3 |
| Touchpad, Keyboard | да | да | — | — |
| PC Monitor: текущие значения, история 10 мин | да | да | — | — |
| PC Monitor: история 1 ч и 24 ч | нет | да | `monitoring.extended` | сегменты «1 ч», «24 ч» с `ProBadge`; выбор открывает `ProCard` листом |
| RGB Lighting: статус устройств | да (`rgb.status`) | да | — | список устройств виден |
| RGB Lighting: применить цвет, профили подсветки | нет | да | `rgb.sync` | элементы управления выключены, над ними `ProCard` |
| Files | с лимитом | без лимита | `files.unlimited`; `plan.limit` — `feature: "files"` | работает; при `plan.limit` — `LimitNotice` |
| Files: буфер обмена (`clipboard.*`) | **ждёт решения** | да | — | см. 3.3 |
| Automations: список | только чтение | да | `automations` | список виден, переключатели и «+» выключены, `ProCard` сверху |
| Automations: создать, изменить, запустить, удалить | нет | да | `automations` | `ProCard` |
| History (`history.list`) | **ждёт решения** | да | — | см. 3.3 |
| Account, Settings, Help | да | да | — | — |

Правило автоматизаций: действия `profile` и `rgb`/`rgbProfile` требуют `profiles` и `rgb.sync`. В редакторе Pro-пользователя они доступны; если у плана нет одной из функций (например, в будущем раздельные планы) — пункт действия с `ProBadge`.

Права — по аккаунту **владельца ПК**. iPhone члена семьи видит Pro, если Pro у владельца; экран Pro (E5-3) для не-владельца показывает `pro.card.ownerHint` (`error-texts.md`, 3.2).

### 3.2 Окончание Pro

Состояния плана из `hello`/`account.refresh`: `entitlementState` = `active` / `grace` / `expired` / `revoked` / `none`.

| Состояние | Где | Что показываем |
|-----------|-----|----------------|
| `grace` | Account (E5-3); Home — нет | строка «Pro действует без связи с сервером до {DATE}» (`validUntil`, формат даты системы). Не пугаем: в разделах ничего не меняется |
| `expired`, `revoked` | Automations (корень) | баннер `AutomationsPausedBanner` (новый): «Автоматизации остановлены, не удалены. Продлите Pro, чтобы они снова работали.» + «Подробнее о Pro». Список правил виден (только чтение) |
| `expired`, `revoked` | остальные Pro-разделы | `ProCard` с `pro.expired.body` / `pro.revoked.body` |

Факт «остановлены» выводится из плана (`plan: free` при наличии правил в `automations.list`), новых полей не требуется.

### 3.3 Спорные пункты — оба варианта

Пока владелец не решил, исполнитель реализует **вариант А**, а переключение на Б — изменение одной записи в таблице соответствия «раздел → feature» (без новой вёрстки).

| Пункт | Вариант А: Free | Вариант Б: Pro | Рекомендация дизайна |
|-------|-----------------|----------------|----------------------|
| `screen.video` | видео во Free; переключатель качества «Быстро / Обычно / Чётко» доступен | во Free только кадры JPEG; строка «Плавное видео» с `ProBadge`, касание → `ProCard` (`pro.feature.screenVideo`) | А: экран — функция Free по COMMERCIAL_PLAN, JPEG заметно хуже |
| `clipboard.*` | кнопки «Отправить в буфер ПК» / «Взять из буфера ПК» в Files работают | кнопки с `ProBadge`, касание → `ProCard` (`pro.feature.clipboard`) | А: мелкая функция, во Free «базовое управление» |
| `history.list` | History открыт | строка History в Automations с `ProBadge`; внутри — `ProCard` (`pro.feature.history`) | А: история — это прозрачность действий с ПК, полезна для безопасности |
| таймер выключения (`delaySeconds`) | «Выключить через…» на Home доступно | только мгновенные действия; пункт «Через…» с `ProBadge` | А: Live Activity отсчёта — заметная функция Free |

## 4. Вкладка Home

### 4.1 Корень (`HomeView`, разделённый)

```
┌────────────────────────────────┐
│ Имя ПК              [● Готов]  │  ← StatusPill
│ Работает 3 ч · Windows 11      │
│ [TrayMissingCard, если нужно]  │
├────────────────────────────────┤
│ ПИТАНИЕ                        │  ← SectionHeader
│ [Сон] [Блок.] [Перезагр.] [Выкл]│ ← PowerTile ×4, сетка 2×2 при AX
│ ⏱ Выключение через 04:59 [Отменить]│ ← только при таймере
├────────────────────────────────┤
│ PROFILES               Все >   │  ← Pro: ProCard компактный
│ [Игры] [Работа] [Кино]         │
├────────────────────────────────┤
│ APPS                   Все >   │
│ [Steam] [Chrome] [Discord] …   │
└────────────────────────────────┘
```

| Состояние | Поведение |
|-----------|-----------|
| нет ПК | `EmptyState` «ПК не подключён» на весь корень |
| ПК не в сети | `StatusPill` «ПК не в сети»; `OfflineCard` внутри карточки статуса; плитки питания выключены; Profiles/Apps — из кэша серым с подписью `home.cached` |
| трей не запущен | `TrayMissingCard`; «Блокировка», Apps, Profiles выключены (их выполняет трей); сон/перезагрузка/выключение работают |
| нет разрешения `power` / `apps` | соответствующая секция заменяется компактным `PermissionCard` |
| Profiles во Free | компактный `ProCard` вместо ряда профилей |
| пусто | Apps: «Программы добавляются на ПК: значок N → Программы» (`apps.empty.body`); Profiles (Pro): «Создайте профиль» + «Создать» |

Действия: плитки питания (выключение и перезагрузка — `confirmationDialog`, уже есть), «Отменить» таймер, запуск профиля, запуск программы, «Все >».

### 4.2 Apps (`AppsView`)

Список программ из `apps.list`: имя, кнопка «Запустить», кнопка «Закрыть» только при `canClose`. Обновление — pull-to-refresh. Пусто — `apps.empty.*`. Добавление программ — только на ПК (текст, без кнопки).

### 4.3 Profiles (`ProfilesView`, Pro)

Список профилей: имя, состав (имена программ), «Запустить»; редактирование, удаление (свайп + `confirmationDialog`), «+». В редакторе выбор программ только из `apps.list` (`profiles.invalid` невозможен из UI). Во Free — `ProCard` (`pro.feature.profiles`).

## 5. Вкладка Control

Корень `ControlHomeView` — три `NavRow`: Screen, Touchpad, Keyboard. Значок `lock.fill` у строки без разрешения (`screen` / `input`). Значок Pro не нужен (вариант А).

### 5.1 Screen (`ScreenView`)

Состав (существующий): картинка экрана, к/с и задержка, качество, полноэкранный режим, `ScreenKeysRow`. Состояния: запрос на ПК — `screen.waiting` «Подтвердите показ экрана на ПК»; `screen.denied` / `screen.blocked` — `ErrorCard`; без `input` экран только смотрится, над картинкой строка `screen.viewOnly` + ссылка на `PermissionCard`. Переход на кадры при `screen.videoUnavailable` — молча, в «Диагностике».

### 5.2 Touchpad (`TouchpadView`)

Поле тачпада (касание = клик, два пальца = прокрутка, долгое касание = перетаскивание — как сейчас), кнопки мыши. Ввод текста переезжает в Keyboard; в тачпаде остаётся кнопка-переход «Клавиатура». `input.blocked` — `Toast`.

### 5.3 Keyboard (`KeyboardView`, новый из существующего кода)

Поле ввода текста + «Отправить» (`input.text`), `ScreenKeysRow` (особые клавиши и сочетания из списка `input.key`), медиа-клавиши (`input.media`). Подсказка `keyboard.ctrlAltDel` — что Ctrl+Alt+Del недоступен (Windows не разрешает). Набранный текст после отправки очищается.

## 6. Вкладка PC

Корень `PCHomeView` — `NavRow`: PC Monitor, RGB Lighting (`ProBadge` во Free), Files.

### 6.1 PC Monitor (`MonitoringView`)

`MetricGauge` CPU, память, GPU, температуры, диски, сеть; график истории с сегментами «10 мин / 1 ч / 24 ч» (1 ч, 24 ч — Pro). Значение `null` → «—» и причина из `unavailable` в `accessibilityHint` и по касанию (например, датчик температуры недоступен). Текст причины берётся с ПК (своих кодов причин нет — см. «Нужно от протокола»). История после перезапуска ПК начинается заново — подпись `monitor.historyReset` под пустым графиком.

### 6.2 RGB Lighting (`RgbView`)

Во Free: список устройств из `rgb.status` + `ProCard` (`pro.feature.rgb`) над выключенными элементами. В Pro: цвет, яркость, эффект, профили подсветки. `available: false` — `ErrorCard` `error.rgb.unavailable` (или `rgb.busy` при `conflict`) с пояснением, что нужен OpenRGB на ПК. Пусто (OpenRGB запущен, устройств нет) — `rgb.empty.body`.

### 6.3 Files (`FilesView`)

Список файлов папки «Загрузки\Nexus PC», «Отправить на ПК» (системный выбор файла/фото), «Скачать» (сохранение через системный лист), прогресс передачи в строке. Буфер обмена — две кнопки (вариант А). `plan.limit` — `LimitNotice` над списком, передачи начатые до лимита не прерываются интерфейсом. Пусто — `files.empty.body`. Без `clipboard` при наличии `files` — кнопки буфера заменяет компактный `PermissionCard`.

## 7. Вкладка Automations

Корень `AutomationsView`: сегмент «Автоматизации | История», «+» в навигации.

- **Список**: строка — имя, условие («Каждый день в 08:00»), переключатель «Вкл.», последнее выполнение. Свайп: «Запустить», «Удалить». Выключение/перезагрузка в правиле — `confirmationDialog` при сохранении и ручном запуске.
- **Пусто** (Pro): `automations.empty.*` + «Создать».
- **Free**: `ProCard` сверху, список только чтение, переключатели выключены (не скрыты).
- **Pro закончился**: `AutomationsPausedBanner` (3.2).
- **Правило другого iPhone** не показывается агентом — пояснение не нужно; `automations.notFound` — `Toast`.
- **Editor** (`AutomationEditor`): «Когда» (время и дни / показатель выше значения / запуск ПК / вход в Windows), «Только с … до …», «Что сделать» (профиль, программа, подсветка, блокировка, сон, выключение, перезагрузка, пауза). Без разрешений на действие — пункт с `lock.fill` и `PermissionCard` листом.
- **History** (`HistoryView`): записи по дням — время, устройство, действие, итог. Пусто — `history.empty.body`.

## 8. Вкладка More

Корень `MoreView`: `NavRow` Account, Settings, Help & Support; в DEBUG-сборке — Developer.

### 8.1 Account

Экраны — E5-3. Здесь только строка в `MoreView`: при отсутствии входа подпись «Войдите снова» (`more.account.signInAgain`).

### 8.2 Settings (`SettingsView`, новый)

```
┌──────────────────────────────┐
│ Settings                     │
│ ЯЗЫК                         │
│ ○ Как в системе  ○ English  ○ Русский │
│ Виджеты, Siri и системные запросы — на языке системы. │
│ [Системные настройки языка]  │  ← открывает настройки приложения в iOS
│ О ПРИЛОЖЕНИИ                 │
│ Версия 1.0 (42)              │
│ Лицензии                     │  ← если в iOS есть сторонние компоненты; иначе строки нет
└──────────────────────────────┘
```

Других настроек в E5-2 не добавляем (новых функций нет).

### 8.3 Help & Support (`HelpView`, новый)

- Статьи-подсказки (статический текст в приложении): «Как подключить ПК», «Как включить разрешения на ПК», «ПК не в сети», «Что даёт Pro».
- «Связаться с поддержкой» → `{SUPPORT_URL}` (Safari).
- **Диагностика** (`DiagnosticsView`, новый, есть в релизе): версия приложения; состояние подключения; имя ПК; разрешения iPhone на этом ПК (человеческими названиями); план и `entitlementState` человеческими словами; режим экрана (видео / кадры); последние 50 ошибок — время, раздел, код, текст ПК. «Экспорт журнала» — системный лист (`ShareLink`), текстовый файл без ключей, токенов, адресов, имён файлов и набранного текста. Коды ошибок видны **только** здесь.

### 8.4 Developer (только DEBUG)

`DeveloperView` существует только в `#if DEBUG`: в Release не компилируется, строки в `MoreView` нет, текстов в `Localizable.xcstrings` для него не заводится (только EN-литералы под `#if DEBUG`). В релизе для поддержки служит «Диагностика» — скрытых жестов и меню нет (App Review 2.3.1).

## 9. Компоненты (новые в E5-2)

| Компонент | Назначение | Устройство | Состояния |
|-----------|-----------|------------|-----------|
| `AutomationsPausedBanner` | Pro закончился, правила остановлены | `GlassCard` с рамкой `state.warning`, `pause.circle.fill`, заголовок + текст + `SecondaryButton` «Подробнее о Pro» | обычное; нет интернета (кнопка выключена, `pro.card.offline`) |
| `ProSegment` | сегмент/строка Pro-варианта (история 1 ч/24 ч, видео) | обычный сегмент + `ProBadge`; выбор не переключает, а открывает `ProCard` листом | обычное; выбрано невозможно |
| `TransferRow` | строка передачи файла | имя, `ProgressView`, «Отмена» / «Повторить» | идёт; пауза (обрыв); ошибка (Inline-текст); готово |

Остальное — из `foundations.md` 6.

## 10. Тексты EN/RU

| Ключ | en | ru | Где |
|------|----|----|-----|
| `home.power` | Power | Питание | Home |
| `home.cached` | Last known — PC is offline | Последние данные — ПК не в сети | Home |
| `home.timer` | Shuts down in %@ | Выключение через %@ | Home |
| `home.timer.cancel` | Cancel | Отменить | Home |
| `home.shutdownIn` | Shut Down In… | Выключить через… | Home (меню плитки) |
| `apps.launch` | Open | Запустить | Apps |
| `apps.close` | Close | Закрыть | Apps |
| `apps.empty.title` | No Apps Yet | Программ пока нет | Apps |
| `apps.empty.body` | Apps are added on the PC: N icon → Apps. | Программы добавляются на ПК: значок N → Программы. | Apps |
| `profiles.run` | Run | Запустить | Profiles |
| `profiles.empty.title` | No Profiles | Профилей нет | Profiles |
| `profiles.empty.body` | A profile opens several apps at once. | Профиль запускает несколько программ сразу. | Profiles |
| `profiles.create` | Create Profile | Создать профиль | Profiles |
| `profiles.delete.confirm` | Delete profile “%@”? | Удалить профиль «%@»? | Profiles |
| `screen.waiting` | Allow screen sharing on the PC. | Разрешите показ экрана на ПК. | Screen |
| `screen.viewOnly` | View only. Turn on Mouse and Keyboard on the PC to control it. | Только просмотр. Чтобы управлять, включите на ПК «Мышь и клавиатура». | Screen |
| `screen.quality.fast` | Fast | Быстро | Screen |
| `screen.quality.normal` | Normal | Обычно | Screen |
| `screen.quality.sharp` | Sharp | Чётко | Screen |
| `touchpad.openKeyboard` | Keyboard | Клавиатура | Touchpad |
| `keyboard.placeholder` | Type text for the PC | Текст для ПК | Keyboard |
| `keyboard.send` | Send | Отправить | Keyboard |
| `keyboard.ctrlAltDel` | Ctrl+Alt+Del can't be sent: Windows doesn't allow it. | Ctrl+Alt+Del отправить нельзя: Windows этого не разрешает. | Keyboard |
| `monitor.range.10m` | 10 min | 10 мин | PC Monitor |
| `monitor.range.1h` | 1 h | 1 ч | PC Monitor |
| `monitor.range.24h` | 24 h | 24 ч | PC Monitor |
| `monitor.unavailable` | Not available | Недоступно | PC Monitor |
| `monitor.historyReset` | History starts again after the PC restarts. | После перезапуска ПК история начинается заново. | PC Monitor |
| `rgb.empty.body` | OpenRGB is running but found no devices. | OpenRGB запущен, но не нашёл устройств. | RGB |
| `files.send` | Send to PC | Отправить на ПК | Files |
| `files.download` | Save to iPhone | Сохранить на iPhone | Files |
| `files.empty.title` | No Files | Файлов нет | Files |
| `files.empty.body` | Files in the Downloads\Nexus PC folder on the PC appear here. | Здесь появляются файлы из папки «Загрузки\Nexus PC» на ПК. | Files |
| `clipboard.send` | Send to PC Clipboard | В буфер обмена ПК | Files |
| `clipboard.get` | Paste from PC Clipboard | Из буфера обмена ПК | Files |
| `automations.segment.list` | Automations | Автоматизации | Automations |
| `automations.segment.history` | History | История | Automations |
| `automations.run` | Run Now | Запустить | Automations |
| `automations.enabled` | On | Вкл. | Automations |
| `automations.empty.title` | No Automations | Автоматизаций нет | Automations |
| `automations.empty.body` | Automations run on the PC by themselves, even when iPhone is away. | Автоматизации выполняются на ПК сами, даже без iPhone рядом. | Automations |
| `automations.create` | Create Automation | Создать автоматизацию | Automations |
| `automations.paused.title` | Automations Paused | Автоматизации остановлены | `AutomationsPausedBanner` |
| `automations.paused.body` | They're stopped, not deleted. Renew Pro to run them again. | Они остановлены, а не удалены. Продлите Pro, чтобы они снова работали. | `AutomationsPausedBanner` |
| `history.empty.body` | Actions from iPhone and automation runs will appear here. | Здесь появятся действия с iPhone и запуски автоматизаций. | History |
| `pro.feature.profiles` | Open several apps with one tap. | Запуск нескольких программ одним касанием. | `ProCard` |
| `pro.feature.rgb` | Control lighting from iPhone. | Управление подсветкой с iPhone. | `ProCard` |
| `pro.feature.automations` | Let the PC do things on schedule. | ПК сам выполняет действия по расписанию. | `ProCard` |
| `pro.feature.monitoringExtended` | See up to 24 hours of history. | История показателей за сутки. | `ProCard` |
| `pro.feature.files` | Transfer files without limits. | Передача файлов без ограничений. | `ProCard`, `LimitNotice` |
| `pro.feature.screenVideo` | Smooth video of the PC screen. | Плавное видео экрана ПК. | вариант Б |
| `pro.feature.clipboard` | Share the clipboard with the PC. | Общий буфер обмена с ПК. | вариант Б |
| `pro.feature.history` | See what happened on the PC. | Что происходило на ПК. | вариант Б |
| `pro.feature.timer` | Schedule a shutdown with a countdown. | Выключение по таймеру с отсчётом. | вариант Б |
| `pro.grace` | Pro works offline until %@. | Pro работает без связи с сервером до %@. | Account |
| `account.offline` | No internet connection. Free features on your PC keep working. | Нет подключения к интернету. Бесплатные функции на ПК продолжают работать. | Account, Pro |
| `more.account.signInAgain` | Sign In Again | Войдите снова | More |
| `settings.language` | Language | Язык | Settings |
| `settings.language.system` | System Default | Как в системе | Settings |
| `settings.language.note` | Widgets, Siri and system prompts use the system language. | Виджеты, Siri и системные запросы — на языке системы. | Settings |
| `settings.language.openSystem` | System Language Settings | Системные настройки языка | Settings |
| `settings.about` | About | О приложении | Settings |
| `settings.version` | Version %@ | Версия %@ | Settings |
| `settings.licenses` | Licenses | Лицензии | Settings |
| `help.connect` | How to Connect a PC | Как подключить ПК | Help |
| `help.permissions` | How to Turn On Permissions | Как включить разрешения | Help |
| `help.offline` | PC Is Offline | ПК не в сети | Help |
| `help.pro` | What Pro Includes | Что даёт Pro | Help |
| `help.contact` | Contact Support | Связаться с поддержкой | Help |
| `diag.export` | Export Log | Экспорт журнала | Diagnostics |
| `diag.export.note` | The log has no keys, passwords, file names or typed text. | В журнале нет ключей, паролей, имён файлов и набранного текста. | Diagnostics |
| `diag.recentErrors` | Recent Errors | Последние ошибки | Diagnostics |
| `diag.screen.jpegMode` | Screen: frames mode (no video encoder on the PC) | Экран: режим кадров (на ПК нет видеокодировщика) | Diagnostics |
| `diag.plan.free` | Free | Бесплатный | Diagnostics |
| `diag.plan.pro` | Pro | Pro | Diagnostics |
| `diag.plan.grace` | Pro, offline grace period | Pro, работа без связи | Diagnostics |
| `diag.plan.ended` | Pro ended | Pro закончился | Diagnostics |

Тексты ошибок — `error-texts.md`.

## 11. Доступность

- VoiceOver-подписи:
  - `StatusPill` — «ПК, имя, готов» (одним элементом, `.combine`);
  - `PowerTile` — название действия, `accessibilityHint` «Попросит подтверждение» для выключения и перезагрузки;
  - таймер — «Выключение через 4 минуты 59 секунд», обновление объявляется не чаще раза в минуту;
  - `NavRow` с `ProBadge` — «RGB Lighting, требуется Pro, кнопка»; с `lock.fill` — «…, нужно разрешение на ПК»;
  - `ProSegment` — «24 часа, требуется Pro»;
  - тачпад — `accessibilityLabel` «Тачпад», `accessibilityHint` «Касание — щелчок, два пальца — прокрутка»; для VoiceOver — `accessibilityActions` «Щелчок», «Правый щелчок», «Прокрутить вверх/вниз» (жесты VoiceOver перехватывает);
  - экран ПК — изображение «Экран ПК», управление касанием недоступно в VoiceOver — подсказка ведёт к Touchpad/Keyboard;
  - `MetricGauge` — «CPU: 34 %», нет данных — «CPU: недоступно»;
  - `TransferRow` — «photo.jpg, отправка, 40 %», `updatesFrequently`.
- Порядок фокуса: сверху вниз, как на схемах; карточка состояния (Offline/Permission/Pro) идёт первой после заголовка.
- Dynamic Type до AX5: сетка плиток питания 4 в ряд → 2×2 → 1 столбец; строки списков переходят в вертикальную компоновку (`ViewThatFits`); сегменты истории при AX — `Picker` меню. Обрезки текста нет, всё в `ScrollView`.
- Контраст: `ProBadge`, `LimitNotice`, баннер — пары токенов из `foundations.md` 2 (AA проверен там).
- Области касания ≥ 44×44 pt, включая «Отменить» таймер, `ProBadge`-сегменты и кнопки `ScreenKeysRow`.
- Reduce Motion: смена состояний без сдвигов; прогресс передачи без пульсации.

## 12. Нужно от протокола

Данных хватает для всех экранов; ниже — уточнения, без придуманных полей. Решения — E5-5 (`code-architect`).

1. `auth.confirmationRequired` упоминается в `docs/API.md` (раздел «Автоматизации», `automations.run`), но отсутствует в таблице кодов; рядом есть `confirm.required`. Нужно уточнить, один это код или два. В дизайне оба показываются одинаково.
2. `admin.required` из задания нет в `docs/API.md`. Если он вводится (E5-0, канал трея), нужно определить пространство (трей/IPC) — на iPhone он не ожидается.
3. Версия агента ПК для «Диагностики»: в схемах ответа поля версии не найдено. Если её нет — строка «Версия на ПК» в «Диагностике» не показывается.
4. Причины `unavailable` в `metrics.snapshot` и `reason` в `rgb.status` — свободный текст ПК (на русском). Для EN-интерфейса нужны коды причин; до этого текст ПК показывается как есть (запасной путь).
5. Спорные пункты 3.3 — после решения владельца нужны идентификаторы `feature` для выбранных Pro-вариантов (сейчас в списке `features[]` их нет).

## 13. ЗАДАНИЯ

```
ЗАДАНИЕ E6-SCREENS (черновик для E5-5): состояния разделов, Free/Pro, тексты ошибок
Исполнитель: ios-developer
Файлы: ios/NexusPC/Views/** (HomeView, AppsView, ProfilesView, ControlHomeView, ScreenView, TouchpadView, KeyboardView, PCHomeView, MonitoringView, RgbView, FilesView, AutomationsView, MoreView, новые SettingsView, HelpView, DiagnosticsView, DesignSystem/*); ios/NexusPC/Resources/Localizable.xcstrings
Сделать: по docs/design/ios-screens.md разделы 2–11 и docs/design/error-texts.md: единая проверка состояний раздела (таблица раздела 2); матрица Free/Pro раздела 3.1 как одна таблица «раздел → feature», спорные пункты — вариант А; AutomationsPausedBanner; Settings, Help, Diagnostics; DeveloperView только под #if DEBUG; выбор текста ошибки по error.code (таблица error-texts.md), текст ПК — только запасной; все ключи раздела 10 и error-texts.md в EN и RU.
Не трогать: протокол, NexusKit (кроме чтения plan/features/entitlementState из hello, если ещё не читаются — согласовать с E5-5), логику питания и ввода.
Критерии приёмки: ни одного текста с PowerShell, GUID, кодом ошибки вне Diagnostics; каждый код из error-texts.md отображается своим ключом; Pro-раздел во Free виден с ProCard; plan.limit показывает LimitNotice без чисел; после окончания Pro автоматизации видны с баннером «остановлены, не удалены»; в Release нет DeveloperView (проверка сборкой Release); AX5 без обрезки на iPhone SE.
Тесты: сборка iOS Debug и Release в CI; модульный тест соответствия «код → ключ» по списку error-texts.md; ручная проверка состояний по чек-листу QA.
Зависит от: E5-5, E6-NAV
```
