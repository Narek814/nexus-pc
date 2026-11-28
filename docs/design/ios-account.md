# iOS — аккаунт, вход ПК, «Мои устройства», удаление, каркас Pro

Задание E5-3. Опирается на `navigation.md` (место в More, первый запуск, шаг 2), `foundations.md` (токены и компоненты), `error-texts.md` раздел 4 (тексты кодов сервера), `ios-screens.md` раздел 3 (состояния плана). Источники решений: `docs/COMMERCIAL_PLAN.md` раздел 0 («Аккаунты обязательны», «Вход», «Удаление аккаунта», «Вход ПК E3-5», «Безопасность входа ПК», «Удаление аккаунта, аудит E3-8», OPEN «повторный вход Apple»), `docs/API.md` («pc-login-describe/approve»), `docs/BACKEND.md` (эндпоинты приложения, `delete-account`, `my_pcs`).

Общие правила этого документа:
- Почта аккаунта показывается **только на iPhone** (экран Account). На ПК — нигде (E5-4).
- Ни цен, ни домена, ни названий поставщиков сервера и оплаты. Заглушки — `README.md`.
- Аккаунт не даёт доступа к ПК: доступ — только по QR-сопряжению. Это повторяется в текстах там, где пользователь может подумать иначе (My Devices, удаление).
- Числа лимитов в интерфейсе не пишем (20 ПК — параметр сервера).
- Кнопки «Пропустить» для аккаунта нет (`navigation.md`, раздел 5).

## 1. Цель и для кого

| Экран | Цель | Кто |
|-------|------|-----|
| A1 Вход / регистрация | создать аккаунт или войти: email+пароль, Sign in with Apple | новый пользователь (первый запуск, шаг 2) и тот, у кого истёк вход |
| A2 Подтверждение email | дождаться перехода по ссылке из письма, отправить письмо снова | после регистрации email+паролем |
| A3 Сброс пароля | восстановить доступ | забыл пароль |
| A4 Код 2FA | ввести код из приложения-аутентификатора при входе | пользователь с включённой 2FA |
| A5 Account | почта, тариф, переходы: My Devices, Безопасность, Pro, Выйти, Удалить аккаунт | вошедший пользователь |
| A6 Безопасность | смена пароля, включение/выключение 2FA, выход на всех устройствах | вошедший пользователь с паролем |
| A7 Одобрение входа ПК | два шага: сверить имя ПК и время → подтвердить | владелец, который начал вход в трее ПК |
| A8 My Devices | ПК аккаунта, удаление записи ПК; сопряжение этого iPhone с ПК | владелец |
| A9 Удаление аккаунта | Guideline 5.1.1(v): предупреждения, повторный вход, удаление | любой вошедший |
| A10 Pro (каркас) | Monthly/Yearly, «Восстановить покупки» | любой; покупка — E7 |

Все экраны открываются из More → Account (`MoreView`), A1–A4 — также из первого запуска (шаг 2). A7 открывается только из Account → «Войти на ПК»: вход ПК начинается в трее, push-уведомлений в продукте нет.

## 2. Структура, состояния, переходы

### 2.1 Карта потоков

```
More ─ Account (A5) ─┬─ [не вошёл] → A1 Вход/регистрация
                     ├─ My Devices (A8) ─ карточка ПК ─ «Удалить из аккаунта»
                     ├─ «Войти на ПК» (A7: шаг 1 → шаг 2 → готово)
                     ├─ Безопасность (A6) ─ пароль · 2FA · «Выйти на всех устройствах»
                     ├─ Pro (A10)
                     ├─ «Выйти»  (confirmationDialog)
                     └─ «Удалить аккаунт» (A9: предупреждение → повторный вход → удаление)

A1 ─ «Создать аккаунт» → форма → A2 (ждём письмо) → вошёл
   ─ «Войти» → форма → [2FA?] → A4 → вошёл
   ─ «Sign in with Apple» → системный лист → вошёл
   ─ «Забыли пароль?» → A3 → письмо → вход
```

### 2.2 A1 Вход / регистрация

```
┌──────────────────────────────┐
│ Создайте аккаунт Nexus PC    │ type.display (шаг 2 онбординга) / type.title (из More)
│ Аккаунт хранит список ваших  │ type.body, text.secondary
│ ПК и тариф. ...              │
│ ┌──────────────────────────┐ │
│ │  Sign in with Apple     │ │ SignInWithAppleButton, стиль .white, высота ≥ 44
│ └──────────────────────────┘ │
│ ──────── или ────────        │
│ [ Email                    ] │ TextField, .textContentType(.username), .emailAddress
│ [ Пароль              (👁) ] │ SecureField, .newPassword (регистрация) / .password (вход)
│  Не меньше 8 символов        │ подсказка только при регистрации, type.caption
│ [ Создать аккаунт ]          │ NeonButton (isLoading)
│  Уже есть аккаунт? Войти     │ переключатель режима, ссылка accent.primary
│  Забыли пароль?              │ только в режиме «Вход»
│  Условия · Конфиденциальность│ {TERMS_URL} {PRIVACY_URL}, type.caption
└──────────────────────────────┘
```

Кнопка Apple — первой и не меньше остальных (HIG «Sign in with Apple», Guideline 4.8). Значок «глаз» — кнопка показа пароля, ≥ 44 pt, VoiceOver «Показать пароль».

Состояния:

| Состояние | Что видно |
|-----------|-----------|
| обычное | кнопка «Создать аккаунт»/«Войти» выключена, пока email пуст или пароль пуст |
| загрузка | `NeonButton` в загрузке, поля выключены, Apple-кнопка выключена |
| ошибка входа | под полями `ErrorCard` без «Повторить»: `account.error.credentials` (неверная почта или пароль — одним текстом, не говорим, что именно) |
| почта уже занята (регистрация) | тот же нейтральный текст `account.error.signUpFailed` + ссылка «Войти» (не подтверждаем существование аккаунта) |
| слабый пароль | `account.error.weakPassword` под полем пароля |
| нет интернета | `ErrorCard` `onboarding.account.offline` (из `navigation.md`), кнопки выключены, «Повторить» |
| `rate.limited` | `error.rateLimited` (`error-texts.md`, раздел 4) |
| Apple отменён пользователем | ничего не показываем (возврат на A1) |
| Apple ошибка | `account.error.apple` |
| email не подтверждён (вход) | переход на A2 |

### 2.3 A2 Подтверждение email

```
┌──────────────────────────────┐
│        (envelope.badge)      │ декоративная иконка
│ Подтвердите почту            │ type.title
│ Мы отправили письмо на       │
│ name@example.com. Откройте   │ почта — type.headline, .textSelection
│ ссылку в письме на этом      │
│ iPhone.                      │
│ [ Я подтвердил(а) ]          │ NeonButton → повторная проверка
│  Отправить письмо снова      │ SecondaryButton; после нажатия выключена 60 с,
│                              │ подпись «Можно снова через 0:59»
│  Изменить почту              │ ссылка → A1 с заполненной почтой
└──────────────────────────────┘
```

Ссылка из письма открывает приложение (универсальная ссылка — домен после решения владельца, `{DOMAIN}`), вход завершается автоматически. Если пользователь открыл ссылку на другом устройстве — кнопка «Я подтвердил(а)» проверяет статус. Ещё не подтверждено → `Toast` `account.verify.notYet`.

### 2.4 A3 Сброс пароля

Шаг 1: поле email, `NeonButton` «Отправить ссылку». Ответ **всегда** одинаковый: «Если такой аккаунт есть, мы отправили письмо со ссылкой» (не раскрываем существование аккаунта). Шаг 2 (по ссылке из письма в приложении): «Новый пароль» + «Повторите пароль», `NeonButton` «Сохранить». Успех → вход выполнен, `Toast` `account.reset.done`, все прочие сессии завершаются (текст это говорит). Ссылка устарела → `account.reset.expired` + «Отправить снова».

Пользователь, у которого есть только Sign in with Apple, пароля не имеет: в A3 получает то же нейтральное письмо (содержание письма — E6/backend, не здесь).

### 2.5 A4 Код 2FA (TOTP, по желанию пользователя)

```
┌──────────────────────────────┐
│ Код подтверждения            │
│ Введите 6 цифр из приложения │
│ для кодов.                   │
│ [ _ _ _  _ _ _ ]             │ одно поле, .oneTimeCode, .numberPad, type.mono
│ [ Подтвердить ]              │ автоотправка при 6 цифрах
│  Нет доступа к приложению?   │ → пояснение: сброс 2FA через поддержку {SUPPORT_URL}
└──────────────────────────────┘
```

Неверный код → поле очищается, `account.error.totpInvalid`, VoiceOver-объявление. Резервных кодов в продукте нет — не обещаем их.

Включение 2FA (A6 → «Двухэтапная проверка»): шаг 1 — пояснение и «Продолжить»; шаг 2 — QR для приложения-аутентификатора + кнопка «Скопировать ключ» (ключ показывается `type.mono` по группам по 4, `.privacySensitive()`); шаг 3 — ввести 6 цифр для проверки; готово → `Toast`. Выключение — `confirmationDialog` + текущий код. 2FA доступна только пользователям с паролем; для входа только через Apple строка скрыта с пояснением `account.security.appleOnly` (Apple защищает вход сам).

### 2.6 A5 Account

```
┌──────────────────────────────┐
│ АККАУНТ                      │ SectionHeader
│ ┌──────────────────────────┐ │
│ │ name@example.com         │ │ type.headline; для Apple со скрытой почтой —
│ │ Вход через Apple         │ │ подпись способа входа (type.callout)
│ │ Тариф: Free   [Pro]      │ │ StatusPill/ProBadge; grace → строка pro.grace
│ └──────────────────────────┘ │
│ [ Войти на ПК            > ] │ NavRow → A7
│ [ Мои устройства         > ] │ NavRow → A8
│ [ Безопасность           > ] │ NavRow → A6
│ [ Nexus PC Pro           > ] │ NavRow → A10
│                              │
│ [ Выйти ]                    │ SecondaryButton
│ [ Удалить аккаунт ]          │ кнопка-текст state.danger, внизу экрана
└──────────────────────────────┘
```

Состояния:
- **не вошёл / вход истёк** — вместо карточки `EmptyState`: «Войдите в аккаунт», `NeonButton` «Войти» → A1. Free-управление при этом работает (`navigation.md`, раздел 5).
- **нет интернета** — карточка показывает последние известные почту и тариф с подписью `account.cachedAt`; `NavRow` «Войти на ПК», «Мои устройства», «Pro» выключены с `account.offline` (`ios-screens.md`).
- **тариф** по `entitlementState` (`ios-screens.md`, 3): `active` → «Pro»; `grace` → «Pro» + `pro.grace`; `expired`/`revoked`/`none` → «Free».
- **«Выйти»** — `confirmationDialog`: «Выйти из аккаунта? Управление сопряжёнными ПК продолжит работать.» Действие «Выйти» (обычное, не разрушительное). Выход не трогает сопряжения и вход ПК.

### 2.7 A6 Безопасность

Строки: «Изменить пароль» (текущий + новый ×2), «Двухэтапная проверка: Вкл/Выкл» (2.5), «Выйти на всех устройствах» (`confirmationDialog`, разрушительное; завершает входы аккаунта на других iPhone и сайте; **ПК не отвязывает** — так и написано). Отдельного списка сессий нет: сервер его не предоставляет клиенту; если владелец захочет список — НУЖНО РЕШЕНИЕ (раздел 7).

### 2.8 A7 Одобрение входа ПК (два шага)

Начало: пользователь в трее ПК выбирает «Войти в аккаунт», трей показывает 8-символьный код (E5-4) и ждёт. На iPhone: Account → «Войти на ПК».

```
Шаг 0 — ввод кода                    Шаг 1 — сверка (describe)
┌──────────────────────────────┐    ┌──────────────────────────────┐
│ Войти на ПК                  │    │ Это ваш ПК?                  │
│ Введите код, который показан │    │ ┌──────────────────────────┐ │
│ на ПК в окне «Аккаунт».      │    │ │ (desktopcomputer)        │ │
│ [ XXXX-XXXX ]                │    │ │ GAMING-PC                │ │ displayName, type.title
│ Код действует 5 минут.       │    │ │ Запрос: сегодня, 14:02   │ │ requestedAt, локальное время
│ [ Продолжить ]               │    │ └──────────────────────────┘ │
└──────────────────────────────┘    │ Сравните с именем на экране  │
                                    │ ПК. Не совпадает — нажмите   │
                                    │ «Это не мой ПК».             │
                                    │ [ Продолжить ]               │ NeonButton → шаг 2
                                    │  Это не мой ПК               │ SecondaryButton → отмена
                                    └──────────────────────────────┘
Шаг 2 — подтверждение (approve)      Готово
┌──────────────────────────────┐    ┌──────────────────────────────┐
│ Привязать GAMING-PC к        │    │ (checkmark.circle.fill)      │ state.success
│ вашему аккаунту?             │    │ Готово                       │
│ ⚠ Если этот ПК был привязан   │    │ GAMING-PC завершит вход сам  │
│ к другому аккаунту, он       │    │ через несколько секунд.      │
│ перейдёт в ваш, а прежний    │    │ [ Готово ]                   │
│ аккаунт потеряет к нему      │    └──────────────────────────────┘
│ доступ к Pro.                │ блок state.warning, иконка + текст
│ [ Привязать ПК ]             │ NeonButton
│  Отмена                      │
└──────────────────────────────┘
```

Правила:
- Поле кода: 8 символов, верхний регистр автоматически, дефис вставляется визуально после 4-го (`XXXX-XXXX`), `.autocorrectionDisabled`, `.textInputAutocapitalization(.characters)`, `type.mono`. Символы вне алфавита (0, O, 1, I, L) не принимаются: подсказка `account.pcLogin.codeHint`. Вставка из буфера поддерживается.
- Шаг 1 показывает **только** имя ПК и время запроса (из ответа describe). Время — относительно сегодня («сегодня, 14:02»), формат системы.
- Шаг 2 нельзя пройти, не пройдя шаг 1 (сервер так же отклоняет approve без describe).
- Предупреждение о перепривязке показывается **всегда** на шаге 2: describe не сообщает, привязан ли ПК к другому аккаунту (НУЖНО РЕШЕНИЕ, раздел 7). Текст написан условно («Если…»).
- Таймер 5 минут на iPhone не показывается (iPhone не знает момента выдачи кода точно); показан срок в подсказке шага 0 и время запроса на шаге 1.
- «Это не мой ПК» → экран `account.pcLogin.notMine`: «Ничего не привязано. Если вы не начинали вход, просто закройте этот экран.» Код не одобряется и сгорит сам.

Состояния и ошибки:

| Когда | Код | Что видно |
|-------|-----|-----------|
| шаг 0/1/2, запрос идёт | — | `NeonButton` в загрузке |
| неверный, истёкший, чужой, уже использованный код | `code.invalid` | `ErrorCard` `account.error.codeInvalid` («Код неверный или истёк. Запросите новый на ПК.»), поле очищается, возврат на шаг 0. Истёк и неверный **не различаем** — сервер отвечает одинаково |
| слишком много попыток | `rate.limited` | `error.rateLimited`, кнопка выключена до `retryAfter` |
| вход аккаунта истёк | `auth.required` | переход на A1, после входа — снова шаг 0 |
| нет интернета | — | `account.offline`, «Повторить» |
| у аккаунта максимум ПК | `pcs.limit` | возникает на ПК при завершении входа (после approve), iPhone его не получает и числа лимита не знает. Поэтому экран «Готово» **всегда** содержит строку `account.pcLogin.limitHint` (`type.caption`). На ПК — текст `account.error.pcsLimit` (E5-4). Код не тратится до конца срока: после удаления лишнего ПК вход завершится сам, если 5 минут не прошли |
| сервис недоступен | `internal` | `account.error.internal` |
| `bad.request` | `bad.request` | `account.error.badRequest` |

### 2.9 A8 My Devices

Объединяет две разные вещи, и экран их не смешивает в одной кнопке:
1. **ПК в аккаунте** (серверный список `my_pcs`: имя, дата привязки, последний вход ПК). Удаление записи = серверная отвязка (`pc-unlink`): ПК теряет Pro аккаунта, следующий вход в трее снова свободен.
2. **Сопряжение этого iPhone** (локальное: отпечаток, «Обновить адрес ПК по QR-коду», «Забыть ПК» — перенос с Home по `navigation.md`, строка 57).

```
┌──────────────────────────────┐
│ ПК В АККАУНТЕ                │ SectionHeader
│ ┌──────────────────────────┐ │
│ │ GAMING-PC   [Этот iPhone │ │ StatusPill «Сопряжён с этим iPhone»,
│ │             сопряжён]    │ │ если имя/pcId совпадает с локальным
│ │ Привязан: 3 нояб. 2026   │ │ type.callout
│ │ Последний вход: сегодня  │ │ last_seen_at; null → «—»
│ └──────────────────────────┘ │ нажатие → карточка ПК
│ ...                          │
│ Аккаунт не даёт доступа к ПК:│ type.caption, постоянная сноска
│ управлять ПК можно только    │
│ после сопряжения по QR-коду. │
└──────────────────────────────┘

Карточка ПК
┌──────────────────────────────┐
│ GAMING-PC                    │
│ [ Переименовать ]            │ SecondaryButton (my_pcs_update_own, 1–64 символа)
│ СОПРЯЖЕНИЕ С ЭТИМ iPHONE     │ только если сопряжён
│  Отпечаток ключа  AB12 …     │ type.mono
│ [ Обновить адрес по QR ]     │ существующий поток
│ [ Забыть ПК на этом iPhone ] │ существующий forgetButton, confirmationDialog
│ АККАУНТ                      │
│ [ Удалить из аккаунта ]      │ state.danger, confirmationDialog
└──────────────────────────────┘
```

Диалог «Удалить из аккаунта»: заголовок «Удалить GAMING-PC из аккаунта?», текст «ПК перестанет получать Pro этого аккаунта. Сопряжение с iPhone и бесплатные функции останутся. Чтобы снова привязать ПК, войдите в аккаунт в меню значка N на ПК.» Действие — разрушительное «Удалить». Подсказка в трее (E5-4): «Отвязать» в трее — локально; запись в аккаунте удаляется здесь.

Состояния: загрузка — `LoadingState`; пусто — `EmptyState` «В аккаунте пока нет ПК» + «Войдите в аккаунт на ПК: значок N → Аккаунт»; нет интернета — последний список с `account.cachedAt`, кнопки «Переименовать»/«Удалить» выключены; `pc.not_found` при удалении → `Toast` `account.error.pcNotFound`, строка исчезает; `rate.limited`, `internal` — как в `error-texts.md`. Удалённый ПК освобождает место в лимите (числа не пишем).

Сопоставление «Этот iPhone сопряжён»: по `pcId` (после E4 он выводится из ключа ПК и совпадает с сопряжением) — уточнит `code-architect` в E5-5; при отсутствии совпадения значок просто не показывается.

### 2.10 A9 Удаление аккаунта (Guideline 5.1.1(v))

Доступно: Account → «Удалить аккаунт» (внизу, всегда видно вошедшему). Также на сайте `{ACCOUNT_URL}` и через трей (переход на сайт, E5-4).

```
Шаг 1 — что удалится                 Шаг 2 — подтвердите, что это вы
┌──────────────────────────────┐    ┌──────────────────────────────┐
│ Удалить аккаунт?             │    │ Введите пароль               │
│ Будут удалены навсегда:      │    │ [ Пароль               ]     │ .password
│ • аккаунт и почта            │    │ [ код 2FA ] (если включена)  │
│ • список ПК в аккаунте       │    │ [ Удалить аккаунт ]          │ NeonButton .destructive
│ • тариф Pro на всех ПК       │    │  Отмена                      │
│ Останется: сопряжение iPhone │    └──────────────────────────────┘
│ с ПК и бесплатные функции.   │
│ [⚠ подписка, если есть]      │ блок state.warning (ниже)
│ [ Продолжить ]               │ NeonButton .destructive
│  Отмена                      │
└──────────────────────────────┘
Готово: «Аккаунт удалён» → выход → первый запуск, шаг 2 (сопряжения остаются).
```

Правила:
- Шаг 2 = повторный вход паролем (или кодом из письма, если сервер его предложит — `otp`) непосредственно перед удалением; сервер требует свежесть **300 с**. Приложение вызывает удаление сразу после успешного повторного входа, без лишних экранов. Если между шагами прошло больше 5 минут или сервер ответил `reauth.required` → поле пароля очищается, текст `account.error.reauthRequired`, пользователь вводит пароль снова.
- 2FA: если включена, после пароля запрашивается код (A4) — это часть повторного входа.
- **Пользователь только с Sign in with Apple — ЖДЁТ РЕШЕНИЯ ВЛАДЕЛЬЦА** (COMMERCIAL_PLAN раздел 0, OPEN; рекомендация — принимать свежий вход Apple 300 с). Проект экрана на случай одобрения: шаг 2 = кнопка Sign in with Apple «Подтвердить через Apple» вместо пароля, затем сразу удаление. Пока решения нет, сервер такой вход не принимает (`reauth.required`), и такие пользователи не смогут удалить аккаунт в приложении — это блокер для App Review, его нельзя выпускать без решения. Тексты `account.delete.apple.*` подготовлены (раздел 4).
- **Платная подписка (E7, текст-заглушка).** Если у аккаунта активная подписка: блок предупреждения на шаге 1. Для подписки из App Store — «Удаление аккаунта не отменяет подписку App Store. Отмените её в настройках Apple» + кнопка «Управление подписками» (системный лист `manageSubscriptionsSheet` / ссылка управления подписками Apple). Для подписки с сайта — «Подписка с сайта будет отменена при удалении аккаунта» (сервер отменяет у провайдера до удаления). Окончательные тексты — E7.
- Ошибки: `rate.limited` → `error.rateLimited` (сервер — 3 попытки в час); `internal` → `account.delete.failed` «Не удалось удалить аккаунт. Ваши данные уже удаляются — повторите через минуту.» с «Повторить» (повтор безопасен); `auth.required` → A1; нет интернета → `account.offline`, кнопка выключена.
- После успеха: локальный выход, очистка кэша аккаунта; сопряжения и ключ Secure Enclave не трогаем. VoiceOver-объявление «Аккаунт удалён».

### 2.11 A10 Nexus PC Pro (каркас)

```
┌──────────────────────────────┐
│ [Pro]  Nexus PC Pro          │ ProBadge + type.title
│ • Расширенная передача файлов│ список преимуществ — только CONFIRMED из
│ • RGB Sync                   │ COMMERCIAL_PLAN раздел 0 (Pro)
│ • Automation Studio          │
│ • Расширенный мониторинг     │
│ ┌────────────┐┌────────────┐ │ два варианта, выбор — рамка 2 pt (GlassCard выделенное)
│ │ Месяц      ││ Год        │ │ AX-размеры: один столбец
│ │{PRICE_MONTHLY}│{PRICE_YEARLY}│ │ цены только из StoreKit (displayPrice)
│ └────────────┘└────────────┘ │
│ {TRIAL}                      │ строка пробного периода, только если есть
│ [ Оформить ]                 │ NeonButton; до E7 — выключена с подписью
│                              │ «Покупка появится в следующем обновлении»
│  Восстановить покупки        │ SecondaryButton
│  Подписка продлевается       │ type.caption, обязательный текст автопродления
│  автоматически…              │
│  Условия · Конфиденциальность│ {TERMS_URL} {PRIVACY_URL}
└──────────────────────────────┘
```

Состояния: загрузка товаров — `LoadingState`; товары не загрузились — `ErrorCard` «Не удалось загрузить варианты подписки» + «Повторить»; уже Pro — вместо выбора строка «У вас Pro» + `Toast`-статус `grace` при необходимости + «Управление подпиской»; не владелец ПК — `pro.card.ownerHint` (`error-texts.md`, 3.2); нет интернета — `account.offline`, кнопки выключены. Пробный период и цены — заглушки до E7, в коде не хардкодятся (берутся из StoreKit). Покупка на iPhone не меняет права на ПК мгновенно: подпись под кнопкой после покупки «Pro появится на ПК в течение минуты» — текст E7.

Экран Pro не блокирует приложение и не появляется сам (без навязчивых окон); открывается из Account, `ProCard`, `LimitNotice`.

## 3. Компоненты и токены

Используются `GlassCard`, `NeonButton` (вкл. `.destructive`, `isLoading`), `SecondaryButton`, `NavRow`, `StatusPill`, `ProBadge`, `EmptyState`, `LoadingState`, `ErrorCard`, `Toast` из `foundations.md`. Новые:

| Компонент | Назначение | Состав | Состояния |
|-----------|-----------|--------|-----------|
| `FormField` | поле ввода почты/пароля/имени | подпись над полем (`type.callout`, видима всегда, не placeholder-only), поле на `bg.sunken`, `radius.control`, высота ≥ 44, текст ошибки под полем (`state.danger` + иконка) | обычное; фокус (рамка `accent.primary` 2 pt); ошибка; выключено |
| `CodeField` | код ПК (8 симв.) и TOTP (6 цифр) | одно поле `type.mono`, визуальные группы, без отдельных ячеек (VoiceOver читает целиком) | обычное; ошибка (очистка + объявление); загрузка |
| `WarningBlock` | предупреждения перепривязки и подписки | `exclamationmark.triangle.fill` `state.warning` + текст `type.body` на `bg.elevated`, рамка `state.warning` 1 pt | одно состояние |
| `PlanOption` | вариант подписки | `GlassCard` с названием, ценой StoreKit, подписью | обычное; выбрано (рамка 2 pt + `checkmark.circle.fill`, VoiceOver «выбрано»); выключено |

Анимации: переходы шагов A7/A9 — `motion.step` (Reduce Motion — затухание). Ошибки кода — без «тряски» поля (тряска не проходит Reduce Motion и не нужна: есть текст).

## 4. Тексты EN/RU

Коды сервера — `error-texts.md`, раздел 4 (не дублируются). `%@` — подстановка.

| Ключ | en | ru | Где |
|------|----|----|-----|
| `account.title` | Account | Аккаунт | A5 |
| `account.signIn` | Sign In | Войти | A1 |
| `account.signUp` | Create Account | Создать аккаунт | A1 |
| `account.or` | or | или | A1 |
| `account.email` | Email | Эл. почта | A1, A3 |
| `account.password` | Password | Пароль | A1, A9 |
| `account.password.new` | New password | Новый пароль | A3, A6 |
| `account.password.repeat` | Repeat password | Повторите пароль | A3, A6 |
| `account.password.hint` | At least 8 characters. | Не меньше 8 символов. | A1 |
| `account.password.show` | Show password | Показать пароль | VoiceOver |
| `account.password.hide` | Hide password | Скрыть пароль | VoiceOver |
| `account.haveAccount` | Already have an account? Sign In | Уже есть аккаунт? Войти | A1 |
| `account.noAccount` | New to Nexus PC? Create Account | Нет аккаунта? Создать | A1 |
| `account.forgot` | Forgot password? | Забыли пароль? | A1 |
| `account.legal` | Terms of Use · Privacy Policy | Условия использования · Конфиденциальность | A1, A10 |
| `account.error.credentials` | Email or password is incorrect. | Неверная почта или пароль. | A1 |
| `account.error.signUpFailed` | Couldn't create an account with this email. If you already have one, sign in. | Не удалось создать аккаунт с этой почтой. Если он у вас уже есть, войдите. | A1 |
| `account.error.weakPassword` | Choose a longer password. | Выберите пароль длиннее. | A1, A3 |
| `account.error.passwordMismatch` | Passwords don't match. | Пароли не совпадают. | A3, A6 |
| `account.error.apple` | Couldn't sign in with Apple. Try again. | Не удалось войти через Apple. Повторите. | A1 |
| `account.error.totpInvalid` | Wrong code. Enter the current code from your authenticator app. | Неверный код. Введите текущий код из приложения для кодов. | A4 |
| `account.verify.title` | Confirm your email | Подтвердите почту | A2 |
| `account.verify.body` | We sent an email to %@. Open the link in it on this iPhone. | Мы отправили письмо на %@. Откройте ссылку из него на этом iPhone. | A2 |
| `account.verify.done` | I've Confirmed | Я подтвердил(а) | A2 |
| `account.verify.resend` | Send Email Again | Отправить письмо снова | A2 |
| `account.verify.resendIn` | You can resend in %@ | Можно снова через %@ | A2 |
| `account.verify.change` | Change Email | Изменить почту | A2 |
| `account.verify.notYet` | Email not confirmed yet. Check your inbox and spam folder. | Почта ещё не подтверждена. Проверьте входящие и папку «Спам». | A2 |
| `account.reset.title` | Reset password | Сброс пароля | A3 |
| `account.reset.send` | Send Link | Отправить ссылку | A3 |
| `account.reset.sent` | If an account with this email exists, we sent a link to reset the password. | Если аккаунт с этой почтой есть, мы отправили ссылку для сброса пароля. | A3 |
| `account.reset.save` | Save Password | Сохранить пароль | A3 |
| `account.reset.done` | Password changed. You were signed out on other devices. | Пароль изменён. На других устройствах выполнен выход. | A3 |
| `account.reset.expired` | This link has expired. Request a new one. | Ссылка устарела. Запросите новую. | A3 |
| `account.totp.title` | Verification code | Код подтверждения | A4 |
| `account.totp.body` | Enter the 6-digit code from your authenticator app. | Введите 6 цифр из приложения для кодов. | A4 |
| `account.totp.confirm` | Confirm | Подтвердить | A4 |
| `account.totp.lost` | Can't access the app? | Нет доступа к приложению? | A4 |
| `account.totp.lostBody` | Contact support to turn off two-step verification: %@ | Обратитесь в поддержку, чтобы отключить двухэтапную проверку: %@ | A4 ({SUPPORT_URL}) |
| `account.plan` | Plan: %@ | Тариф: %@ | A5 |
| `account.method.apple` | Signed in with Apple | Вход через Apple | A5 |
| `account.method.email` | Signed in with email | Вход по почте | A5 |
| `account.cachedAt` | Last updated %@ | Обновлено %@ | A5, A8 |
| `account.signedOut.title` | Sign in to your account | Войдите в аккаунт | A5 |
| `account.signedOut.body` | Your paired PCs keep working with free features. | Сопряжённые ПК продолжают работать с бесплатными функциями. | A5 |
| `account.pcLogin.row` | Sign In on PC | Войти на ПК | A5 |
| `account.security.row` | Security | Безопасность | A5 |
| `account.pro.row` | Nexus PC Pro | Nexus PC Pro | A5 |
| `account.signOut` | Sign Out | Выйти | A5 |
| `account.signOut.confirm` | Sign out of your account? Paired PCs will keep working. | Выйти из аккаунта? Сопряжённые ПК продолжат работать. | A5 |
| `account.delete.row` | Delete Account | Удалить аккаунт | A5 |
| `account.security.password` | Change Password | Изменить пароль | A6 |
| `account.security.password.current` | Current password | Текущий пароль | A6 |
| `account.security.totp` | Two-Step Verification | Двухэтапная проверка | A6 |
| `account.security.totp.on` | On | Вкл. | A6 |
| `account.security.totp.off` | Off | Выкл. | A6 |
| `account.security.appleOnly` | You sign in with Apple. Apple protects this sign-in. | Вы входите через Apple. Этот вход защищает Apple. | A6 |
| `account.totp.setup.intro` | Add a second step when signing in: a code from an authenticator app. | Добавьте второй шаг при входе — код из приложения для кодов. | A6 |
| `account.totp.setup.scan` | Scan this QR code in your authenticator app, or copy the key. | Отсканируйте QR-код в приложении для кодов или скопируйте ключ. | A6 |
| `account.totp.setup.copy` | Copy Key | Скопировать ключ | A6 |
| `account.totp.setup.verify` | Enter the code from the app to finish. | Введите код из приложения, чтобы завершить. | A6 |
| `account.totp.setup.done` | Two-step verification is on. | Двухэтапная проверка включена. | A6 |
| `account.totp.disable.confirm` | Turn off two-step verification? | Выключить двухэтапную проверку? | A6 |
| `account.security.signOutAll` | Sign Out Everywhere | Выйти на всех устройствах | A6 |
| `account.security.signOutAll.confirm` | Sign out on all iPhones and the website? Your PCs stay linked. | Выйти на всех iPhone и на сайте? ПК останутся привязаны. | A6 |
| `account.pcLogin.title` | Sign In on PC | Войти на ПК | A7 |
| `account.pcLogin.enter` | Enter the code shown on the PC in the Account window. | Введите код, который показан на ПК в окне «Аккаунт». | A7 шаг 0 |
| `account.pcLogin.expiry` | The code works for 5 minutes. | Код действует 5 минут. | A7 шаг 0 |
| `account.pcLogin.codeHint` | The code has letters and digits. It doesn't use 0, O, 1, I or L. | В коде буквы и цифры. В нём нет 0, O, 1, I и L. | A7 шаг 0 |
| `account.pcLogin.continue` | Continue | Продолжить | A7 |
| `account.pcLogin.check.title` | Is this your PC? | Это ваш ПК? | A7 шаг 1 |
| `account.pcLogin.check.requested` | Requested: %@ | Запрос: %@ | A7 шаг 1 |
| `account.pcLogin.check.body` | Compare with the name on the PC screen. If it doesn't match, tap "Not My PC". | Сравните с именем на экране ПК. Если не совпадает, нажмите «Это не мой ПК». | A7 шаг 1 |
| `account.pcLogin.notMineButton` | Not My PC | Это не мой ПК | A7 шаг 1 |
| `account.pcLogin.notMine` | Nothing was linked. If you didn't start this sign-in, just close this screen. | Ничего не привязано. Если вы не начинали вход, просто закройте этот экран. | A7 |
| `account.pcLogin.confirm.title` | Link %@ to your account? | Привязать %@ к вашему аккаунту? | A7 шаг 2 |
| `account.pcLogin.confirm.rebind` | If this PC was linked to another account, it will move to yours, and that account will lose Pro on it. | Если этот ПК был привязан к другому аккаунту, он перейдёт в ваш, а прежний аккаунт потеряет на нём Pro. | A7 шаг 2 |
| `account.pcLogin.confirm.button` | Link PC | Привязать ПК | A7 шаг 2 |
| `account.pcLogin.done.title` | Done | Готово | A7 |
| `account.pcLogin.done.body` | %@ will finish signing in by itself in a few seconds. | %@ сам завершит вход через несколько секунд. | A7 |
| `account.pcLogin.limitHint` | If the PC says your account has no room, remove an old PC in My Devices and sign in again. | Если ПК сообщит, что в аккаунте нет места, удалите старый ПК в «Моих устройствах» и войдите снова. | A7 готово |
| `devices.section.account` | PCs in Account | ПК в аккаунте | A8 |
| `devices.pairedHere` | Paired with this iPhone | Сопряжён с этим iPhone | A8 |
| `devices.added` | Linked: %@ | Привязан: %@ | A8 |
| `devices.lastSeen` | Last sign-in: %@ | Последний вход: %@ | A8 |
| `devices.noAccess` | An account doesn't give access to a PC. You can control a PC only after pairing with its QR code. | Аккаунт не даёт доступа к ПК. Управлять ПК можно только после сопряжения по QR-коду. | A8 |
| `devices.empty.title` | No PCs in your account yet | В аккаунте пока нет ПК | A8 |
| `devices.empty.body` | Sign in on the PC: N icon → Account. | Войдите на ПК: значок N → Аккаунт. | A8 |
| `devices.rename` | Rename | Переименовать | A8 |
| `devices.section.pairing` | Pairing with this iPhone | Сопряжение с этим iPhone | A8 |
| `devices.section.accountActions` | Account | Аккаунт | A8 |
| `devices.remove` | Remove from Account | Удалить из аккаунта | A8 |
| `devices.remove.title` | Remove %@ from your account? | Удалить %@ из аккаунта? | A8 |
| `devices.remove.body` | The PC will stop getting this account's Pro. Pairing with iPhone and free features stay. To link it again, sign in from the N icon menu on the PC. | ПК перестанет получать Pro этого аккаунта. Сопряжение с iPhone и бесплатные функции останутся. Чтобы снова привязать ПК, войдите в аккаунт в меню значка N на ПК. | A8 |
| `devices.remove.action` | Remove | Удалить | A8 |
| `devices.removed` | PC removed from account. | ПК удалён из аккаунта. | A8 Toast |
| `account.delete.title` | Delete account? | Удалить аккаунт? | A9 шаг 1 |
| `account.delete.list` | These will be deleted permanently: your account and email, the list of PCs in it, and Pro on all PCs. | Будут удалены навсегда: аккаунт и почта, список ПК в нём и Pro на всех ПК. | A9 шаг 1 |
| `account.delete.keeps` | Pairing between iPhone and PCs and free features will stay. | Сопряжение iPhone с ПК и бесплатные функции останутся. | A9 шаг 1 |
| `account.delete.continue` | Continue | Продолжить | A9 |
| `account.delete.reauth.title` | Confirm it's you | Подтвердите, что это вы | A9 шаг 2 |
| `account.delete.reauth.body` | Enter your password to delete the account. | Введите пароль, чтобы удалить аккаунт. | A9 шаг 2 |
| `account.delete.button` | Delete Account | Удалить аккаунт | A9 шаг 2 |
| `account.delete.done` | Account deleted. | Аккаунт удалён. | A9 |
| `account.delete.failed` | Couldn't finish deleting the account. Your data is already being removed — try again in a minute. | Не удалось завершить удаление. Ваши данные уже удаляются — повторите через минуту. | A9 |
| `account.delete.apple.body` | Confirm with Apple to delete the account. | Подтвердите через Apple, чтобы удалить аккаунт. | A9 шаг 2 (ждёт решения владельца) |
| `account.delete.apple.button` | Confirm with Apple | Подтвердить через Apple | A9 шаг 2 (ждёт решения владельца) |
| `account.delete.sub.appstore` | Deleting the account doesn't cancel your App Store subscription. Cancel it in Apple settings. | Удаление аккаунта не отменяет подписку App Store. Отмените её в настройках Apple. | A9 (заглушка до E7) |
| `account.delete.sub.manage` | Manage Subscriptions | Управление подписками | A9, A10 |
| `account.delete.sub.web` | Your website subscription will be canceled when the account is deleted. | Подписка с сайта будет отменена при удалении аккаунта. | A9 (заглушка до E7) |
| `pro.title` | Nexus PC Pro | Nexus PC Pro | A10 |
| `pro.feature.files.title` | Extended file transfer | Расширенная передача файлов | A10 |
| `pro.feature.rgb.title` | RGB Sync | RGB Sync | A10 |
| `pro.feature.automations.title` | Automation Studio | Automation Studio | A10 |
| `pro.feature.monitoring` | Extended monitoring | Расширенный мониторинг | A10 |
| `pro.plan.monthly` | Monthly | Месяц | A10 |
| `pro.plan.yearly` | Yearly | Год | A10 |
| `pro.plan.selected` | Selected | Выбрано | VoiceOver |
| `pro.subscribe` | Subscribe | Оформить | A10 |
| `pro.subscribe.soon` | Purchase will be available in a future update. | Покупка появится в следующем обновлении. | A10 до E7 |
| `pro.restore` | Restore Purchases | Восстановить покупки | A10 |
| `pro.renewal` | The subscription renews automatically until you cancel it in Apple settings. | Подписка продлевается автоматически, пока вы не отмените её в настройках Apple. | A10 (окончательно — E7) |
| `pro.active` | You have Pro | У вас Pro | A10 |
| `pro.loadFailed` | Couldn't load subscription options. | Не удалось загрузить варианты подписки. | A10 |

`{TRIAL}`, `{PRICE_MONTHLY}`, `{PRICE_YEARLY}` — не ключи, а значения из StoreKit (E7).

## 5. Доступность

- Каждое поле — видимая подпись + `accessibilityLabel` = подпись; ошибка поля добавляется в `accessibilityValue` и объявляется (`AccessibilityNotification.Announcement`).
- Порядок фокуса A1: заголовок → пояснение → Sign in with Apple → почта → пароль → показать пароль → главная кнопка → переключатель режима → «Забыли пароль?» → ссылки. A7: заголовок шага → карточка ПК (одним элементом: «GAMING-PC, запрос сегодня в 14:02») → пояснение → предупреждение → главная кнопка → отмена.
- `CodeField`: VoiceOver читает код по символам (`.accessibilityTextContentType`/ `speechSpellsOutCharacters`), не как слово. TOTP — `.oneTimeCode` для автоподстановки.
- Предупреждения (`WarningBlock`) — иконка + текст, цвет не единственный носитель смысла; `.accessibilityElement(children: .combine)`, префикс «Внимание».
- Dynamic Type до AX5: варианты Pro и кнопки — в один столбец; длинные русские строки (`account.delete.list`) переносятся, `lineLimit(nil)`; список удаляемого — маркированный `VStack`, не одна строка.
- Разрушительные действия: `role: .destructive`, `confirmationDialog`, VoiceOver читает «Удалить аккаунт, кнопка, разрушительное действие» (системно).
- Области касания ≥ 44 pt, в т. ч. «Забыли пароль?», «Отправить письмо снова», ссылки условий.
- Пароли и ключ TOTP — `.privacySensitive()`; экран A6 с ключом скрывается в переключателе приложений.
- Live Activity / Dynamic Island здесь не используются (кратковременные действия).

## 6. ЗАДАНИЯ

```
ЗАДАНИЕ E6-ACC-1: экраны аккаунта iOS (вход, регистрация, подтверждение почты, сброс, 2FA, Account, Безопасность)
Исполнитель: ios-developer
Файлы: ios/NexusPC/Views/Account/** (новые), ios/NexusPC/Views/MoreView.swift, ios/NexusPC/Views/DesignSystem/** (FormField, CodeField, WarningBlock), каталог строк iOS
Сделать: по docs/design/ios-account.md разделы 2.1–2.7, 3, 4, 5: A1–A6; Sign in with Apple первой кнопкой; нейтральные тексты ошибок входа/регистрации/сброса; почта только на экране Account; выход не трогает сопряжения; тексты из раздела 4.
Не трогать: протокол, сопряжение и Secure Enclave, Bundle ID и подпись; домены и цены не хардкодить ({DOMAIN}, {TERMS_URL}, {PRIVACY_URL}, {SUPPORT_URL} — конфигурация).
Критерии приёмки: все состояния таблицы 2.2 воспроизводимы; нет кнопки пропуска аккаунта в первом запуске; Dynamic Type AX5 без обрезки; VoiceOver порядок по разделу 5.
Тесты: unit-тесты NexusKit для нормализации и проверки форм (почта, пароль, совпадение), CI ios.yml.
Зависит от: E5-5, решение по серверу аккаунтов в облаке (боевые тесты)
```

```
ЗАДАНИЕ E6-ACC-2: одобрение входа ПК (A7) и My Devices (A8)
Исполнитель: ios-developer
Файлы: ios/NexusPC/Views/Account/** , ios/NexusPC/Views/HomeView.swift (перенос отпечатка, «Обновить адрес по QR», forgetButton в A8), ios/NexusKit (клиент pc-login-describe/approve, pc-unlink, чтение и переименование my_pcs)
Сделать: по docs/design/ios-account.md 2.8–2.9: шаг 0 ввод кода (нормализация алфавита API), шаг 1 describe — только имя ПК и время, шаг 2 approve с предупреждением о перепривязке всегда; ошибки code.invalid / rate.limited / auth.required / internal по error-texts.md; строка limitHint на экране «Готово»; My Devices — список, переименование, «Удалить из аккаунта» (pc-unlink) с диалогом; локальные действия сопряжения — отдельной секцией.
Не трогать: формат QR и сопряжения, pinning; шаг 2 недоступен без шага 1.
Критерии приёмки: два шага одобрения; истёк и неверный код — один текст; ни pcId, ни ключи не показываются; после переноса с Home функции сопряжения работают как прежде.
Тесты: unit-тесты нормализации кода и разбора ответов (фикстуры ответов describe/approve/unlink), CI ios.yml.
Зависит от: E6-ACC-1
```

```
ЗАДАНИЕ E6-ACC-3: удаление аккаунта (A9) и каркас Pro (A10)
Исполнитель: ios-developer
Файлы: ios/NexusPC/Views/Account/**, каталог строк iOS
Сделать: по docs/design/ios-account.md 2.10–2.11: шаг 1 — что удалится/останется; шаг 2 — повторный вход паролем (+2FA) и сразу delete-account; reauth.required → повторный ввод; ошибки rate.limited/internal; после успеха — выход без удаления сопряжений. Вариант Apple — только после решения владельца. A10 — каркас без покупки: кнопка «Оформить» выключена с pro.subscribe.soon; «Восстановить покупки» видна; цены и пробный период только из StoreKit (до E7 — не показывать).
Не трогать: StoreKit-покупку (E7), цены, ключи Secure Enclave.
Критерии приёмки: удаление доступно в приложении в 2 шага; свежесть 300 с соблюдается вызовом сразу после повторного входа; на экране Pro нет конкретных цен.
Тесты: unit-тест порядка вызовов (reauth → delete) с подставным клиентом, CI ios.yml.
Зависит от: E6-ACC-1; вариант Apple — решение владельца (OPEN, COMMERCIAL_PLAN раздел 0)
```

## 7. Нужны решения

- **НУЖНО РЕШЕНИЕ (владелец):** повторный вход через Apple для удаления аккаунта (OPEN). Без него пользователи только с Apple не смогут удалить аккаунт в приложении — блокер App Review 5.1.1(v).
- **НУЖНО РЕШЕНИЕ (протокол, code-architect E5-5):** describe не сообщает, привязан ли ПК к другому аккаунту; поэтому предупреждение о перепривязке показывается всегда и условно. Если нужно точное предупреждение — аддитивное поле в ответе describe (без раскрытия чужого аккаунта).
- **НУЖНО РЕШЕНИЕ (протокол):** `pcs.limit` приходит только ПК при consume; iPhone узнаёт об этом лишь через подсказку. Вариант — проверка лимита уже в approve.
- **НУЖНО РЕШЕНИЕ (владелец):** список активных сессий аккаунта не проектировался (нет серверной поддержки); сейчас — «Выйти на всех устройствах».
- Новые заглушки `{TERMS_URL}`, `{PRIVACY_URL}`, `{DOMAIN}` добавлены в `README.md`.
