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

## E6-SCREENS разбит на три (code-architect, 2026-11-28)
Матрица Free/Pro уже решена (выше). «Диагностика» входит (ios-screens.md:178). После E6-SCREENS-3 — security-auditor (экспорт диагностики, журнал ошибок). ENT-2 не включать до релиза iOS с E6-SCREENS-2 п.3.

```
ЗАДАНИЕ E6-SCREENS-1: модель hello и ошибок в NexusKit, единые состояния раздела, тексты ошибок по коду, подтверждение «Забыть этот ПК»
Исполнитель: ios-developer
Файлы:
- новые: ios/NexusKit/Sources/NexusKit/AgentHello.swift; ios/NexusKit/Sources/NexusKit/ErrorPresentation.swift; ios/NexusKit/Sources/NexusKit/SectionAccess.swift; ios/NexusKit/Tests/NexusKitTests/AgentHelloTests.swift; ios/NexusKit/Tests/NexusKitTests/ErrorPresentationTests.swift; ios/NexusKit/Tests/NexusKitTests/SectionAccessTests.swift
- изменить: ios/NexusKit/Sources/NexusKit/NexusError.swift; ios/NexusKit/Sources/NexusKit/NexusConnection.swift (только разбор error.details); ios/NexusPC/ViewModels/PCController.swift; ios/NexusPC/Views/RootTabView.swift (SectionState/SectionGate); ios/NexusPC/Views/ThisPCView.swift; ios/NexusPC/Siri/Localizable.xcstrings
- только читать: docs/design/error-texts.md (разделы 2, 3), docs/design/ios-screens.md (раздел 2, 3.1), docs/API.md:77, :174-176, :206-221, shared/protocol/fixtures/responses.v1.json, windows/Nexus.Core/Entitlements/PlanPolicy.cs:14-60
Сделать:
1. AgentHello (NexusKit, public, Sendable, Equatable): init(result: [String: Any]). Поля: protocolVersion, pcId, pcName, agentVersion?, authenticated, capabilities: Set<String>, plan?, features: Set<String>, entitlementState? (enum none/active/grace/expired/revoked плюс unknown(String)), validUntil?. Свойство supportsEntitlements = capabilities.contains("entitlements.v1"). Неизвестные поля и неизвестные значения не ломают разбор.
2. ServerErrorDetails (NexusKit): feature?, state?, limit?, retryAfter?. Разбирать error.details в NexusConnection. В NexusError.server добавить третье связанное значение details: ServerErrorDetails? = nil; все сопоставления case .server в ios/** поправить. В NexusError.errorDescription не выводить код: для .server — только message. Это запасной путь, основной — п.3.
3. ErrorPresentation (NexusKit, без SwiftUI): static func for(code: String, details: ServerErrorDetails?, section: String?) -> ErrorPresentation. Результат: key (String-ключ из docs/design/error-texts.md, раздел 3) и style (.card, .toast, .inline, .permission, .pro, .limit, .offline, .tray, .silent). Ровно по таблицам 3.1–3.7, включая: confirm.required и auth.confirmationRequired → один ключ error.confirm.required; plan.required с details.state expired/revoked → pro.expired.body / pro.revoked.body; session.unavailable в разделах control и files → error.session.signIn; screen.videoUnavailable и screen.notStarted → .silent. Неизвестный код → key nil; вызывающий показывает message ПК (не пустой, не длиннее 200 символов) или error.generic.body.
4. SectionAccess (NexusKit): чистая функция resolve(isPaired, connection: .online/.connecting/.offline, capabilities, anyOf commands, userSessionAvailable, needsTray, hello: AgentHello?, requiredFeature: String?) -> .noPC / .offline / .connecting / .permission / .trayMissing / .pro(state) / .content. Порядок — docs/design/ios-screens.md, раздел 2, шаги 1–6. Решение владельца: .offline при ЛЮБОЙ потере связи (state ≠ online и не connecting), независимо от кэша capabilities. .pro возвращается только если hello.supportsEntitlements и requiredFeature не входит в features. Разрешение (permission) всегда проверяется раньше Pro.
5. SectionState и SectionGate в RootTabView.swift перевести на SectionAccess. Добавить ветки: LoadingState для connecting, TrayMissingCard, ProCard для .pro (ключ pro.feature.<feature>.body, при expired/revoked — pro.expired.body / pro.revoked.body). PermissionName остаётся в приложении: сопоставление строки разрешения с PermissionName делается в SectionGate. Параметры needsTray и requiredFeature у SectionGate по умолчанию false и nil, существующие вызовы не меняются.
6. PCController: хранить hello: AgentHello? (заполнять в load(), :246). Публичное ошибочное состояние — lastError(code, details, message, section). banner для ошибок .server формировать по ErrorPresentation, а не через localizedDescription. Журнал «Диагностики»: кольцевой буфер последних 50 записей (время, раздел, код, message ПК) только в памяти, без сохранения на диск. rate.limited с retryAfter: хранить момент, до которого «Повторить» выключено.
7. ThisPCView: «Забыть этот ПК» через confirmationDialog. Ключи pc.forget.confirm.title, pc.forget.confirm.message, pc.forget.confirm.action, EN и RU. Текст: что сопряжение удаляется только на iPhone, а на ПК устройство остаётся в «Устройства и разрешения». Кнопка role: .destructive, есть «Отмена».
8. Localizable.xcstrings: добавить EN и RU для всех ключей error.* и pro.expired.body, pro.revoked.body, pro.card.ownerHint, limit.files.body, limit.files.bodyWithLimit, error.generic.body, error.rateLimited.retryIn из docs/design/error-texts.md (разделы 3.1–3.7) дословно. Ключи account.error.* не добавлять (E6-ACC).
Не трогать: протокол v1, shared/**, windows/**; логику питания, ввода, экрана; вёрстку разделов (E6-SCREENS-2); DesignSystem/*.swift (только использовать); MoreView (E6-SCREENS-3); NexusConnection кроме разбора details.
Критерии приёмки:
- AgentHello разбирает все hello-фикстуры responses.v1.json; лишние поля в hello и в metrics.snapshot не ломают разбор (тест с добавленным полем "futureField").
- details разбираются из фикстур plan.required, plan.limit, rate.limited.
- ErrorPresentation: для каждого кода из docs/design/error-texts.md 3.1–3.7 есть ожидаемый ключ и стиль (табличный тест); confirm.required и auth.confirmationRequired дают один ключ; неизвестный код → nil.
- SectionAccess: без связи при непустом кэше capabilities → offline; permission раньше pro; без entitlements.v1 никогда не pro; trayMissing только при needsTray.
- В основном тексте ошибки нет кода (grep "(\\(code))" в NexusError.swift пусто).
- «Забыть этот ПК» не срабатывает без подтверждения.
- Каждый новый ключ есть в EN и RU.
- swift test в ios/NexusKit зелёный; сборки iOS Debug и Release в CI зелёные.
Тесты: AgentHelloTests (фикстуры и лишние поля, entitlementState unknown); тест разбора details; ErrorPresentationTests (таблица «код → ключ, стиль» по error-texts.md); SectionAccessTests (порядок 1–6, offline при любой потере связи); swift test --package-path ios/NexusKit; сборка Debug и Release в CI.
Зависит от: E6-NAV-1 (принят, 3c771b3), W-PROTO (принят)
```

E6-SCREENS-2 (Free/Pro в разделах, состояния внутри Rgb и Files, unavailableCodes/reasonCode, SectionHeader вместо SectionTitle, ключи в Home/Monitoring/Rgb/Files/Profiles/PCHome/ControlHome; ScreenView → JPEG при plan.required) и E6-SCREENS-3 (More: Настройки, Помощь, Диагностика с журналом и экспортом; ключи в остальных экранах и PCController) — полные блоки в отчёте code-architect 2026-11-28; перед запуском запросить повторно. Открыто: переключатель языка (рек.: системная настройка), тексты monitor.reason.* (рек.: сначала ui-ux-designer).

## E6-SCREENS-2 — полный блок (code-architect, 2026-11-28)

```
ЗАДАНИЕ E6-SCREENS-2: Free/Pro в разделах, состояния внутри Rgb и Files, коды причин, SectionHeader
Исполнитель: ios-developer
Файлы:
- изменить: ios/NexusPC/Views/HomeView.swift; ProfilesView.swift; PCHomeView.swift; ControlHomeView.swift; MonitoringView.swift; RgbView.swift; FilesView.swift; ScreenView.swift (только переход на JPEG при plan.required и выбор видео во Free); AutomationsView.swift (только ProCard, режим «только чтение» во Free, AutomationsPausedBanner); RootTabView.swift (только аргументы requiredFeature и needsTray у SectionGate, вызов ProCard); ios/NexusPC/Models/Metrics.swift; ios/NexusPC/Views/DesignSystem/StateViews.swift (только ProCard: onLearnMore сделать необязательным; при nil кнопка «Подробнее» не рисуется); ios/NexusPC/Views/DesignSystem/DesignSystemPreviews.swift (только вызовы ProCard); ios/NexusKit/Sources/NexusKit/RgbStatus.swift (поле reasonCode); ios/NexusKit/Sources/NexusKit/ErrorPresentation.swift (таблицы кодов причин); ios/NexusKit/Tests/NexusKitTests/RgbStatusTests.swift; ios/NexusKit/Tests/NexusKitTests/ErrorPresentationTests.swift; ios/NexusPC/Siri/Localizable.xcstrings
- новые: ios/NexusPC/Views/DesignSystem/ProSegment.swift, ios/NexusPC/Views/DesignSystem/AutomationsPausedBanner.swift (по docs/design/ios-screens.md, раздел 9)
- читать: docs/design/ios-screens.md разделы 3–7, 9–11 (ключи — раздел 10, строки 244–270); docs/design/error-texts.md разделы 3.5, 3.8, 3.9, 3.10; docs/API.md:121, :316-320, :348-350; windows/Nexus.Core/Entitlements/PlanPolicy.cs:14-60; shared/protocol/fixtures/responses.v1.json; уже есть из E6-SCREENS-1: AgentHello, ServerErrorDetails, ErrorPresentation, SectionAccess, PCController.hello / lastError / diagnostics
Сделать:
1. Pro определять только через SectionAccess и controller.hello. Без маркера entitlements.v1 в capabilities нигде нет ни ProCard, ни ProBadge, и всё работает как сейчас. Соответствие «команда → feature» — как в PlanPolicy.cs: profiles.* → profiles; rgb.apply и rgb.profiles.* → rgb.sync; automations.upsert/delete/run/stop → automations; metrics.history с диапазоном 1h или 24h → monitoring.extended; screen.video → screen.video; файлы — только plan.limit. Free по решению владельца: clipboard.*, history.list, delaySeconds, screen.frame, automations.list, rgb.status.
2. ProCard: onLearnMore: (() -> Void)? = nil; при nil кнопка «Подробнее» не показывается. Решение владельца: пока нет экрана Pro, кнопку скрыть везде. Никаких кнопок-заглушек и TODO. Все вызовы ProCard — без onLearnMore.
3. Ключи EN и RU, тексты — из docs/design/ios-screens.md:262-266. Имена ключей должны совпадать с тем, что строит код ("pro.feature.<featureId>.body"):
   - pro.feature.profiles.body — «Open several apps with one tap.» / «Запуск нескольких программ одним касанием.»
   - pro.feature.rgb.sync.body — «Control lighting from iPhone.» / «Управление подсветкой с iPhone.»
   - pro.feature.automations.body — «Let the PC do things on schedule.» / «ПК сам выполняет действия по расписанию.»
   - pro.feature.monitoring.extended.body — «See up to 24 hours of history.» / «История показателей за сутки.»
   - pro.feature.screen.video.body — «Smooth video of the PC screen.» / «Плавное видео экрана ПК.»
   - pro.feature.files.body — «Transfer files without limits.» / «Передача файлов без ограничений.»
   Плюс ключи из раздела 10: automations.paused.title, automations.paused.body, monitor.historyReset, rgb.empty.body. Если нужного текста нет в документах, придумывать его не надо: в отчёте укажите ключ для ui-ux-designer.
4. Profiles: на Home и в ProfilesView во Free — компактный ProCard (pro.feature.profiles.body) вместо ряда профилей.
5. MonitoringView: сегменты «10 мин / 1 ч / 24 ч». «1 ч» и «24 ч» — ProSegment: во Free выбор не переключает диапазон, а открывает лист с ProCard (pro.feature.monitoring.extended.body). Подпись monitor.historyReset под пустым графиком.
6. RgbView:
   - во Free список устройств из rgb.status виден, элементы цвета, эффекта и профилей видны, но выключены; над ними ProCard (pro.feature.rgb.sync.body). В PCHomeView у строки RGB — NavRowBadge.pro, только когда SectionAccess даёт Pro;
   - available: false → ErrorCard по reasonCode через ErrorPresentation (error-texts.md 3.9): openrgbNotResponding → error.rgb.unavailable, conflict (или state == "conflict") → rgb.busy. Неизвестный или отсутствующий reasonCode → текст reason с ПК (запасной путь), если его нет — error.rgb.unavailable;
   - OpenRGB доступен, устройств нет → rgb.empty.body.
7. AutomationsView: во Free список виден только для чтения; переключатели, свайп-действия и «+» выключены, но не скрыты; сверху ProCard (pro.feature.automations.body). При entitlementState expired или revoked вместо ProCard — AutomationsPausedBanner (automations.paused.title и automations.paused.body), список виден. История (History) остаётся Free.
8. FilesView:
   - plan.limit (lastError с кодом plan.limit) → LimitNotice над списком. Число только из details.limit (limit.files.bodyWithLimit), иначе limit.files.body. Уже начатые передачи интерфейс не прерывает;
   - решение владельца: внутри раздела отдельные компактные PermissionCard для files и для clipboard. Нет files.* → вместо блоков «Отправить на ПК» и «Папка Nexus PC» PermissionCard(.files). Нет clipboard.* → вместо блока «Буфер обмена» PermissionCard(.clipboard). Остальное раздела работает. Проверки FilesView.swift:34, 41, 45, 134 убрать в пользу этих карточек или выключенных кнопок с подписью. FilesView.swift:330 (guard загрузки списка) оставить.
9. RgbView.swift:111 и :157 (rgb.profiles.upsert и rgb.profiles.delete): элемент не скрывать. Если нет разрешения rgb — компактный PermissionCard(.rgb). Если команда — Pro во Free, элемент выключен под ProCard (п.6).
10. ScreenView: plan.required на screen.video молча переводит экран на кадры JPEG, так же как screen.videoUnavailable (ScreenView.swift:388), и пишет событие в controller.diagnostics. Во Free, когда SectionAccess даёт Pro для screen.video, вариант видео — ProSegment, касание открывает ProCard (pro.feature.screen.video.body). Без entitlements.v1 поведение экрана не меняется.
11. Коды причин метрик:
   - Metrics.swift: разбирать unavailableCodes как [String: String] рядом с unavailable;
   - ErrorPresentation: таблица из error-texts.md 3.8 — 11 кодов (sensorDriverMissing, sensorReadFailed, sensorMissing, warmingUp, osQueryFailed, gpuDriverMissing, gpuLibraryRejected, gpuLibraryLoadFailed, gpuInitFailed, gpuListFailed, platformUnsupported) → monitor.reason.<code>; неизвестный код → monitor.reason.unknown;
   - MonitoringView: значение nil показывать как «—», причина — по коду; кода нет → текст ПК из unavailable; нет и текста → monitor.reason.unknown. Причина в accessibilityHint и по касанию. Для VoiceOver — monitor.unavailable.a11y;
   - ключи 3.8 в EN и RU дословно из error-texts.md.
12. RgbStatus (NexusKit): необязательное поле reasonCode: String?, разбирается из result["reasonCode"]. Существующий init сохранить: новый параметр со значением по умолчанию nil.
13. Тексты pc.forget.confirm.title, pc.forget.confirm.message, pc.forget.confirm.action в Localizable.xcstrings заменить финальными из error-texts.md 3.10 (EN и RU, дословно). ThisPCView не менять.
14. MonitoringView, RgbView, FilesView перевести с SectionTitle на SectionHeader с ключами, затем удалить struct SectionTitle (HomeView.swift:197).
15. Русские строковые литералы в HomeView, ProfilesView, PCHomeView, ControlHomeView, MonitoringView, RgbView, FilesView заменить ключами EN и RU. Текст с ПК (имена устройств, reason, message) выводить через Text(verbatim:).
Не трогать: протокол v1, shared/**, windows/**; NexusConnection, AgentHello, SectionAccess, PCController (кроме чтения hello, lastError и записи в diagnostics); логику питания и ввода; внутреннюю логику потока экрана, кроме п.10; DesignSystem/*, кроме ProCard в StateViews.swift и превью; MoreView, SettingsView, HelpView, DiagnosticsView, переключатель языка (E6-SCREENS-3); литералы в AutomationsView вне п.7, ScreenView вне п.10, TouchpadView, KeyboardView, AppsView, ThisPCView и PCController (E6-SCREENS-3).
Критерии приёмки:
1. Hello без entitlements.v1: ни ProCard, ни ProBadge, ни NavRowBadge.pro не показываются; разделы работают как до задания.
2. Hello с entitlements.v1, но features без нужного id: раздел или элемент виден с ProCard, строка не скрыта. При expired или revoked — pro.expired.body или pro.revoked.body; в Automations — AutomationsPausedBanner.
3. На ProCard нет кнопки «Подробнее».
4. plan.required на screen.video ведёт к кадрам JPEG без ошибки на экране, событие есть в журнале диагностики.
5. plan.limit показывает LimitNotice; число только из details.limit.
6. В FilesView отдельные PermissionCard для files и clipboard; в RgbView и FilesView нет скрытия UI по controller.can, кроме FilesView.swift:330.
7. rgb.status с reasonCode conflict показывает rgb.busy, с openrgbNotResponding — error.rgb.unavailable; неизвестный код — текст reason ПК.
8. Каждый из 11 кодов причин метрик показывается своим ключом monitor.reason.<code>; неизвестный — monitor.reason.unknown; кода нет — текст ПК.
9. Ключи pro.feature.{profiles, rgb.sync, automations, monitoring.extended, screen.video, files}.body есть в EN и RU и совпадают с ключами, которые строит код.
10. pc.forget.confirm.* совпадают с error-texts.md 3.10.
11. Поиск "SectionTitle" по ios/** ничего не находит.
12. В строковых литералах файлов из п.15 нет кириллицы (комментарии допустимы).
13. Все новые ключи есть в EN и RU.
14. swift test --package-path ios/NexusKit зелёный; сборки iOS Debug и Release в CI зелёные.
Тесты:
- RgbStatusTests: reasonCode из фикстур «rgb.status unavailable» и «rgb.status conflict», поле отсутствует → nil;
- тест разбора unavailableCodes из фикстуры «metrics.snapshot with unavailable and unavailableCodes» (если разбор метрик в приложении, вынести чистую функцию разбора в NexusKit или проверить через ErrorPresentation на значениях фикстуры);
- ErrorPresentationTests: 11 кодов метрик, неизвестный → monitor.reason.unknown; rgb openrgbNotResponding и conflict, неизвестный → nil;
- тест: для каждого feature id из PlanPolicy (profiles, rgb.sync, automations, monitoring.extended, screen.video) ErrorPresentation для plan.required даёт ключ pro.feature.<id>.body;
- swift test --package-path ios/NexusKit; сборка Debug и Release в CI;
- ручная проверка на устройстве у владельца: без маркера, Free, Pro, expired; Files без clipboard; RGB conflict; AX5 на iPhone SE.
Зависит от: E6-SCREENS-1 (принят, be65338), UX-TEXTS (внесён, 944be40)
```

```
ПЕРЕДАЧА В QA E6-SCREENS-2
Изменённые файлы: <пути>
Что проверить: критерии приёмки 1–14 задания E6-SCREENS-2
Запущено: swift test --package-path ios/NexusKit — <результат>; сборка iOS Debug и Release — <в CI: ссылка на прогон / не запускалось локально: нет Xcode>
Не запускалось: ручная проверка на устройстве (без маркера, Free, Pro, expired; Files без clipboard; RGB conflict; AX5 на iPhone SE) — у владельца
```
Примечание: имена ключей pro.feature.* в docs/design/ios-screens.md:262-267 отличаются от кода — привести документ к коду (ui-ux-designer, мелкая правка).
