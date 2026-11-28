# План после E5 (итог E5-5, code-architect) — УТВЕРЖДЁН владельцем 2026-11-25

E5 завершён: E5-0 (21dfec8, QA ПРИНЯТО, аудит без замечаний), E5-1…E5-4 + E5-3a (docs/design/**, QA ПРИНЯТО). Все экраны реализуемы на протоколе v1 + аддитивные поля E1; блокирующих изменений протокола нет.
Проверено главной сессией: агент отвечает `confirm.required` (windows/Nexus.Core/Protocol/ErrorCodes.cs:16) → `auth.confirmationRequired` в docs/API.md:364 — опечатка, правится в W-ACC (п.5).

Порядок (один агент записи): W-ACC → QA → security-auditor → W-TRAY → QA → E6-DS → QA → E6-NAV → QA → E6-SCREENS → QA → E6-ACC-1 → QA → E6-ACC-2 → QA → security-auditor → E6-ACC-3 → QA → W-SETUP (после решения LICENSE) → QA.

```
ЗАДАНИЕ W-ACC: окно «Аккаунт» трея — опрос только при открытом окне, ProblemElevationFailed, общий путь лога, дедупликация аудита
Исполнитель: windows-developer
Файлы: windows/Nexus.Tray/AccountForm.cs, ElevatedAccount.cs, TrayContext.cs (только путь лога), TrayText.cs, Strings.resx, Strings.ru.resx; windows/Nexus.Core/Account/AccountLoginModel.cs, ElevatedAccountCommand; windows/Nexus.Agent/Hosting/AgentHost.cs (колбэк onAccessDenied), windows/Nexus.Agent/Account/RefreshTokenStore.cs (только если нужно для дедупликации); docs/API.md (новый раздел «Коды канала администратора и трея»; исправить опечатку auth.confirmationRequired → confirm.required в строке ~364); windows/Nexus.Tests/** (новые тесты)
Сделать:
1. AccountController (AccountForm.cs:6-20): опрос login.status запускать при открытии окна «Аккаунт» и останавливать при закрытии. При повторном открытии сразу один раз запросить статус. Комментарий в начале файла привести в соответствие. Фоновой задачи не добавлять.
2. Добавить AccountProblem.ElevationFailed и строку ProblemElevationFailed в EN и RU: «Не удалось запустить шаг с правами администратора». Использовать для ошибок запуска повышенного процесса: Win32Exception с кодом не 1223 (ElevatedAccount.cs:47-56), Process.Start вернул null, exe не найден. Для этого завести отдельный код выхода, например ExitCodeOfError("elevation") в ElevatedAccountCommand. Код "failed" по-прежнему означает отказ службы (ProblemFailed). Отказ UAC (1223) — по-прежнему null и «Отменено».
3. Путь к tray.log задавать в одном месте: TrayLog.DefaultPath или общая константа. Убрать дубль в ElevatedAccount.cs:52 и TrayContext.cs:33/:75.
4. Аудит "account.file"/"account.accessDenied" (AgentHost.cs:295): записывать один раз на процесс службы, а после успешного Load сбрасывать флаг, чтобы новая проблема записалась снова. Удаление по handle не делать.
5. docs/API.md: добавить раздел «Коды канала администратора и трея (не WebSocket)» с кодом admin.required и смыслом остальных кодов этого канала; исправить опечатку auth.confirmationRequired → confirm.required. Протокол WebSocket не менять.
Не трогать: формат account.dat, DPAPI, ACL; протокол v1; решения «userCode — обычное окно Аккаунт, повышенный процесс без UI», «отвязка локальная», «почту не показывать»; текст «Отменено»; DevicesForm.cs и RunAdminCommand (они в W-TRAY).
Критерии приёмки: при закрытом окне запросы login.status не идут (тест модели или контроллера с фейковым таймером); ElevationFailed отличается от Failed и от AdminDeclined; путь лога задан в одном месте; повторный Load с отказом в доступе даёт одну запись аудита; userCode и токены не логируются; dotnet test windows/Nexus.slnx -c Release зелёный.
Тесты: теория «код выхода → AccountProblem» (null, "failed", "elevation"); тест дедупликации аудита accessDenied; тест старта и остановки опроса; полный набор.
Зависит от: нет
```

```
ЗАДАНИЕ W-TRAY: меню трея, окна, Настройки, локализация EN/RU, обработка UAC
Исполнитель: windows-developer
Файлы: windows/Nexus.Tray/TrayContext.cs, TrayText.cs, Strings.resx, Strings.ru.resx, PairingForm.cs, DevicesForm.cs, AppsForm.cs, Program.cs, Theme.cs, ScreenVideo.cs, ShellLinks.cs, WindowsDesktop.cs, AgentConnection.cs (только пользовательские строки), SettingsForm.cs (новый); windows/Nexus.Tests/**
Сделать:
1. Сделать по docs/design/windows.md (меню, окна, Настройки) и черновику W-TRAY оттуда же.
2. Все пользовательские строки трея вынести в Strings.resx (EN, основной) и Strings.ru.resx. Язык — по языку интерфейса Windows, для неподдерживаемого — EN. Тексты логов остаются на английском.
3. DevicesForm.cs:139: вместо общего catch(Win32Exception) — `when (ElevatedAccountCommand.IsUacDeclined(ex.NativeErrorCode))` → «Отменено». Любой другой Win32Exception → строка ProblemElevationFailed (добавлена в W-ACC) и запись в лог только NativeErrorCode.
4. TrayContext.cs:229-232 RunAdminCommand: отказ UAC показывать уведомлением «Отменено», прочие Win32Exception — ProblemElevationFailed плюс NativeErrorCode в лог. Без аргументов в логе.
5. Строку «Проверить обновления» не реализовывать, пока владелец не решит (windows.md:343).
Не трогать: протокол v1, admin-пайп, логику аккаунта из W-ACC, Nexus.Agent.
Критерии приёмки: grep кириллицы в windows/Nexus.Tray/*.cs находит только комментарии и логи; ключи EN и RU совпадают по набору; отказ UAC и ошибка запуска различаются в DevicesForm и RunAdminCommand; полный dotnet test зелёный.
Тесты: тест «ключи Strings.resx и Strings.ru.resx совпадают»; теория классификации Win32-кодов (переиспользовать IsUacDeclined); полный набор; ручная проверка UAC — в Windows CI или у владельца.
Зависит от: W-ACC
```

```
ЗАДАНИЕ E6-DS: дизайн-система iOS
Исполнитель: ios-developer
Файлы: ios/NexusPC/Views/DesignSystem/*.swift, Asset Catalog цветов, ios/NexusPC/Resources/Localizable.xcstrings
Сделать: дословно по черновику E6-DS в docs/design/foundations.md (токены, компоненты и их состояния, Dynamic Type, Reduce Motion, VoiceOver, контраст AA).
Не трогать: NexusKit, протокол, экраны вне DesignSystem.
Критерии приёмки: по черновику; сборка iOS Debug и Release в CI зелёная.
Тесты: сборка в CI; snapshot или preview-проверка компонентов, если она есть в проекте.
Зависит от: W-TRAY (только из-за очереди записи)
```

```
ЗАДАНИЕ E6-NAV: навигация TabView и первый запуск
Исполнитель: ios-developer
Файлы: дословно по черновику E6-NAV в docs/design/navigation.md
Сделать: по черновику. Аккаунт обязателен, кнопки «Пропустить» нет; текст о том, что Free работает без интернета.
Не трогать: протокол, NexusKit (кроме навигации).
Критерии приёмки: по черновику; каждый экран из карты docs/design/navigation.md достижим.
Тесты: сборка в CI; UI-тест «первый запуск → вход → QR», если есть UI-тесты.
Зависит от: E6-DS
```

```
ЗАДАНИЕ E6-SCREENS: состояния разделов, Free/Pro, тексты ошибок, Диагностика
Исполнитель: ios-developer
Файлы: по черновику в docs/design/ios-screens.md, плюс ios/NexusKit (только чтение agentVersion, plan, features, entitlementState из system.hello)
Сделать: по черновику, плюс:
- в «Диагностике» показывать «Версия на ПК» из system.hello.agentVersion (поле есть: docs/API.md:77);
- причины unavailable и rgb.reason пока показывать текстом ПК (запасной путь); если появятся поля кодов, клиент должен их поддерживать;
- спорные пункты — по решению владельца о матрице Free/Pro;
- коды confirm.required и auth.confirmationRequired показывать одной строкой;
- декодер ответов NexusKit должен игнорировать неизвестные поля.
Не трогать: протокол, логику питания и ввода.
Критерии приёмки: по черновику; «Версия на ПК» показывается; есть тест, что неизвестные поля в hello и snapshot не ломают декодирование.
Тесты: модульный тест «код → ключ» по docs/design/error-texts.md; тест декодера с лишними полями; сборка Debug и Release.
Зависит от: E6-NAV
```

```
ЗАДАНИЕ E6-ACC-1 / E6-ACC-2 / E6-ACC-3
Исполнитель: ios-developer (каждое задание отдельно, по очереди)
Файлы и шаги: дословно по черновикам E6-ACC-1..3 в docs/design/ios-account.md
Сделать (дополнение к E6-ACC-2): ПК аккаунта сопоставлять с локальным сопряжением по pcId (UUID в нижнем регистре) из my_pcs.pc_id и из сопряжения или hello. Предупреждение о перепривязке — условный текст, пока нет поля rebind. pcs.limit обрабатывать и в approve, и в consume.
Не трогать: протокол, backend.
Критерии приёмки: по черновикам; сопоставление по pcId покрыто модульным тестом.
Тесты: модульные тесты NexusKit-клиента describe/approve и unlink на фикстурах; сборка в CI.
Зависит от: E6-SCREENS → ACC-1 → ACC-2 → ACC-3; боевые тесты — после решения по Supabase; удаление через Apple — решение владельца
```

```
ЗАДАНИЕ W-SETUP: мастер установщика
Исполнитель: windows-developer
Файлы и шаги: дословно по черновику W-SETUP в docs/design/windows.md
Зависит от: W-TRAY; решение владельца по LICENSE; {THIRD_PARTY_SOURCES_URL}
```

## Решения владельца (2026-11-25): все 8 пунктов утверждены по рекомендациям
Блоки «Задание» для п.1, 3, 4 (протокол/backend) — у code-architect, затем показать владельцу.

## Бывшее НУЖНО РЕШЕНИЕ (рекомендации — приняты)
1. Коды причин: аддитивные `unavailableCodes` в metrics.snapshot и `reasonCode` в rgb.status — рекомендация: да, отдельной задачей в E6.
2. Матрица Free/Pro: `screen.video` → Pro (новый feature), clipboard.*, history.list, delaySeconds → Free.
3. Поле `rebind` в ответе pc-login-describe — рекомендация: да, после вердикта security-auditor; иначе условный текст.
4. `pcs.limit` уже при approve (backend; consume тоже проверяет) — рекомендация: да.
5. Список активных сессий — не делать до E9.
6. «Подключу позже» на шаге QR — оставить; RU-подпись «Автоматизации» в таб-баре — «Сценарии» или проверка на iPhone SE.
7. Проверка обновлений в трее — открывать {DOWNLOAD_URL}; двойной щелчок по значку — «Устройства и разрешения»; исходники GPL — на сайте рядом со страницей загрузки.
8. Повторный вход Apple при удалении аккаунта — принимать (свежесть 300 с).
Не проверено архитектором: отдаёт ли RLS my_pcs поле pc_id в iOS; строгость декодера NexusKit (тест в E6-SCREENS).

## Задания по протоколу/backend (code-architect, 2026-11-25) — УТВЕРЖДЕНЫ владельцем: feature id screen.video; 11 кодов как есть; W-PROTO одним заданием; ENT-2 не включать до iOS с fallback JPEG при plan.required (ограничение для release-manager)
Новый порядок: W-ACC → QA → security-auditor → W-TRAY → QA → W-PROTO → QA → BE-1 → QA → E6-DS → … → E6-ACC-1 → QA → SEC-REBIND (анализ) → BE-2 → QA → E6-ACC-2 → …
- W-PROTO (windows-developer): unavailableCodes в metrics.snapshot (11 кодов: sensorDriverMissing, sensorReadFailed, sensorMissing, warmingUp, osQueryFailed, gpuDriverMissing, gpuLibraryRejected, gpuLibraryLoadFailed, gpuInitFailed, gpuListFailed, platformUnsupported), reasonCode в rgb.status (openrgbNotResponding, conflict); PlanPolicy: feature screen.video (Pro), screen.frame Free; docs/API.md, фикстуры.
- BE-1 (backend-developer): pcs.limit в pc_login_approve (новая миграция, та же блокировка, свой ПК не считается, code.invalid без различий); screen.video в правах Pro.
- SEC-REBIND (security-auditor): вердикт по rebind в describe.
- BE-2 (backend-developer): rebind в describe — только после вердикта.
Полный текст блоков — в отчёте архитектора (запросить повторно при необходимости).
Риски: старый iOS + Free получит plan.required на screen.video и не перейдёт на JPEG (ScreenView.swift:388) → не включать ключи ENT-2 до iOS с fallback; BE-1 развернуть до агента с W-PROTO.

## E6-NAV разбит на два (владелец 2026-11-28, вариант A; блоки — code-architect)

```
ЗАДАНИЕ E6-NAV-1: RootTabView из 5 вкладок, перенос разделов из HomeView, PermissionCard вместо скрытия
Исполнитель: ios-developer
Файлы:
- изменить: ios/NexusPC/App/NexusPCApp.swift; ios/NexusPC/Views/HomeView.swift; ios/NexusPC/Siri/Localizable.xcstrings; ios/NexusPC/Views/TouchpadView.swift (только вынести поле ввода текста в переиспользуемый View, поведение не менять)
- новые: ios/NexusPC/Views/RootTabView.swift, ControlHomeView.swift, PCHomeView.swift, ProfilesView.swift, AppsView.swift, KeyboardView.swift, MoreView.swift, ThisPCView.swift
- только использовать, не менять: ios/NexusPC/Views/DesignSystem/*.swift (Theme.Palette, DS.StatusPill, DS.PowerTile, NavRow, EmptyState.noPC, PermissionCard, PermissionName, OfflineCard, SectionHeader, Toast)
Сделать:
1. RootTabView: TabView из 5 вкладок в фиксированном порядке. Подписи — ключи tab.home, tab.control, tab.pc, tab.automations, tab.more. Символы: house.fill, hand.point.up.left.fill, desktopcomputer, gearshape.2.fill, ellipsis.circle.fill. Label всегда с текстом. У каждой вкладки свой NavigationStack. NexusPCApp показывает RootTabView вместо HomeView, .preferredColorScheme(.dark) сохранить.
2. Единый PCController: `@State private var controller = PCController()` перенести из HomeView (HomeView.swift:6) в RootTabView и передать во все вкладки. В RootTabView перенести:
   - `.task { refresh }` и refresh по scenePhase == .active;
   - лист сопряжения `.sheet` с PairingFlowView и `.onOpenURL` с функцией handle(_:) (HomeView.swift:128-131, 376-406).
   Ссылка `nexuspc://pair…` открывает лист поверх любой активной вкладки. Ссылки `nexuspc://action/lock`, `cancel`, `profile/<id>` работают как сейчас. Ссылка `action/shutdown` переключает на вкладку Home и показывает существующий confirmationDialog: выключения без подтверждения нет. Логику и состояние диалога питания можно оставить в HomeView и привязать через Binding или общее состояние, но они должны быть в одном месте. Плашку `controller.banner` показывать один раз на уровне RootTabView через DS-компонент Toast.
3. Вкладка Home (HomeView, корень): заголовок — имя ПК (без «NEXUS PC / Command Center»). Затем statusCard с DS.StatusPill, powerGrid на DS.PowerTile с прежними confirmationDialog и pendingPower и «Отменить» таймера. Затем секция Profiles (SectionHeader section.profiles, превью до 3 шт., «Все» по common.seeAll → ProfilesView) и секция Apps (section.apps, превью → AppsView). ProfilesView и AppsView — полный список из кода profilesSection и appsSection. Удалить с Home все NavigationLink-переходы к разделам (HomeView.swift:34-89, monitoringLink, homeLink) и forgetButton. Локальные struct StatusPill, PowerTile и SectionTitle (HomeView.swift:424-480) заменить на DS-компоненты и удалить, если они больше нигде не используются (проверить grep).
4. Вкладка Control (ControlHomeView): список NavRow → Screen (section.screen → ScreenView), Touchpad (section.touchpad → TouchpadView), Keyboard (section.keyboard → KeyboardView). KeyboardView собрать из существующего кода: поле ввода текста из TouchpadView и ScreenKeysRow (ScreenView.swift:196). Новых команд протокола не добавлять.
5. Вкладка PC (PCHomeView): NavRow → PC Monitor (section.monitor → MonitoringView), RGB Lighting (section.rgb → RgbView), Files (section.files → FilesView; буфер обмена остаётся внутри Files).
6. Вкладка Automations: корень — AutomationsView. History (section.history → HistoryView) — строка или сегмент в корне вкладки. Новый «+» не добавлять, если его ещё нет.
7. Состояние каждого раздела — общий хелпер, например в RootTabView.swift, по порядку проверки:
   а) `controller.pc == nil` → EmptyState.noPC, кнопка открывает лист сопряжения. Это показывают корни Home, Control, PC и Automations.
   б) ПК сопряжён, но связи нет, а capabilities ещё не получены → OfflineCard или существующий статус. Не PermissionCard.
   в) связь есть, но `!controller.can(cmd)` → PermissionCard(permission:, onCheckAgain: { await controller.refresh() }).
   г) иначе — сам экран.
   Строка NavRow не скрывается ни в одном состоянии; PermissionCard показывается внутри раздела. Соответствие команд и разрешений:
   - screen.start → .screen;
   - input.move → .input (Touchpad, Keyboard);
   - files.list || clipboard.get → .files;
   - metrics.snapshot → .monitoring;
   - rgb.status → .rgb;
   - automations.list → .automation;
   - history.list → .automation;
   - плитки питания: power.* → .power (сейчас плитки выключаются через enabled, это оставить).
   Если для history.list в трее есть отдельное имя разрешения, взять его и указать в отчёте.
8. Вкладка More (MoreView): только работающие строки. Строка «Этот ПК» (ThisPCView) показывается, только если `controller.pc != nil`, и переносит без изменений поведения из HomeView.forgetButton (HomeView.swift:311-345): отпечаток ключа `DeviceCrypto.grouped(controller.deviceKeyFingerprint)`, «Обновить адрес ПК по QR-коду» (открывает тот же лист сопряжения с pairingLink = nil), «Забыть ПК» (controller.forget(), с прежним подтверждением, если оно есть). Также строка «Подключить ПК», открывающая лист сопряжения, если ПК нет. В DEBUG — ничего нового. Строк Account, Settings, Help, Diagnostics и Developer НЕ добавлять, заглушек и «Скоро» не делать. В комментарии отметить, что ThisPCView временная: в E6-ACC она переедет в Account → My Devices.
9. Локализация в ios/NexusPC/Siri/Localizable.xcstrings, EN основной, RU перевод, по таблице docs/design/navigation.md раздел 4:
   - tab.home, tab.control, tab.pc, tab.automations, tab.more;
   - section.screen, section.touchpad, section.keyboard, section.monitor, section.rgb, section.files, section.automations, section.history, section.apps, section.profiles.
   Ключи section.account, section.devices, section.settings, section.help и section.diagnostics пока не добавлять. Для «Этот ПК» добавить ключи section.thisPC (This PC / Этот ПК) и pc.updateAddressByQR, pc.forget, pc.keyFingerprint с текстами из существующего кода (RU) и английским переводом. RU для tab.automations — «Автоматизации» (решение владельца: при обрезке на iPhone SE заменить на «Сценарии»; в отчёте указать, проверялось ли это).
10. В новых и изменённых View не оставлять литералов Text("…") и Label("…") на русском: только ключи. Тексты внутри экранов, которые просто переносятся (ScreenView, RgbView и другие), не переписывать: это E6-SCREENS.
Не трогать: NexusKit; протокол v1 и shared/**; PCController (кроме чтения; если без изменения PCController никак, указать в отчёте минимальную правку); PairingFlowView и QRScannerView (кроме точки вызова); внутреннюю логику ScreenView, TouchpadView (кроме выноса поля ввода), MonitoringView, RgbView, FilesView, AutomationsView и HistoryView; DesignSystem/*; виджеты NexusWidgets и Siri/PCIntents.swift; первый запуск и онбординг (E6-NAV-2); ProCard и матрицу Free/Pro (E6-SCREENS).
Критерии приёмки:
- 5 вкладок с подписями EN/RU; у каждой свой NavigationStack.
- Все 11 разделов (Home, Apps, Profiles, Screen, Touchpad, Keyboard, PC Monitor, RGB, Files, Automations, History) достижимы не более чем за 2 касания от таб-бара.
- `grep -n "if controller.can" ios/NexusPC/Views` не находит условий, скрывающих строки и разделы; can() используется только для выбора состояния (PermissionCard или экран) и для enabled плиток питания.
- Без ПК корни Home, Control, PC и Automations показывают EmptyState.noPC; без связи — не PermissionCard.
- `nexuspc://pair…` открывает лист с любой вкладки; action/lock, cancel и profile работают; action/shutdown всегда идёт через подтверждение.
- На Home нет «Забыть ПК», отпечатка и «Обновить адрес по QR»; все три доступны в More → «Этот ПК» и работают как раньше.
- В More нет Account, Settings, Help, Diagnostics и заглушек.
- PCController создаётся ровно один раз (grep `PCController()` даёт одно место в Views/App).
- Ключи tab.* и section.* из п.9 есть в xcstrings с EN и RU.
- Новых зависимостей нет.
- CI .github/workflows/ios.yml зелёный: NexusKit tests и xcodebuild Release.
Тесты: локальной сборки Xcode нет — сборка проверяется только CI ios.yml (Release); отчёт должен ссылаться на прогон CI или прямо сказать, что CI ещё не запускался. UI-тестов в проекте нет, новых не добавлять. Если в NexusPC есть таргет модульных тестов, добавить тест хелпера состояния раздела: pc nil → noPC, нет capabilities → offline, нет команды → permission, есть → content; если таргета нет, вынести хелпер в чистую функцию и указать это в отчёте. Ручной чек-лист для QA — в «Передаче в QA».
Зависит от: E6-DS (принято)
```

```
ПЕРЕДАЧА В QA E6-NAV-1
Что проверить:
1. Статически: grep `PCController()` — одно место; grep `if controller.can` в ios/NexusPC/Views — нет скрывающих условий; в HomeView нет forgetButton, NavigationLink к разделам и локальных StatusPill, PowerTile и SectionTitle; .onOpenURL только в RootTabView; action/shutdown ведёт к confirmationDialog; в MoreView нет Account, Settings, Help, Diagnostics; ключи tab.* (5) и section.* (10 + thisPC) имеют en и ru; нет Text/Label с кириллицей в новых файлах; NexusKit, shared/**, DesignSystem/** и PCController не изменены.
2. CI ios.yml: NexusKit tests и xcodebuild Release зелёные.
3. Ручной чек-лист (у владельца): 5 вкладок, повторное касание к корню; без ПК — EmptyState; ПК офлайн — не PermissionCard; выключенное разрешение — PermissionCard внутри раздела, строка видна; «Проверить снова»; QR deep link с вкладок PC и More; More → «Этот ПК»; виджет «Заблокировать» и «Выключить» (с подтверждением); VoiceOver подписей вкладок; «Автоматизации» на iPhone SE.
```

E6-NAV-2 (после E6-NAV-1 и E5-3 iOS): первый запуск из 4 шагов (Intro → Account → Install → Pair), без «Пропустить», onboarding.account.why о Free без интернета, {DOWNLOAD_URL} = страница релизов GitHub одной константой, при сопряжённом ПК шаг 3 пропускается, «Подключу позже» → вкладки с EmptyState, истёкший вход не блокирует управление, достижимость реализованных экранов карты, ключи onboarding.*. Полный блок — повторно у code-architect при запуске.
