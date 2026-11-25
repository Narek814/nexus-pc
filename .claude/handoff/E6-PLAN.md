# План после E5 (итог E5-5, code-architect) — ждёт утверждения владельца

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

## НУЖНО РЕШЕНИЕ (рекомендации)
1. Коды причин: аддитивные `unavailableCodes` в metrics.snapshot и `reasonCode` в rgb.status — рекомендация: да, отдельной задачей в E6.
2. Матрица Free/Pro: `screen.video` → Pro (новый feature), clipboard.*, history.list, delaySeconds → Free.
3. Поле `rebind` в ответе pc-login-describe — рекомендация: да, после вердикта security-auditor; иначе условный текст.
4. `pcs.limit` уже при approve (backend; consume тоже проверяет) — рекомендация: да.
5. Список активных сессий — не делать до E9.
6. «Подключу позже» на шаге QR — оставить; RU-подпись «Автоматизации» в таб-баре — «Сценарии» или проверка на iPhone SE.
7. Проверка обновлений в трее — открывать {DOWNLOAD_URL}; двойной щелчок по значку — «Устройства и разрешения»; исходники GPL — на сайте рядом со страницей загрузки.
8. Повторный вход Apple при удалении аккаунта — принимать (свежесть 300 с).
Не проверено архитектором: отдаёт ли RLS my_pcs поле pc_id в iOS; строгость декодера NexusKit (тест в E6-SCREENS).
