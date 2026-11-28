# Навигация iOS и первый запуск

Задание E5-1. Связанные документы: `foundations.md` (компоненты, тексты), `README.md` (заглушки).

## 1. Цель и для кого

Владелец ПК с Windows, который хочет управлять им с iPhone в домашней сети. Сейчас всё приложение — один длинный `HomeView` со списком переходов, появляющихся и исчезающих в зависимости от разрешений (`HomeView.swift:30-104`). Цель: предсказуемая структура из 5 вкладок, где каждый из 14 разделов имеет одно постоянное место, а недоступный раздел не исчезает, а объясняет, как его включить.

## 2. Вкладки

`TabView`, 5 вкладок, порядок фиксированный. Иконки — SF Symbols, у каждой вкладки текстовая подпись (не только иконка).

```
┌───────────────────────────────────────────────┐
│                  (контент)                    │
├─────────┬─────────┬─────────┬────────┬────────┤
│  house  │ hand.   │ desktop │ gear-  │ ellip- │
│         │ point.. │ computer│ shape.2│ sis    │
│  Home   │ Control │   PC    │ Autom. │  More  │
└─────────┴─────────┴─────────┴────────┴────────┘
```

| Вкладка | Ключ | SF Symbol | Разделы ТЗ (14) |
|---------|------|-----------|-----------------|
| Home | `tab.home` | `house.fill` | Home (статус, питание, быстрые действия), Apps, Profiles |
| Control | `tab.control` | `hand.point.up.left.fill` | Screen, Touchpad, Keyboard |
| PC | `tab.pc` | `desktopcomputer` | PC Monitor, RGB Lighting, Files |
| Automations | `tab.automations` | `gearshape.2.fill` | Automations, History |
| More | `tab.more` | `ellipsis.circle.fill` | Account, Settings, Help & Support (+ My Devices как часть Account) |

Итого 14 разделов: Home, Apps, Profiles, Screen, Touchpad, Keyboard, PC Monitor, RGB Lighting, Files, Automations, History, Account, Settings, Help & Support. «My Devices» — подраздел Account (список ПК аккаунта, E5-3), а не 15-й раздел.

Правила:
- У каждой вкладки свой `NavigationStack`; повторное нажатие на активную вкладку возвращает к корню (поведение системы).
- Вкладки видны всегда, даже если ПК не подключён: тогда корень вкладки показывает состояние «ПК не подключён» (`foundations.md`, компонент `EmptyState`) с кнопкой «Подключить ПК».
- Раздел без разрешения на ПК показывает карточку `PermissionCard` («Включите на ПК: значок N → Устройства и разрешения → …»), а не скрывается. Это заменяет текущие `if controller.can(...)` в `HomeView.swift:34-76`.
- Pro-раздел во Free виден и показывает `ProCard` (без цен). Окончательная матрица Free/Pro — E5-2.
- Корни вкладок Control и PC — список карточек-переходов (`NavRow`), не сразу один экран: так все разделы вкладки видны и доступны VoiceOver.
- Deep link `nexuspc://pair…` (`HomeView.swift:131`) открывает лист сопряжения поверх любой вкладки.

## 3. Карта экранов

Легенда: **оставить** — экран используется как есть (перевод текстов и токенов в E6); **разделить** — часть кода выносится в отдельный View; **новое** — экрана нет в коде.

### Существующие файлы → место в новой структуре

| Файл / View | Что сейчас | Куда | Действие |
|-------------|-----------|------|----------|
| `App/NexusPCApp.swift` · `NexusPCApp` | корень, сразу `HomeView` | корень: первый запуск → `RootTabView` | изменить (новый `RootTabView`) |
| `Views/HomeView.swift` · `HomeView` | шапка, онбординг, статус, переходы, питание, профили, приложения, «Забыть ПК» | Home → корень | разделить (см. ниже) |
| `HomeView` · `header` | «NEXUS PC / Command Center» | Home → заголовок | изменить: заголовок вкладки «Home»/имя ПК, «Command Center» убрать |
| `HomeView` · `onboarding` | карточка «ПК не подключён» | первый запуск, шаг 4, и `EmptyState` вкладок | перенести |
| `HomeView` · `statusCard`, `StatusPill` | статус ПК, «Tray не запущен», «Включить ПК» (WoL) | Home → карточка статуса | оставить, тексты переписать |
| `HomeView` · `powerGrid`, `PowerTile`, `confirmationDialog`, `pendingPower` | питание, таймер выключения | Home → «Питание» | оставить |
| `HomeView` · `profilesSection` | профили | Home → «Profiles» (превью 3 шт.) → `ProfilesView` | разделить: новый `ProfilesView` |
| `HomeView` · `appsSection` | программы | Home → «Apps» (превью) → `AppsView` | разделить: новый `AppsView` |
| `HomeView` · отпечаток, «Обновить адрес ПК по QR-коду», `forgetButton` | обслуживание сопряжения | More → Account → My Devices → карточка ПК (E5-3) | перенести, с Home убрать |
| `HomeView` · `monitoringLink`, `homeLink`, `NavigationLink`-карточки | переходы ко всем разделам | убрать; разделы живут во вкладках | удалить переходы (не экраны) |
| `HomeView` · `SectionTitle` | заголовок секции | компонент `SectionHeader` | перенести в DesignSystem |
| `HomeView` · `banner` | всплывающая плашка | компонент `Toast` | перенести в DesignSystem |
| `Views/PairingFlowView.swift` · `PairingFlowView`, `StatusCard` | сопряжение: камера, вставка ссылки, код сверки | первый запуск шаг 4; More → My Devices → «Добавить ПК»; deep link | оставить |
| `Views/QRScannerView.swift` | камера QR | внутри `PairingFlowView` | оставить |
| `Views/ScreenView.swift` · `ScreenView`, `ScreenKeysRow` | экран ПК, полноэкранный режим, клавиатура поверх экрана | Control → Screen | оставить |
| `Views/TouchpadView.swift` · `TouchpadView` | тачпад + ввод текста | Control → Touchpad | оставить; ввод текста — см. Keyboard |
| (код клавиатуры из `TouchpadView`/`ScreenView` — поле ввода, `ScreenKeysRow`) | клавиатура только внутри тачпада и экрана | Control → Keyboard → `KeyboardView` | новое из существующего кода: экран = поле ввода текста + `ScreenKeysRow`; без новых команд |
| `Views/MonitoringView.swift` · `MonitoringView`, `RingGauge` | мониторинг | PC → PC Monitor | оставить; `RingGauge` = компонент `MetricGauge` |
| `Views/RgbView.swift` · `RgbView` (+ редактор профиля подсветки) | подсветка | PC → RGB Lighting | оставить |
| `Views/FilesView.swift` · `FilesView` | файлы и буфер обмена | PC → Files | оставить (буфер обмена остаётся внутри Files, отдельного раздела нет) |
| `Views/AutomationsView.swift` · `AutomationsView` | список автоматизаций | Automations → корень | оставить |
| `AutomationsView.swift` · `AutomationEditor` | создание/правка | Automations → правка | оставить |
| `AutomationsView.swift` · `HistoryView` | история | Automations → History | оставить (переход из корня вкладки, сегмент или строка «History») |
| `Views/DesignSystem/Theme.swift` · `Theme`, `GlassCard`, `NeonButton` | токены и 2 компонента | DesignSystem | изменить по `foundations.md` |
| `NexusWidgets/NexusWidgets.swift` · `QuickActionsWidget`, `QuickActionsView` | виджет быстрых действий | вне приложения | оставить; тексты — E6 |
| `NexusWidgets.swift` · `PowerCountdownLiveActivity` | Live Activity / Dynamic Island отсчёта выключения | вне приложения | оставить (единственная уместная Live Activity; передача файла — опционально, не в E5-1) |
| `Siri/PCIntents.swift` | Siri и Shortcuts | вне приложения | оставить |

### Новые экраны

| Экран | Вкладка | Назначение | Подробно |
|-------|---------|-----------|----------|
| `RootTabView` | — | `TabView` из 5 вкладок | здесь |
| `OnboardingFlow` | — | первый запуск, 4 шага | раздел 4 |
| `ProfilesView` | Home | все профили (из `profilesSection`) | E5-2 |
| `AppsView` | Home | все программы (из `appsSection`) | E5-2 |
| `ControlHomeView` | Control | корень: Screen, Touchpad, Keyboard | E5-2 |
| `KeyboardView` | Control | ввод текста и особые клавиши (существующий код) | E5-2 |
| `PCHomeView` | PC | корень: PC Monitor, RGB Lighting, Files | E5-2 |
| `MoreView` | More | корень: Account, Settings, Help & Support | E5-2 |
| `AccountView`, `MyDevicesView`, вход/регистрация | More | аккаунт и устройства | E5-3 |
| `SettingsView` | More | язык (Как в системе / English / Русский), «Системные настройки языка» и др. | E5-2 |
| `HelpView` (+ «Диагностика») | More | помощь; «Диагностика» — открытый раздел, экспорт журнала без секретов | E5-2 |
| `DeveloperView` | More | только `#if DEBUG`, в релизе не компилируется | E5-2 |

Каждый существующий View из `ios/NexusPC/Views/` есть в карте: `HomeView`, `SectionTitle`, `StatusPill`, `PowerTile`, `PairingFlowView`, `StatusCard`, `QRScannerView`, `ScreenView`, `ScreenKeysRow`, `TouchpadView`, `MonitoringView`, `RingGauge`, `RgbView`, `FilesView`, `AutomationsView`, `AutomationEditor`, `HistoryView`, `Theme`/`GlassCard`/`NeonButton`.

### Схемы корней вкладок

```
Home                          Control                    PC
┌──────────────────────┐      ┌────────────────────┐     ┌────────────────────┐
│ Имя ПК    [● Готов]  │      │ Control            │     │ PC                 │
│ Работает 3 ч · Win11 │      │ ┌────────────────┐ │     │ ┌────────────────┐ │
│ ┌──────────────────┐ │      │ │▣ Screen       >│ │     │ │◔ PC Monitor   >│ │
│ │ Питание          │ │      │ ├────────────────┤ │     │ ├────────────────┤ │
│ │ [Сон][Блок][Перез│ │      │ │☝ Touchpad     >│ │     │ │✦ RGB Lighting >│ │
│ │ [Выкл]           │ │      │ ├────────────────┤ │     │ ├────────────────┤ │
│ │ ⏱ Выкл. через… [Отм]│    │ │⌨ Keyboard     >│ │     │ │⇅ Files        >│ │
│ └──────────────────┘ │      │ └────────────────┘ │     │ └────────────────┘ │
│ PROFILES        Все >│      │ (строка без права →│     │ (Pro во Free →     │
│ [Игры] [Работа] [..] │      │  PermissionCard    │     │  значок «Pro»)     │
│ APPS            Все >│      │  внутри раздела)   │     │                    │
│ [Steam] [Chrome] […] │      └────────────────────┘     └────────────────────┘
└──────────────────────┘
Automations                   More
┌──────────────────────┐      ┌────────────────────┐
│ Automations      [+] │      │ More               │
│ [Список | History]   │      │ Account          > │  ← My Devices внутри
│ ┌──────────────────┐ │      │ Settings         > │
│ │ Утро · 08:00 [on]│ │      │ Help & Support   > │  ← Диагностика внутри
│ └──────────────────┘ │      │ (DEBUG) Developer >│
└──────────────────────┘      └────────────────────┘
```

## 4. Первый запуск

Аккаунт обязателен (COMMERCIAL_PLAN раздел 0 главнее раздела 5). **Кнопки «Пропустить» нет** ни на одном шаге. Аккаунт не даёт доступа к ПК: доступ — только по QR.

```
[1 Что это] → [2 Вход / Регистрация] → [3 Установите на ПК] → [4 QR] → RootTabView (Home)
                    │                                              │
                    └─ уже есть аккаунт: «Войти»                   └─ «Подключу позже» → RootTabView
                                                                      с EmptyState «ПК не подключён»
```

| Шаг | Экран | Содержимое | Переходы |
|-----|-------|-----------|----------|
| 1 | Что это | иллюстрация (SF Symbol `desktopcomputer` + `iphone`), заголовок, 3 пункта: управление питанием и программами, экран/тачпад/клавиатура, всё в домашней сети | «Продолжить» → 2 |
| 2 | Вход / регистрация | «Создать аккаунт», «Войти», «Sign in with Apple»; пояснение, зачем аккаунт и что Free затем работает без интернета | успех → 3 (если на iPhone уже есть сопряжённый ПК → сразу RootTabView); экраны потоков — E5-3 |
| 3 | Установите на ПК | «Откройте на компьютере {DOWNLOAD_URL} и установите Nexus PC»; «Поделиться ссылкой» (системный лист) | «Уже установлено» → 4 |
| 4 | Отсканируйте QR | инструкция «На ПК: значок N у часов → Подключить iPhone»; кнопка «Сканировать QR» → `PairingFlowView` | успех → Home; «Подключу позже» → RootTabView |

«Подключу позже» на шаге 4 — не пропуск аккаунта: аккаунт уже создан, откладывается только сопряжение ПК. Аккаунт без ПК бесполезен для управления, поэтому вкладки покажут `EmptyState`.

Состояния шага 2: загрузка (кнопки выключены, `ProgressView` в кнопке), нет интернета (`ErrorCard` «Для первой настройки нужен интернет. После неё бесплатные функции работают без него.» + «Повторить»), ошибка сервера (общий `ErrorCard` + «Повторить»).

Повторный запуск: если аккаунт есть — сразу `RootTabView`; если вход истёк, а ПК сопряжён — Free-управление продолжает работать без входа (агент не проверяет аккаунт для Free), в More → Account показывается «Войдите снова», без блокирующего экрана.

### Тексты первого запуска

| Ключ | en | ru | Где |
|------|----|----|-----|
| `onboarding.intro.title` | Control your PC from iPhone | Управляйте ПК с iPhone | шаг 1 |
| `onboarding.intro.point.power` | Power, apps and profiles in one tap | Питание, программы и профили в одно касание | шаг 1 |
| `onboarding.intro.point.control` | Screen, touchpad and keyboard | Экран, тачпад и клавиатура | шаг 1 |
| `onboarding.intro.point.local` | Works over your home network | Работает через домашнюю сеть | шаг 1 |
| `onboarding.continue` | Continue | Продолжить | шаг 1, 3 |
| `onboarding.account.title` | Create your Nexus PC account | Создайте аккаунт Nexus PC | шаг 2 |
| `onboarding.account.why` | An account keeps your PCs and plan in one place. After setup, free features work without the internet. | Аккаунт хранит список ваших ПК и тариф. После настройки бесплатные функции работают без интернета. | шаг 2 |
| `onboarding.account.create` | Create Account | Создать аккаунт | шаг 2 |
| `onboarding.account.signIn` | Sign In | Войти | шаг 2 |
| `onboarding.account.offline` | Internet is needed for the first setup. After it, free features work without it. | Для первой настройки нужен интернет. После неё бесплатные функции работают без него. | шаг 2, ошибка |
| `onboarding.install.title` | Install Nexus PC on your computer | Установите Nexus PC на компьютер | шаг 3 |
| `onboarding.install.body` | On your Windows PC, open {DOWNLOAD_URL} and install Nexus PC. | На компьютере с Windows откройте {DOWNLOAD_URL} и установите Nexus PC. | шаг 3 |
| `onboarding.install.share` | Share Link | Поделиться ссылкой | шаг 3 |
| `onboarding.install.done` | It's Installed | Уже установлено | шаг 3 |
| `onboarding.pair.title` | Connect your PC | Подключите ПК | шаг 4 |
| `onboarding.pair.body` | On the PC, click the N icon near the clock → Connect iPhone. Then scan the QR code. | На ПК нажмите значок N у часов → «Подключить iPhone». Затем отсканируйте QR-код. | шаг 4 |
| `onboarding.pair.scan` | Scan QR Code | Сканировать QR-код | шаг 4 |
| `onboarding.pair.later` | Connect Later | Подключу позже | шаг 4 |
| `tab.home` | Home | Главная | таб-бар |
| `tab.control` | Control | Управление | таб-бар |
| `tab.pc` | PC | ПК | таб-бар |
| `tab.automations` | Automations | Автоматизации | таб-бар |
| `tab.more` | More | Ещё | таб-бар |
| `section.screen` | Screen | Экран | Control |
| `section.touchpad` | Touchpad | Тачпад | Control |
| `section.keyboard` | Keyboard | Клавиатура | Control |
| `section.monitor` | PC Monitor | Мониторинг | PC |
| `section.rgb` | RGB Lighting | Подсветка | PC |
| `section.files` | Files | Файлы | PC |
| `section.automations` | Automations | Автоматизации | Automations |
| `section.history` | History | История | Automations |
| `section.apps` | Apps | Программы | Home |
| `section.profiles` | Profiles | Профили | Home |
| `section.account` | Account | Аккаунт | More |
| `section.devices` | My Devices | Мои устройства | More → Account |
| `section.settings` | Settings | Настройки | More |
| `section.help` | Help & Support | Помощь и поддержка | More |
| `section.diagnostics` | Diagnostics | Диагностика | More → Help |
| `common.seeAll` | See All | Все | Home, секции |

Подписи RU в таб-баре короче 12 символов («Автоматизации» — 13: при AX-размерах система показывает подпись в крупном HUD по долгому нажатию, обрезки в самом таб-баре допускаются системой; проверить на iPhone SE).

## 5. Доступность навигации

- Таб-бар системный: VoiceOver читает «Главная, вкладка, 1 из 5». Свои подписи не нужны, но `Label` обязан иметь текст.
- Порядок фокуса в корне Home: имя ПК и статус → питание (слева направо) → таймер и «Отменить» → Profiles → Apps.
- Первый запуск: фокус VoiceOver при смене шага ставится на заголовок шага (`accessibilityFocused`), заголовок помечен `.isHeader`.
- Все шаги онбординга в `ScrollView`: при AX5 кнопки не уходят за экран.
- Переходы между шагами: сдвиг по горизонтали; при Reduce Motion — перекрёстное затухание (`foundations.md`, «Анимации»).

## 6. ЗАДАНИЯ

```
ЗАДАНИЕ E6-NAV (черновик для E5-5): навигация TabView и первый запуск
Исполнитель: ios-developer
Файлы: ios/NexusPC/App/NexusPCApp.swift; ios/NexusPC/Views/HomeView.swift; новые ios/NexusPC/Views/RootTabView.swift, Onboarding/*.swift, ProfilesView.swift, AppsView.swift, KeyboardView.swift, ControlHomeView.swift, PCHomeView.swift, MoreView.swift; Localizable.xcstrings
Сделать: по docs/design/navigation.md разделы 2–4: RootTabView из 5 вкладок; перенос разделов из HomeView по карте; первый запуск из 4 шагов без кнопки пропуска аккаунта; PermissionCard вместо скрытия разделов; тексты по таблице раздела 4.
Не трогать: NexusKit, протокол, PairingFlowView (кроме точки вызова), логику PCController.
Критерии приёмки: все 14 разделов достижимы не более чем за 2 касания от таб-бара; ни один раздел не скрыт из-за разрешения; на первом запуске нет «Пропустить»; «Забыть ПК» не на Home; литералов в Text("…") нет.
Тесты: сборка iOS в CI; UI-проверка вручную по чек-листу QA.
Зависит от: E5-5 (окончательная нарезка), E5-3 (экраны аккаунта)
```
