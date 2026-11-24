# Сводка для новой сессии (2026-11-24)

Ветка `claude/nexus-pc-commercial-5stkn9`, черновой PR Narek814/nexus-pc#9 (не слит). Правила — `CLAUDE.md`, `.claude/AGENT_RULES.md` (7а). На события PR не подписываться, CI проверять вручную после итогового коммита.

## Сделано в E4
- N2 и решения 1А/2/3 — CONFIRMED (`docs/COMMERCIAL_PLAN.md` раздел 0).
- План E4 согласован: `.claude/handoff/E4-PLAN.md` (блоки «Задание» передавать дословно).
- E4-1: фикстура `shared/fixtures/pc-id/derive.v1.json`, абзац «Идентификатор ПК» в `docs/API.md`.
- E4-2 (QA принято): миграция 000010 `pc_id_from_spki`, сверка в `pc_login_start` и Edge `pc-login-start`. SQL 284, TS 80, check_fixtures зелёный. Замечание QA (не блокер): SQL проверяет только форму SPKI, точку на кривой — Edge.

## Дальше
E4-3 сделан (aef3cec, QA условно — проверить Windows CI). Дальше E4-3a (раздел 6 E4-PLAN.md, degraded mode согласован) → QA → E4-4 → QA → E4-5 → QA → аудит security-auditor на E4 (раздел 5 + дополнение раздела 6). Затем E5.

## Ждёт владельца (без изменений)
Домен и `iss`, проект Supabase, anon-ключ, TLS pinning (блокеры реального сервера в E4-4); функции Free/Pro, LICENSE, юрлицо, оплата, цены; повторный вход Apple для удаления аккаунта (до E6); слияние PR #9.
