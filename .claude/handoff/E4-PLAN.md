# План E4 (code-architect, 2026-11-24) — СОГЛАСОВАН 2026-11-24: решения 1А, 2 (программный ключ без TPM), 3

## 1. Рекомендация
`pcId` = UUIDv8 от SHA-256 по SPKI постоянного ключа ПК: `SHA-256("nexus-pc-id-v1\n" || SPKI DER)`, первые 16 байт, биты версии 8 и варианта RFC 9562.

- Ключ ПК: ECDSA P-256, машинный ключ CNG, неэкспортируемый, имя `NexusPC-Device-v1`; TPM (Microsoft Platform Crypto Provider), без TPM — Microsoft Software Key Storage Provider. Новых зависимостей нет (D10).
- Ключ не ротируется: новый ключ = новый ПК; старую привязку снимает `pc-unlink`. `spki` в `pc-login-start` всегда равен ключу устройства.
- Протокол v1, QR и схема по формату не меняются (`pc=`/`pcId` остаются UUID). Маркер в `capabilities` не нужен.
- Сервер: новая миграция 000010 — `pc_login_start` всегда проверяет `p_pc_id = derive(p_spki)`, иначе `pc.key_required`; Edge `pc-login-start` — та же проверка до БД. Перепривязка старым ключом и `pc_release` без изменений.
- Существующие установки: агент один раз создаёт ключ, пишет новый `pcId` в `config.json`, прежний — в `legacyPcId`. Спаренные iPhone требуют повторного сопряжения (`pcId` входит в `nexuspc-auth-v1`) — см. решение 1.

Альтернативы: сертификат в LocalMachine\My (лишняя сущность); два id (сеть/облако) — отклонено.

## 2. Затрагиваемые компоненты
- `windows/Nexus.Agent/Storage/AgentConfig.cs:37` (`Guid.NewGuid()` → derive), `Server/AgentIdentity.cs:7`, `Admin/AdminService.cs`, `windows/Nexus.Core/Security/DeviceCrypto.cs`, `Nexus.Core/Pairing/PairingLink.cs:20` (формат без изменений), `Nexus.Core/Entitlements/EntitlementTracker.cs`.
- `windows/Nexus.Tray/TrayContext.cs`, `PairingForm.cs`, `AgentConnection.cs`.
- `backend/supabase/migrations/20261123000005_pc_login.sql:145-187`; новая `20261124000010_pc_id_derive.sql`.
- `backend/supabase/functions/pc-login-start/index.ts:24-36`, `_shared/`.
- Тесты: `backend/tests/sql/50_pc_login.sql`, `backend/tests/ts/pc_login.test.ts`, `shared/fixtures/`.
- Документы: `docs/API.md` (24, 245-262, 76, 201), `docs/BACKEND.md`, `ARCHITECTURE.md`.

## 3. План

```
ЗАДАНИЕ E4-1: Общие тест-векторы derive(pcId) и описание в API
Исполнитель: windows-developer
Файлы: shared/fixtures/pc-id/derive.v1.json (новый), docs/API.md
Сделать: 1) Добавить фикстуру: ≥5 пар {spkiB64u, pcId}, все SPKI — P-256 несжатая точка; плюс негативные случаи: сжатая точка, лишние байты, не P-256 (ожидается отказ). Формула: h = SHA-256(ASCII "nexus-pc-id-v1\n" || SPKI DER); u = h[0..16]; u[6] = (u[6] & 0x0F) | 0x80; u[8] = (u[8] & 0x3F) | 0x80; pcId = UUID в нижнем регистре, формат D. 2) В docs/API.md (разделы «Сопряжение» и «Сервер прав») описать: pcId выводится из постоянного ключа ПК; spki в pc-login-start всегда равен ключу устройства; несовпадение pcId и spki → pc.key_required; формат QR не меняется.
Не трогать: формат nexuspc://pair, конверт, request.v1.schema.json, коды ошибок.
Критерии приёмки: векторы посчитаны независимо (скрипт в отчёте); в API.md нет противоречий с E3 (перепривязка старым ключом, pc_release).
Тесты: нет (данные для E4-2 и E4-3).
Зависит от: нет
```

```
ЗАДАНИЕ E4-2: Сервер — сверка pcId с ключом
Исполнитель: backend-developer
Файлы: backend/supabase/migrations/20261124000010_pc_id_derive.sql (новый), backend/supabase/functions/_shared/*, backend/supabase/functions/pc-login-start/index.ts, backend/tests/sql/50_pc_login.sql (или новый 55_pc_id_derive.sql), backend/tests/ts/pc_login.test.ts, docs/BACKEND.md
Сделать: 1) SQL-функция public.pc_id_from_spki(bytea) returns uuid по формуле из shared/fixtures/pc-id/derive.v1.json: sha256() из ядра PG, без новых расширений; immutable; set search_path = ''. 2) В новой миграции заменить public.pc_login_start (create or replace): сразу после pc_nonce_use — если p_pc_id <> pc_id_from_spki(p_spki), вернуть 'pc.key_required'. Проверка выполняется всегда, и при первой привязке, и при перепривязке. Если для этого pc_id в pc_keys есть ключ, p_spki обязан ему равняться (ключ не ротируется). 3) В Edge pc-login-start — та же проверка derive до обращения к БД; ответ тот же, что при неверной подписи. 4) В тестах: SQL — все векторы фикстуры, несовпадение pcId → pc.key_required, перепривязка старым ключом и после pc_release работают как раньше; TS — векторы фикстуры и отказ при чужом pcId. Перевести фикстуры E3 со случайным pcId на derive(spki). 5) docs/BACKEND.md — описание проверки.
Не трогать: миграции 000001–000009 (не редактировать), остальные функции, формат подписываемых строк, ответы API.
Критерии приёмки: все прежние 269 SQL-проверок и 78 TS-тестов зелёные плюс новые; первая привязка с pcId, не выведенным из spki, невозможна; секретов в коде нет.
Тесты: backend/tests/run_sql_tests.sh, backend/tests/run_ts_tests.sh.
Зависит от: E4-1
```

```
ЗАДАНИЕ E4-3: Агент — постоянный ключ ПК, derive pcId, миграция установок
Исполнитель: windows-developer
Файлы: windows/Nexus.Core/Security/PcIdDerivation.cs (новый), windows/Nexus.Agent/Security/DeviceKeyStore.cs (новый), windows/Nexus.Agent/Storage/AgentConfig.cs, windows/Nexus.Agent/Server/AgentIdentity.cs, windows/Nexus.Agent/Hosting/AgentHost.cs, windows/Nexus.Tests/**
Сделать: 1) Nexus.Core: PcIdDerivation.FromSpki(ReadOnlySpan<byte>) → Guid по shared/fixtures/pc-id/derive.v1.json; кроссплатформенный код, без Windows API. 2) Nexus.Agent: DeviceKeyStore — открыть или создать машинный ключ CNG "NexusPC-Device-v1", ECDSA P-256, неэкспортируемый, MachineKey; сначала Microsoft Platform Crypto Provider, при недоступности TPM — Microsoft Software Key Storage Provider; провайдер записать в лог. Наружу — только SPKI и подпись P1363. 3) AgentConfig: PcId = FromSpki(ключ). Если в старом config.json другой PcId — сохранить его в LegacyPcId, записать новый (атомарно), в лог — один раз. Если ключ потерян (пересоздан), у ПК новый pcId; в журнал — предупреждение. 4) Существующие сопряжённые устройства: поведение — по решению владельца (вариант А: остаются в списке, вход с прежним pcId отклоняется, пользователь сопрягает заново). 5) Новых NuGet-зависимостей не добавлять.
Не трогать: протокол, PairingLink (формат), DeviceCrypto.AuthTranscript, проверку токена прав.
Критерии приёмки: FromSpki проходит все векторы; при перезапуске pcId стабилен; старая установка переходит на новый pcId с LegacyPcId; ключ не экспортируется (тест на исключение при попытке экспорта, на Windows); все 1112 прежних тестов зелёные.
Тесты: Nexus.Tests — векторы derive, миграция config (старый id → новый + legacy), стабильность; тесты CNG помечены как Windows-only.
Зависит от: E4-1
```

```
ЗАДАНИЕ E4-4: Служба — клиент сервера прав (вход, опрос, refresh, отвязка)
Исполнитель: windows-developer
Файлы: windows/Nexus.Agent/Account/** (новые), windows/Nexus.Agent/Admin/AdminService.cs, windows/Nexus.Agent/Server/CommandDispatcher.cs (account.refresh), windows/Nexus.Tests/**
Сделать: 1) HttpClient к Edge Functions pc-login-start/poll, pc-token, pc-unlink по docs/API.md «Сервер прав (HTTPS)». Подписываемые строки nexus-pc-login-v1; подпись — DeviceKeyStore; nonce — 16..32 байт, новый на каждый запрос и на каждый повтор; ts в секундах. URL сервера и anon-ключ берутся из конфигурации сборки; пока домена нет, без них функциональность выключена (как в ENT-2 вариант A). 2) Refresh-токен хранить DPAPI LocalMachine в каталоге данных службы. Ротация атомарная, повтор после потерянного ответа — тем же старым токеном в пределах 60 с. 3) account.refresh из протокола вызывает pc-token; лимит 1 раз в минуту сохраняется; новый токен прав проходит EntitlementTracker. 4) Команды IPC для трея: login.start (вернуть userCode, срок), login.status (pending/ok/expired, email/аккаунт привязки), unlink. Только с проверенного пути трея (уже есть в AdminService). 5) Ответы revoked и pc.key_required: локальный refresh удалить, план → free.
Не трогать: формат протокола v1 для телефона, проверку токена прав, ключи подписи сервера.
Критерии приёмки: с фейковым HTTP-сервером проходит полный цикл start → poll (202 → ok) → token → refresh → unlink; секреты (refresh, deviceCode) не попадают в лог; без конфигурации сервера команды возвращают notImplemented/disabled.
Тесты: Nexus.Tests — подписываемые строки против векторов из backend/tests/ts, ротация и идемпотентный повтор, revoked, rate.limited, отсутствие секретов в логах.
Зависит от: E4-2, E4-3
```

```
ЗАДАНИЕ E4-5: Трей — экран входа и аккаунт привязки
Исполнитель: windows-developer (тексты и макет — ui-ux-designer, если нужен макет; иначе по Theme.cs)
Файлы: windows/Nexus.Tray/TrayContext.cs, windows/Nexus.Tray/AccountForm.cs (новый), windows/Nexus.Tray/AgentConnection.cs, ресурсы локализации EN/RU
Сделать: пункт меню «Аккаунт»: кнопка «Войти» показывает userCode (8 символов, группы по 4), срок 5 минут и обратный отсчёт, опрос login.status. После входа показывается аккаунт привязки (обязательно по аудиту E3-5), план и кнопка «Отвязать» с подтверждением. Обработать ошибки rate.limited, pcs.limit, отсутствие сети, сервер не настроен.
Не трогать: PairingForm (сопряжение iPhone), формат QR.
Критерии приёмки: все строки локализованы EN/RU; userCode не пишется в лог; трей работает без сервера.
Тесты: Nexus.Tests — модель состояний формы (без UI).
Зависит от: E4-4
```

После каждого задания исполнитель заканчивает отчёт блоком:
```
ПЕРЕДАЧА В QA E4-n
Изменённые файлы: <пути>
Что проверить: <критерии приёмки E4-n>
Запущено: <run_sql_tests.sh / run_ts_tests.sh / dotnet test — результат> / Не запускалось: <тесты CNG/TPM — нужна Windows, CI windows-latest>
```
QA — на E4-2…E4-5. security-auditor — после E4-2, E4-3, E4-4 (по 7а — допустимо одним аудитом на этап).

## 4. НУЖНО РЕШЕНИЕ
1. Совместимость: после обновления спаренные iPhone не войдут (сменился `pcId`). А — повторное сопряжение (рекомендовано: публичных пользователей нет). Б — переходный приём `LegacyPcId` + новый `pcId` из `hello` в iOS, маркер `pcid.v2`.
2. Нет TPM: программный ключ CNG (рекомендовано) или отказ во входе.
3. Потеря ключа (переустановка Windows, очистка TPM) = новый ПК; старый пользователь удаляет сам (`pc-unlink`, в лимите 20).

Блокеры E4-4 (реальный сервер): домен и `iss`, проект Supabase, anon-ключ, TLS pinning. До них — только фейковый сервер.

## 5. security-auditor проверяет
- derive: доменное разделение, усечение до 122 бит, подбор `pcId` под чужой ключ.
- Проверка `pcId ↔ spki` в SQL и Edge всегда; одинаковый ответ для привязанного и непривязанного ПК.
- Ключ CNG: MachineKey, неэкспортируемый, ACL только LocalSystem; слабее в Software KSP.
- Refresh через DPAPI LocalMachine.
- Трей ↔ служба: login/unlink только от проверенного трея; userCode другим процессам не выдаётся.
- Секреты не в логах; неподписанный `revoked` по закреплённому HTTPS.

## 6. E4-3a — режим «ключ ПК недоступен» (code-architect, 2026-11-24) — СОГЛАСОВАН 2026-11-24: degraded mode; автоматизации в degraded разрешены (бесплатный план)
Решение владельца по отказу ключа: без постоянного ключа авторизация и привязка запрещены, обходов нет; службу целиком не блокировать, если локальные функции безопасны.
Предложение архитектора: degraded — служба стартует без Kestrel/WebSocket/сертификата/EntitlementTracker/AgentIdentity; работают трей, admin-пайп (статус keyUnavailable, без pcId), метрики, RGB, автоматизации (бесплатный план). Повтор: 3 попытки при старте (2/5/10 с) и команда key.retry (admin-пайп, 1 раз в 60 с) → выход с ненулевым кодом → перезапуск SCM recovery. Плюс исправления тестов: изоляция/очистка FileDeviceKey, user-key для DeviceKeyStoreWindowsTests без прав администратора.
Решения владельца: degraded mode — да; автоматизации в degraded — да. E4-3 (aef3cec): QA принял условно, ждёт зелёного Windows CI.
audit.log: пишет только SYSTEM, каталог с защищённым DACL и проверкой при старте; от администратора не защищён (норма для модели угроз). Рекомендация: дублировать события безопасности в Windows Event Log.
security-auditor (дополнение к разделу 5): нет пути старого pcId/Guid.Empty/временного ключа в hello, AuthTranscript, QR, токен, сертификат; ошибки ключа без лишних данных; в degraded порт не слушается, key.retry только admin-пайп и с лимитом, нет цикла рестартов до TPM lockout, файлы не меняются, автоматизации без платных прав, Setup задаёт SCM recovery; в fail-closed нет бесконечного цикла рестартов.
Блок задания:

```
ЗАДАНИЕ E4-3a: Режим «ключ ПК недоступен» (degraded, fail-closed для сети) + исправления тестов ключа
Исполнитель: windows-developer
Файлы: windows/Nexus.Agent/Hosting/AgentHost.cs, windows/Nexus.Agent/Admin/AdminService.cs, windows/Nexus.Agent/Admin/AdminPipeServer.cs (и протокол пайпа трея, если команда добавляется там), windows/Nexus.Agent/Security/DeviceKeyStore.cs, windows/Nexus.Tray/** (отображение статуса и кнопка «Повторить»; строки EN/RU), windows/Nexus.Tests/PcIdTests.cs, windows/Nexus.Tests/AgentTestHost.cs, новые тесты в windows/Nexus.Tests/DeviceKeyDegradedTests.cs; docs/SECURITY или раздел безопасности в ARCHITECTURE.md (абзац о режиме).
Сделать:
1. AgentHost.Build: открыть ключ через фабрику (AgentHostOptions.DeviceKeyFactory для тестов) с 3 попытками и паузами 2/5/10 с (в тестах паузы инжектируются). При неудаче: audit "device.keyUnavailable" ok:false error=<тип исключения и HRESULT, без текста с путями и данными>, запись в Windows Event Log (существующий WriteEventLog), затем режим Degraded.
2. В Degraded НЕ выполнять: AgentConfig.LoadOrCreate(…, derivedPcId), AgentCertificate.LoadOrCreate, создание AgentIdentity, EntitlementTracker и EntitlementLoop, kestrel.Listen и Map(/v1/ws). config.json, entitlement.json и сертификат не читать для идентификации, не изменять и не удалять.
3. В Degraded запустить: TraySessionServer, AdminPipeServer, MetricsLoop, OpenRgbHost и RgbService, AutomationService, если её код не зависит от Identity, Entitlements и сети. Лицензируемые действия автоматизаций в Degraded по правилам «нет токена» (бесплатный план), явно, без null-разыменований.
4. AdminService в Degraded: статус {state:"keyUnavailable", error:<код>, attempts, lastAttemptUtc}, без поля pcId и без QR (pairing-команды возвращают ошибку keyUnavailable). AgentIdentity и pcId в этом режиме нигде не существуют: не делать PcId nullable, а разделить регистрацию сервисов.
5. Команда admin-пайпа "key.retry" (только admin-пайп, не session-пайп): audit "device.keyRetry", затем штатная остановка с ненулевым кодом, перезапуск по SCM recovery. Лимит: не чаще 1 раза в 60 с, иначе ошибка rateLimited. Проверить, что установщик задаёт recovery «перезапуск»; если нет, сообщить в отчёте, Setup не менять.
6. Трей: показать «Ключ защиты ПК недоступен — подключение телефонов отключено» (EN/RU) и кнопку «Повторить» (через admin-путь, как другие админ-действия).
7. FileDeviceKey: хранить ключ в отдельном каталоге каждого теста (Path.Combine(Path.GetTempPath(), "nexus-test-keys-" + Guid) или рядом с тестовым dataDirectory, но не внутри него), создавать с FileMode.CreateNew (не перезаписывать при гонке). Каждый тест и AgentTestHost удаляют свой ключ в Dispose; AgentTestHost при перезапуске агента в том же тесте использует тот же путь.
8. DeviceKeyStoreWindowsTests: для MachineKey нужны права администратора. Сделать проверку IsElevated (как DataDirectoryWindowsTests.cs:52); без прав выходить с явным сообщением через ITestOutputHelper (в xunit 2.9.3 нет динамического Skip). Добавить internal-перегрузку OpenOrCreate(name, machineKey:false) только для тестов (user-key в Software KSP), чтобы P256, подпись P1363, неэкспортируемость и стабильность проверялись и без прав. Производственный путь всегда machineKey:true.
Не трогать: формат config.json, деривацию pcId (PcIdDerivation, фикстуры), протокол v1 и схему, логику KeyReplaced/MigratedFromLegacy, ACL каталога данных, Nexus.Setup.
Критерии приёмки:
- Фабрика ключа бросает исключение → служба стартует, порт не слушается (TCP connect отклонён), трей и admin-пайп отвечают статусом keyUnavailable без pcId.
- config.json, entitlement.json и сертификат побайтно не изменены; новый сертификат не создан.
- В audit.log есть device.keyUnavailable; Event Log пишется (Windows).
- Ключ появился (фабрика начинает работать) → после перезапуска обычный режим, pcId совпадает с derive(SPKI), отметки KeyReplaced нет, если ключ тот же.
- key.retry: второй вызов за 60 с → rateLimited; с session-пайпа отклоняется.
- Нет кода, где pcId берётся из config в обход ключа (grep "config.PcId" только после успешного открытия ключа).
- Тестовые ключи после прогона не остаются в %TEMP%; параллельные тесты не делят файл ключа.
Тесты: DeviceKeyDegradedTests (порт закрыт, статус, неизменность файлов, retry-лимит, восстановление); FileDeviceKey-изоляция; DeviceKeyStoreWindowsTests (user-key без прав, MachineKey при правах). Полный dotnet test windows и Windows CI.
Зависит от: E4-3 (aef3cec)
```
Текст для документации, если выбран fail-closed (ARCHITECTURE.md раздел D, docs/SECURITY*): «Без действующего постоянного ключа NexusPC-Device-v1 агент не запускается: временные ключи, старый pcId и обходы не используются. Причина пишется в журнал событий Windows. Восстановление — перезапуск службы; при потере ключа все телефоны нужно сопрячь заново.»

### CI на aef3cec — красный (E4-3 не принят окончательно), чинить вместе с E4-3a
1. .NET tests (windows-latest): 2 падения — PcIdTests.Agent_KeyLost_WarnsInAudit и Agent_OldInstall_MigratesLogsOnceAndKeepsPairedDevices: DataDirectoryUntrustedException (тест сам создаёт каталог nexus-pcid-* в %TEMP% с наследуемым ACL). Исправление: создавать каталог тем же способом, что AgentTestHost (защищённый DACL).
2. iOS (macos-latest), шаг «Start a real Nexus.Agent»: агент падает PlatformNotSupportedException — на macOS нет CNG, AgentHost требует AgentHostOptions.DeviceKey. Живые тесты NexusKit не запускаются. Нужен источник ключа для не-Windows (только dev/CI) — ЖДЁТ РЕШЕНИЯ ВЛАДЕЛЬЦА.

### Решение владельца 2026-11-24 по ключу для macOS (дополнение к E4-3a) — СОГЛАСОВАНО
Разрешён отдельный постоянный файловый ключ только для разработки и CI на не-Windows (живые тесты iOS с настоящим агентом). Условия:
1. Ключ постоянный между запусками, в отдельном файле.
2. Включается только явным флагом разработки/CI.
3. В Release-сборках Windows механизм полностью недоступен.
4. Никакого автоматического fallback с CNG на файловый ключ.
5. Файл ключа защищён правами доступа, исключён из Git, логов и публикуемых артефактов.
6. Тесты: файловый ключ невозможен без явного флага и в Release-конфигурации Windows.
7. Windows — только CNG: TPM при наличии, иначе программный CNG (Software KSP).
По двум упавшим Windows-тестам: создавать тестовые папки данных тем же механизмом защищённых ACL, что в остальных тестах агента; проверки безопасности агента не ослаблять.
Порядок: E4-3a → полный dotnet test → Windows CI → iOS live tests (macOS) → проверка Release на отсутствие файлового fallback → QA. До принятия QA к E4-4 не переходить. Merge — только с отдельного разрешения.
