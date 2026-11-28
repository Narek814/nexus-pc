# Сводка для новой сессии (2026-11-25)

Рабочая ветка `claude/vigilant-gauss-za72xl` (утверждена владельцем). PR Narek814/nexus-pc#9 — черновик, НЕ сливать и не закрывать. PR Narek814/nexus-pc#10 — только для Windows CI, не сливать. Правила — `CLAUDE.md`, `.claude/AGENT_RULES.md`. К каждому вопросу давать рекомендацию.

## E5 завершён
- E5-0 (21dfec8): account.dat > 8 КБ удаляется без чтения + audit; отказ доступа — audit account.accessDenied без удаления; «Отменено» только при UAC 1223. QA ПРИНЯТО (1270/1270 Linux), security-auditor без замечаний (2 низкие → W-ACC). Windows CI на PR #10 — проверить.
- E5-1…E5-4 + E5-3a: спецификации в docs/design/** (README, navigation, foundations, ios-screens, error-texts, ios-account, windows). QA ПРИНЯТО.
- E5-5: сверка с протоколом и план дальше — `.claude/handoff/E6-PLAN.md`.

## Решения владельца (действуют)
Отвязка локальная; почту аккаунта на ПК не показывать; без постоянной фоновой задачи входа; userCode — обычное окно «Аккаунт», повышенный процесс без UI, отказ UAC = «Отменено»; план E5 утверждён.

## Дальше
Владелец утверждает E6-PLAN.md и решения из его раздела «НУЖНО РЕШЕНИЕ» → W-ACC (windows-developer).

## Ручная проверка владельцем на Windows
«Войти» → UAC «Да» показывает код; UAC «Нет» → «Отменено»; login.start из неповышенной консоли → admin.required; окно «Аккаунт» EN/RU, отвязка, перезапуск трея.

## Ждёт владельца
Домен и `iss`, проект Supabase, anon-ключ, TLS pinning; функции Free/Pro, LICENSE, юрлицо, оплата, цены; повторный вход Apple для удаления аккаунта; слияние PR #9.

## 2026-11-25 (вечер)
- W-ACC готово (967f23a + 66bd3fd): QA ПРИНЯТО 1276/1276 Linux; security-auditor без замечаний (WACC-1 Low: нет сводки повторных отказов accessDenied; WACC-2/3 Info). Windows CI на PR #10 — проверить.
- П.5 W-ACC (docs/API.md: раздел «Коды канала администратора и трея» + опечатка confirm.required) НЕ внесён: охранник запретил windows-developer. Текст — в отчёте исполнителя; ждёт решения владельца, кто вносит.
- Для W-TRAY добавить: тест аудита после FileNotFound, вынос проверки пути повышенного процесса в Core (QA), WACC-3 (в tray.log не писать ex.Message). Пункт «Проверить обновления» теперь решён: открывать {DOWNLOAD_URL}.
- W-TRAY принято QA (1299/1299 Linux). Хвосты → W-TRAY-2 после ручной проверки владельца: AppsForm ex.Message; фикс. размеры Label (DPI 200%); «Повторить» в Устройствах; лог при !owned && restarting (Program.cs:24-36); WindowsDesktop.cs:38 шлёт ex.Message на телефон (передать security-auditor); хрупкая проверка message.contains("разрешения:") в ios AutomationsView.swift:668. Дальше: W-PROTO.
- Владелец 2026-11-28: {DOWNLOAD_URL} = страница релизов GitHub до домена; W-TRAY-2 — после ручной проверки владельца на Windows.
- W-PROTO принято QA (1327/1327, фикстуры 29/29); docs/API.md внесла главная сессия. Дальше BE-1.
- BE-1 принято QA (SQL 290, TS 83/83); миграция 20261128000011 не применена на стенде. Вне списка файлов: _shared/db.ts:159 (PRO_FEATURES) — QA: нужна; ждёт подтверждения владельца. Пробелы: параллельный тест approve‖insert; approve чужого ПК при 20. Дальше E6-DS.

## Старт новой сессии (точка остановки 2026-11-28)
1. Проверить Windows CI на PR #10 для b978900 (W-TRAY), 7b3539d (W-PROTO), af7257a (BE-1); падения — через qa-engineer.
2. Ждут решения владельца (рекомендации: да/да/да): принять _shared/db.ts вне списка BE-1; два теста-пробела QA добавить в BE-2; запуск E6-DS (ios-developer, сборка только в ios.yml).
3. Порядок дальше: E6-DS → QA → E6-NAV → QA → E6-SCREENS → QA → E6-ACC-1 → QA → SEC-REBIND → BE-2 → QA → E6-ACC-2 → … Блоки — .claude/handoff/E6-PLAN.md (SEC-REBIND/BE-2 — в кратком виде; полный текст при необходимости запросить у code-architect заново).
4. docs/API.md правит главная сессия по готовому тексту исполнителя (охранник не пускает windows-/backend-developer).

## 2026-11-28 (новая сессия)
- Windows CI на PR #10 (69a9767, включает W-TRAY/W-PROTO/BE-1) — всё зелёное.
- Владелец: _shared/db.ts вне списка BE-1 ПРИНЯТ; два теста-пробела (approve‖insert, approve чужого ПК при 20) — в BE-2; E6-DS запущен.
- E6-DS выполнен ios-developer; отклонения: Theme.Palette вместо Theme.Color, DS.StatusPill/DS.PowerTile (конфликт имён со старыми в экранах), ключи в ios/NexusPC/Siri/Localizable.xcstrings (Resources/ не существует), 4 доп. ключа (common.inProgress/unavailable/seeAll, status.a11y %@). Ждёт QA + CI ios.yml.
- E6-DS ПРИНЯТО QA (96dd6c1; ios.yml и ci.yml зелёные; Release). Открыто для владельца: шаг Debug в ios.yml (рек.: release-manager отдельно); принять отклонения E6-DS (рек.: да). Дальше E6-NAV.
- Владелец 2026-11-28: E6-NAV разбит (вариант A). E6-NAV-1 сделан (3c771b3), ждёт QA. Решения: History гейтится по .monitoring (как агент); подтверждение «Забыть этот ПК» — добавить в E6-SCREENS; шаг Debug в ios.yml — release-manager отдельной задачей; отклонения E6-DS приняты.
- CI-IOS-DEBUG: шаг Debug в ios.yml внесён главной сессией по разрешению владельца.
- Владелец: офлайн-карточка при любой потере связи — в E6-SCREENS; нестабильный DataDirectoryHardeningTests — windows-developer (W-FLAKY); подпись tab.automations — по проверке на iPhone.
