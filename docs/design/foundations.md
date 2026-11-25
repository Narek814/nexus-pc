# Дизайн-система Nexus PC (iOS, с общими правилами для Windows)

Задание E5-1. Основа — `ios/NexusPC/Views/DesignSystem/Theme.swift` (`Theme`, `GlassCard`, `NeonButton`) и `ARCHITECTURE.md` раздел 7. Связанные документы: `navigation.md`, `README.md`.

## 1. Цель

Единый набор токенов и компонентов, чтобы: (1) экраны выглядели одинаково и не как отладочная версия; (2) светлая тема добавлялась сменой значений токенов, без правки экранов; (3) все состояния (загрузка, ошибка, нет разрешения, ПК не в сети, нужен Pro) показывались одними компонентами.

Главное правило: **во View нет литералов цвета, шрифта, отступа.** Только семантические токены. Сейчас нарушается, например `HomeView.swift:148` (`.font(.system(size: 34…))`), `HomeView.swift:107` (`.padding(20)`).

## 2. Токены цвета

Семантические имена описывают роль, а не оттенок. В коде — `Color("…")` из Asset Catalog с вариантами Any/Dark (сейчас обе = тёмные значения) или `Theme.Color.*`. Светлая тема позже = заполнить столбец «light» без изменения экранов. До светлой темы приложение принудительно тёмное (`.preferredColorScheme(.dark)`) — как сейчас.

| Токен | dark (сейчас) | light (позже) | Откуда / роль |
|-------|---------------|---------------|---------------|
| `bg.base` | #07070B | заполнить | `Theme.background`, фон экрана |
| `bg.elevated` | #16161E | заполнить | `Theme.surface`, карточки при Reduce Transparency, плашки |
| `bg.sunken` | #0E0E14 | заполнить | поля ввода, область тачпада |
| `text.primary` | #EBEBF5 | заполнить | `Theme.textPrimary` |
| `text.secondary` | #9696AA | заполнить | `Theme.textMuted`, подписи |
| `text.onAccent` | #07070B | заполнить | текст и иконки на градиенте кнопки (новое, см. контраст) |
| `accent.primary` | #9B5CFF | заполнить | `Theme.violet`, ссылки, выделение |
| `accent.secondary` | #00DCFF | заполнить | `Theme.cyan`, второстепенный акцент |
| `accent.gradient` | primary → secondary, ↘ | заполнить | `Theme.accent`, кнопки, рамка карточки |
| `border.card` | accent.gradient, 60 % | заполнить | рамка `GlassCard` |
| `border.subtle` | #FFFFFF 12 % | заполнить | разделители |
| `state.success` | #34D399 | заполнить | «ПК готов», включено |
| `state.warning` | #FBBF24 | заполнить | «нет разрешения», ограничение |
| `state.danger` | #F87171 | заполнить | ошибки, разрушительные действия |
| `state.offline` | = text.secondary | заполнить | «ПК не в сети» |
| `state.pro` | accent.gradient | заполнить | значок «Pro» |
| `glow.accent` | accent.primary 55 % | нет (в светлой теме свечения нет, только тень) | тень `NeonButton` |

Цвет никогда не единственный носитель смысла: статус всегда дублируется текстом и иконкой (`StatusPill`).

### Контраст (WCAG AA)

Расчёт по относительной яркости WCAG 2.2.

| Пара | Отношение | Итог |
|------|-----------|------|
| text.primary на bg.base | ≈ 16:1 | AA |
| text.secondary на bg.base | ≈ 6.9:1 | AA |
| text.secondary на bg.elevated | ≈ 6.2:1 | AA |
| accent.primary (текст-ссылка) на bg.base | ≈ 5.1:1 | AA |
| **text.primary на accent.gradient (сейчас в `NeonButton`, `Theme.swift:54-55`)** | ≈ 3.3:1 на фиолетовом, ≈ 1.4:1 на голубом | **не проходит** |
| text.onAccent (#07070B) на accent.gradient | ≈ 5.1:1 на фиолетовом, ≈ 12:1 на голубом | AA |

Вывод: в `NeonButton` текст и иконку перевести на `text.onAccent`. Материал `ultraThinMaterial` в `GlassCard` над фоном bg.base даёт почти тот же тёмный фон; над картинкой экрана ПК карточки не используются.

## 3. Типографика

Только текстовые стили iOS — масштабируются с Dynamic Type до AX5. Фиксированные размеры запрещены; если нужен особый шрифт — `Font.system(.largeTitle, design: .rounded, weight: .heavy)` (стиль, а не размер).

| Токен | Стиль iOS | Где |
|-------|-----------|-----|
| `type.display` | `.largeTitle`, rounded, heavy | имя ПК на Home, заголовок шага онбординга (вместо `HomeView.swift:148`) |
| `type.title` | `.title2`, semibold | заголовки экранов внутри контента |
| `type.headline` | `.headline` | заголовок карточки, текст кнопки |
| `type.body` | `.body` | основной текст |
| `type.callout` | `.subheadline` | вторичный текст, плашки |
| `type.caption` | `.caption` | подписи метрик |
| `type.section` | `.footnote`, semibold, uppercase, tracking 0.5 | `SectionHeader` |
| `type.metric` | `.title3`, semibold, `.monospacedDigit()` | цифры мониторинга (SF Pro с моноширинными цифрами) |
| `type.mono` | `.body`, monospaced | отпечаток ключа, код сверки |

Правила Dynamic Type:
- Ряды из 2–4 плиток (`PowerTile`, метрики) при `dynamicTypeSize.isAccessibilitySize` перестраиваются в один столбец (`ViewThatFits` или `AnyLayout`).
- Текст не обрезается: `lineLimit(nil)` у заголовков и сообщений; `minimumScaleFactor` не применяется к основному тексту (только к цифрам метрик, не ниже 0.8).
- Иконки рядом с текстом — через `Label` или `@ScaledMetric` размер.

## 4. Отступы, радиусы, размеры, свечение

| Токен | Значение | Применение |
|-------|----------|-----------|
| `space.xs` | 4 | иконка–текст внутри плашки |
| `space.s` | 8 | между строками карточки |
| `space.m` | 12 | между элементами в карточке |
| `space.l` | 16 | внутренний отступ кнопки, между карточками в списке |
| `space.xl` | 20 | отступ экрана, внутренний отступ `GlassCard` (сейчас 20) |
| `space.xxl` | 32 | между крупными секциями |
| `radius.card` | 20, continuous | `GlassCard` (`Theme.cornerRadius`) |
| `radius.control` | 16, continuous | `NeonButton`, плитки |
| `radius.pill` | капсула | `StatusPill`, `Toast`, значок Pro |
| `size.hit` | ≥ 44×44 pt | любая область касания (HIG) |
| `stroke.card` | 1 pt | рамка карточки |
| `glow.radius` | 14 (Reduce Motion: 8, статично) | тень `NeonButton` |

Отступы масштабируются `@ScaledMetric` только там, где содержимое — текст (внутренние отступы карточек); отступы экрана постоянные.

## 5. Анимации

| Токен | Обычно | Reduce Motion |
|-------|--------|---------------|
| `motion.standard` | `.snappy` / easeInOut 0.25 с | без анимации или затухание 0.15 с |
| `motion.toast` | появление сверху + затухание (`HomeView.swift:120`) | только затухание |
| `motion.step` | сдвиг шага онбординга | перекрёстное затухание |
| `motion.press` | масштаб 0.97 при нажатии | только смена прозрачности фона |
| `motion.glow` | статичное свечение (пульсации нет) | свечение меньше (8) |
| `motion.loading` | системный `ProgressView` | системный (iOS сама учитывает) |

Reduce Transparency: `GlassCard` уже переходит на `bg.elevated` (`Theme.swift:31`) — сохранить и применить ко всем плашкам. Повышенный контраст (`colorSchemeContrast == .increased`): рамка карточки 2 pt, 100 % непрозрачности.

## 6. Компоненты

Для каждого — состояния. «Выключено» = `.disabled(true)`: прозрачность 40 %, VoiceOver читает «недоступно». «Нажато» = `motion.press`.

### 6.1 Существующие (изменить)

| Компонент | Сейчас | Состояния | Изменения |
|-----------|--------|-----------|-----------|
| `GlassCard` | `Theme.swift:17` | обычное; нажатое (если в `NavigationLink`); выключенное; выделенное (рамка 2 pt) | токены `space.xl`, `radius.card`, `border.card` |
| `NeonButton` | `Theme.swift:42` | обычное; нажатое; выключенное (без свечения); загрузка (`ProgressView` вместо иконки, текст сохраняется, кнопка не нажимается, VoiceOver «… выполняется»); разрушительный вариант `role: .destructive` (фон `state.danger`) | `text.onAccent`; `title: LocalizedStringResource`; параметр `isLoading`; высота ≥ 44 |
| `StatusPill` | `HomeView.swift:440` | готов (`state.success`, `checkmark.circle.fill`); подключение (`ProgressView`); не в сети (`state.offline`, `wifi.slash`); ограничено (`state.warning`, `exclamationmark.triangle.fill`) | иконка + текст всегда; в DesignSystem |
| `PowerTile` | `HomeView.swift:460` | обычное; нажатое; выключенное (ПК не в сети); загрузка; подтверждение (`confirmationDialog` для выключения/перезагрузки — есть) | ≥ 44 pt; VoiceOver = название действия |
| `SectionHeader` | `SectionTitle`, `HomeView.swift:424` | — ; вариант с кнопкой «Все» справа | `type.section`, `.isHeader` |
| `MetricGauge` | `RingGauge`, `MonitoringView.swift:254` | значение; нет данных («—», «Недоступно»); загрузка | уже есть `accessibilityLabel` (`MonitoringView.swift:279`) — сохранить формат «CPU: 34 %» |
| `Toast` | `banner`, `HomeView.swift:111` | информация; успех; ошибка (иконка `state.danger`) | объявление VoiceOver (`AccessibilityNotification.Announcement`); показ 3 с, при VoiceOver — 6 с |

### 6.2 Новые

| Компонент | Назначение | Структура | Состояния |
|-----------|-----------|-----------|-----------|
| `NavRow` | строка-переход в корнях Control/PC/More | `GlassCard`: иконка, название, необязательный значок (Pro / нет доступа), `chevron.right` | обычное; нажатое; с значком «Pro»; с значком «Нужно разрешение» (`lock.fill`, `state.warning`) — переход всё равно открывает раздел с карточкой |
| `SecondaryButton` | второстепенное действие | рамка `border.card`, текст `accent.primary` | обычное; нажатое; выключенное; загрузка |
| `EmptyState` | нет данных или ПК не подключён | иконка (крупная, декоративная), заголовок, пояснение, 0–1 `NeonButton` | варианты: «ПК не подключён» → «Подключить ПК»; «Пусто» (нет профилей/автоматизаций) → действие создания, если есть |
| `LoadingState` | первая загрузка раздела | `ProgressView` + подпись «Загрузка…» | при задержке > 10 с подпись меняется на «Это занимает дольше обычного» и появляется «Повторить» |
| `ErrorCard` | ошибка запроса | `exclamationmark.triangle.fill` (`state.danger`), заголовок, текст по коду ошибки (тексты — E5-2), «Повторить» | обычное; повтор выполняется (кнопка в загрузке) |
| `OfflineCard` | ПК не в сети / нет связи | `wifi.slash`, «ПК не в сети», пояснение «Проверьте, что ПК включён и iPhone в той же сети Wi-Fi», «Повторить»; если есть WoL — «Включить ПК» | заменяет содержимое раздела; на Home — внутри карточки статуса |
| `PermissionCard` | «недоступно — включите на ПК» | `lock.fill` (`state.warning`), заголовок «Нужно разрешение на ПК», путь «Включите на ПК: значок N → Устройства и разрешения → {permission}», «Проверить снова» | обычное; проверка выполняется. Без GUID, без PowerShell (заменяет `AutomationsView.swift:608`, `TouchpadView.swift:239`, `FilesView.swift:457`) |
| `TrayMissingCard` | агент работает, а трей не запущен (`HomeView.swift:189`) | `exclamationmark.triangle`, «На ПК не запущено приложение Nexus PC у часов. Программы и блокировка недоступны.» | — |
| `ProCard` | «нужен Pro» | значок `ProBadge`, заголовок «Доступно в Pro», одна строка о функции, `NeonButton` «Подробнее о Pro» (ведёт на экран Pro, E5-3) | обычное; нет связи с сервером (кнопка выключена, пояснение). **Без цен.** Если ПК ответил `plan.required` — показывается та же карточка |
| `ProBadge` | метка Pro | капсула `state.pro`, текст «Pro» (`text.onAccent`) | — ; VoiceOver «Требуется Pro» |
| `LimitNotice` | `plan.limit` | `state.warning`, «Достигнут лимит бесплатного тарифа», «Подробнее о Pro» | без чисел (или `{LIMIT}` от сервера) |

Приоритет, если состояний несколько: ПК не в сети → нет разрешения → нужен Pro → ошибка → загрузка → пусто → контент.

### Схемы

```
PermissionCard                         ProCard
┌──────────────────────────────────┐   ┌──────────────────────────────────┐
│ [lock] Нужно разрешение на ПК    │   │ [Pro] Доступно в Pro              │
│ Включите на ПК: значок N →       │   │ Синхронизация подсветки и        │
│ Устройства и разрешения →        │   │ профили подсветки.               │
│ Мышь и клавиатура                │   │ [   Подробнее о Pro   ]          │
│ [ Проверить снова ]              │   └──────────────────────────────────┘
└──────────────────────────────────┘
```

## 7. Доступность (общие правила)

- Каждая кнопка-иконка без текста имеет `accessibilityLabel` (существующие примеры — `ScreenView.swift:103-107`, `TouchpadView.swift:177`). Декоративные иконки — `accessibilityHidden(true)`.
- Карточка-переход — один элемент VoiceOver (`.accessibilityElement(children: .combine)`), значок «Pro» / «нужно разрешение» входит в подпись: «Подсветка, требуется Pro, кнопка».
- Состояния читаются: `StatusPill` → «Статус: ПК готов»; загрузка кнопки → «Выключение, выполняется».
- Области касания ≥ 44×44 pt, включая кнопки поверх экрана ПК.
- Контраст — таблица раздела 2; при повышенном контрасте рамки 2 pt.
- Reduce Motion и Reduce Transparency — раздел 5.
- Dynamic Type до AX5 — раздел 3; каждый экран проверяется на AX5 и iPhone SE.
- Тачпад и экран ПК — прямое управление (`.allowsDirectInteraction`) с подсказкой VoiceOver, как сейчас.

## 8. Правила текстов EN/RU

1. Язык по умолчанию — язык системы; неподдерживаемый → английский (`developmentLanguage: en`). В Settings — «Как в системе / English / Русский» (COMMERCIAL_PLAN раздел 4). Виджеты, Siri, Live Activity и системные запросы разрешений следуют языку системы — это пояснение есть в Settings.
2. Без внутренних терминов: GUID, идентификаторы устройств, PowerShell и любые команды, «служба», «агент», «Tray», «pcId», имена модулей и команд протокола (`input.move`), этапы (P5, E4), коды ошибок как основной текст. Код ошибки допустим только в «Диагностике».
3. Говорим о том, что видит человек: «значок N у часов», «приложение Nexus PC на ПК». Путь по меню — через «→».
4. Ошибка = что случилось + что сделать. Пример: «ПК не в сети. Проверьте, что он включён и iPhone в той же сети Wi-Fi.»
5. Текст ошибки от ПК — только запасной; основной текст телефон выбирает по `code` (таблица — E5-2).
6. Не обещаем то, чего нет: без цен, лимитов числами, названий поставщиков, домена (заглушки — `README.md`).
7. EN — Title Case для кнопок и заголовков (HIG), RU — с заглавной только первое слово. Кавычки RU — «ёлочки».
8. RU обычно на 20–30 % длиннее: компоновка не рассчитывается на длину EN.
9. Каждый ключ есть на обоих языках; ключи по схеме `раздел.элемент.вариант`.

### Замены существующих текстов (примеры для E6)

| Сейчас (код) | Новый ключ | en | ru |
|--------------|-----------|----|----|
| «Command Center» (`HomeView.swift:150`) | — | (удалить) | (удалить) |
| «Nexus PC Tray не запущен — программы и блокировка недоступны.» (`HomeView.swift:189`) | `status.trayMissing` | The Nexus PC app isn't running on the PC. Apps and lock are unavailable. | На ПК не запущено приложение Nexus PC. Программы и блокировка недоступны. |
| «Lighting unavailable: another program on the PC has taken it over» (`RgbView.swift:60`, литерал EN в RU-интерфейсе) | `rgb.busy` | Lighting is used by another program on the PC. | Подсветкой управляет другая программа на ПК. |
| команда PowerShell с GUID (`TouchpadView.swift:239` и др.) | `permission.input.body` | Turn it on on the PC: N icon → Devices and Permissions → Mouse and Keyboard | Включите на ПК: значок N → Устройства и разрешения → Мышь и клавиатура |

### Тексты компонентов

| Ключ | en | ru | Где |
|------|----|----|-----|
| `status.ready` | PC Ready | ПК готов | `StatusPill` |
| `status.connecting` | Connecting… | Подключение… | `StatusPill` |
| `status.offline` | PC Offline | ПК не в сети | `StatusPill`, `OfflineCard` |
| `status.limited` | Limited | Ограничено | `StatusPill` |
| `offline.body` | Make sure the PC is on and your iPhone is on the same Wi-Fi network. | Проверьте, что ПК включён и iPhone в той же сети Wi-Fi. | `OfflineCard` |
| `offline.wake` | Turn On PC | Включить ПК | `OfflineCard` |
| `common.retry` | Try Again | Повторить | `ErrorCard`, `OfflineCard`, `LoadingState` |
| `common.loading` | Loading… | Загрузка… | `LoadingState` |
| `common.loadingSlow` | This is taking longer than usual. | Это занимает дольше обычного. | `LoadingState` |
| `common.cancel` | Cancel | Отмена | диалоги |
| `common.error.title` | Something Went Wrong | Что-то пошло не так | `ErrorCard` (общий) |
| `empty.noPC.title` | No PC Connected | ПК не подключён | `EmptyState` |
| `empty.noPC.body` | On the PC, click the N icon near the clock → Connect iPhone, then scan the QR code. | На ПК нажмите значок N у часов → «Подключить iPhone», затем отсканируйте QR-код. | `EmptyState` |
| `empty.noPC.action` | Connect PC | Подключить ПК | `EmptyState` |
| `permission.title` | Permission Needed on PC | Нужно разрешение на ПК | `PermissionCard` |
| `permission.path` | Turn it on on the PC: N icon → Devices and Permissions → %@ | Включите на ПК: значок N → Устройства и разрешения → %@ | `PermissionCard` |
| `permission.checkAgain` | Check Again | Проверить снова | `PermissionCard` |
| `permission.name.input` | Mouse and Keyboard | Мышь и клавиатура | `PermissionCard` |
| `permission.name.screen` | Screen | Экран | `PermissionCard` |
| `permission.name.files` | Files | Файлы | `PermissionCard` |
| `permission.name.clipboard` | Clipboard | Буфер обмена | `PermissionCard` |
| `permission.name.automation` | Automations | Автоматизации | `PermissionCard` |
| `permission.name.power` | Power | Питание | `PermissionCard` |
| `permission.name.apps` | Apps | Программы | `PermissionCard` |
| `permission.name.monitoring` | Monitoring | Мониторинг | `PermissionCard` |
| `permission.name.rgb` | Lighting | Подсветка | `PermissionCard` |
| `pro.badge` | Pro | Pro | `ProBadge` |
| `pro.badge.a11y` | Requires Pro | Требуется Pro | `ProBadge` VoiceOver |
| `pro.card.title` | Available in Pro | Доступно в Pro | `ProCard` |
| `pro.card.more` | Learn About Pro | Подробнее о Pro | `ProCard`, `LimitNotice` |
| `pro.card.offline` | Connect to the internet to see Pro options. | Подключитесь к интернету, чтобы увидеть варианты Pro. | `ProCard` |
| `limit.title` | Free Plan Limit Reached | Достигнут лимит бесплатного тарифа | `LimitNotice` |

Названия разрешений должны совпадать с пунктами окна трея «Устройства и разрешения» — сверка в E5-4.

## 9. Windows (общие правила)

Токены цвета iOS в WinForms не переносятся один в один: трей и окна следуют системной теме Windows и `windows/Nexus.Tray/Theme.cs`. Общими являются правила текстов (раздел 8), названия разрешений (раздел 8) и «значок N» как имя значка трея. Подробно — E5-4 (`windows.md`).

## 10. ЗАДАНИЯ

```
ЗАДАНИЕ E6-DS (черновик для E5-5): дизайн-система iOS
Исполнитель: ios-developer
Файлы: ios/NexusPC/Views/DesignSystem/*.swift (Theme.swift и новые файлы компонентов); Asset Catalog цветов; Localizable.xcstrings (ключи раздела 8)
Сделать: по docs/design/foundations.md: семантические токены (раздел 2) вместо Theme.violet/cyan/…, со старыми именами как устаревшими псевдонимами до переноса экранов; типографика стилями (раздел 3); токены отступов и радиусов (раздел 4); NeonButton — text.onAccent, isLoading, LocalizedStringResource; перенос StatusPill, PowerTile, SectionTitle→SectionHeader, RingGauge→MetricGauge, banner→Toast в DesignSystem; новые NavRow, SecondaryButton, EmptyState, LoadingState, ErrorCard, OfflineCard, PermissionCard, TrayMissingCard, ProCard, ProBadge, LimitNotice с состояниями раздела 6; правила Reduce Motion/Transparency/повышенного контраста.
Не трогать: NexusKit, протокол, логику PCController, виджеты (кроме общих токенов при необходимости).
Критерии приёмки: во View нет литералов цвета и размеров шрифта; контраст текста на кнопке ≥ 4.5:1; все компоненты имеют #Preview для каждого состояния, включая AX5 и Reduce Motion; иконки без текста имеют accessibilityLabel.
Тесты: сборка iOS в CI; проверка tools/check_l10n.py (когда появится).
Зависит от: E5-5
```
