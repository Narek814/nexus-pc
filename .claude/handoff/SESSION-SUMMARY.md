# Сводка для новой сессии (2026-11-23)

Ветка `claude/nexus-pc-commercial-5stkn9`, черновой PR Narek814/nexus-pc#9 (не слит, CI зелёный на 3a6884c). Правила — `CLAUDE.md`, `.claude/AGENT_RULES.md` (7а — экономия лимита).

## Сделано
- ENT-2 / ENT-2b: проверка токена прав в агенте (вариант A: без ключей проверка плана выключена). C# 1112 тестов.
- Агенты `backend-developer` (`backend/**`, `docs/BACKEND.md`) и `web-developer` (`site/**`, `docs/SITE.md`), зоны в охраннике.
- E3 (сервер, локально, Supabase Free EU): миграции 000001–000009, RLS, вход ПК кодом (подпись ключом ПК, перепривязка только старым ключом или после `pc_release`, describe→approve), refresh ПК (ротация, reuse detection, идемпотентный повтор 60 с), Edge Functions (pc-login-start/poll, pc-token, pc-login-describe/approve, pc-unlink, delete-account), cleanup по pg_cron. SQL 269 проверок, TS 78 тестов, оба в CI. Аудиты пройдены.
- Документы: `docs/API.md` «Сервер прав (HTTPS)», `docs/BACKEND.md`, решения — `docs/COMMERCIAL_PLAN.md` раздел 0.

## Ждёт владельца
- N2: защита первой привязки ПК (`pcId` из ключа устройства) — до E4 (OPEN в COMMERCIAL_PLAN).
- Повторный вход через Apple для удаления аккаунта — до E6 (OPEN).
- ID функций Free/Pro, лимит файлов, `screen.video`/`clipboard.*`/`history.list` во Free; домен и `iss`; правообладатель LICENSE; юрлицо, платёжный провайдер, цены.
- Облако: проект Supabase (Франкфурт), секреты из `backend/.env.example` (`openssl rand -base64 32`), ключи подписи токена (рабочий + 2 запасных).
- Слияние PR #9.

## Проверить на стенде Supabase
Заголовок IP (cf-connecting-ip / XFF), `auth.uid()` из JWT при anon apikey, формат `amr` и `last_sign_in_at` при refresh, pg_cron, функции в Deno, ответы GoTrue admin API.

## Дальше
E4 (вход на ПК: трей и служба ↔ сервер; нужен N2) → E5 дизайн → E6 iOS → E7 оплата → E8 сайт → E9 релиз; App Store — в конце.
