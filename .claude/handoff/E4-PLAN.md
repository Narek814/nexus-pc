# План E4 (code-architect, 2026-11-24) — ждёт согласования владельца

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
