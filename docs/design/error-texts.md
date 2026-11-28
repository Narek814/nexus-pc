# Тексты ошибок по кодам

Задание E5-2. Связанные документы: `foundations.md` (компоненты `ErrorCard`, `PermissionCard`, `ProCard`, `LimitNotice`, `OfflineCard`, правила текстов раздел 8), `ios-screens.md` (где показываются), источник кодов — `docs/API.md` («Порядок проверок и коды ошибок», «Сервер прав»).

## 1. Цель

Телефон выбирает текст ошибки **по `error.code`**, а не по `error.message` с ПК. Текст ПК (`message`) — только запасной: показывается, если код неизвестен этой версии приложения, и всегда виден в «Диагностике» вместе с кодом. Код ошибки в основном тексте не показывается (`foundations.md`, 8.2).

## 2. Правила выбора

1. Известный код → текст из таблиц ниже, компонент из столбца «Как».
2. Неизвестный код → `ErrorCard` с `common.error.title`; текст — `message` ПК, если он не пуст и не длиннее 200 символов, иначе `error.generic.body`. Код и `message` пишутся в журнал «Диагностики».
3. `plan.required` → не ошибка, а `ProCard` вместо содержимого или поверх действия. `plan.limit` → `LimitNotice`. Оба — без чисел; если `error.details.limit` пришёл от ПК, его можно подставить как `{LIMIT}` (`README.md`, заглушки), но тексты по умолчанию работают без него.
4. `auth.permissionDenied` → `PermissionCard` с названием разрешения раздела (`foundations.md`, `permission.name.*`). Какое разрешение — определяет раздел, в котором произошла ошибка (а не разбор текста ПК, как сейчас в `AutomationsView.swift:668-678`).
5. `rate.limited` с `error.details.retryAfter` → кнопка «Повторить» выключена на это время; секунды в текст не выводятся (обратный отсчёт допустим только на кнопке: «Повторить через %d с» — `error.rateLimited.retryIn`).
6. Разработческие коды (`protocol.*`, `command.unknown`, `command.invalidPayload`, `auth.clockSkew`, `auth.replay`, `internal`) показываются одним понятным текстом; подробности — только в «Диагностике».
7. «Как»: **Card** — `ErrorCard` вместо содержимого раздела; **Toast** — `Toast` (ошибка), содержимое остаётся; **Inline** — текст под полем/кнопкой; **Perm** / **Pro** / **Limit** / **Offline** — соответствующая карточка.

## 3. Коды агента ПК (WebSocket)

### 3.1 Проверки конверта и доступа (шаги 1–8)

| Код | Ключ | en | ru | Как |
|-----|------|----|----|-----|
| `protocol.tooLarge` | `error.protocol.tooLarge` | The request was too large for the PC. Try a smaller file or text. | Запрос слишком большой для ПК. Попробуйте файл или текст поменьше. | Toast |
| `protocol.malformed` | `error.protocol.malformed` | The PC couldn't read the request. Update Nexus PC on iPhone and PC. | ПК не смог прочитать запрос. Обновите Nexus PC на iPhone и на ПК. | Card |
| `protocol.unsupportedVersion` | `error.protocol.unsupportedVersion` | The app on iPhone and the PC are different versions. Update Nexus PC on both. | Версии Nexus PC на iPhone и на ПК не совпадают. Обновите обе. | Card |
| `command.unknown` | `error.command.unknown` | This PC doesn't support this action yet. Update Nexus PC on the PC. | Этот ПК пока не поддерживает это действие. Обновите Nexus PC на ПК. | Card |
| `command.notImplemented` | `error.command.notImplemented` | This action isn't available in the version installed on the PC. Update Nexus PC on the PC. | Это действие недоступно в версии Nexus PC на ПК. Обновите её. | Card |
| `command.invalidPayload` | `error.command.invalidPayload` | The PC didn't accept the data. Check the values and try again. | ПК не принял данные. Проверьте значения и повторите. | Toast |
| `auth.required` | `error.auth.required` | Reconnecting to the PC… | Повторное подключение к ПК… | без показа: переподключение; при неудаче — `error.auth.failed` |
| `auth.failed` | `error.auth.failed` | This iPhone is no longer connected to the PC. Connect it again: on the PC, click the N icon → Connect iPhone. | Этот iPhone больше не подключён к ПК. Подключите заново: на ПК значок N → «Подключить iPhone». | Card + кнопка `empty.noPC.action` |
| `auth.permissionDenied` | `permission.title` + `permission.path` | (см. `foundations.md`) | (см. `foundations.md`) | Perm |
| `auth.clockSkew` | `error.auth.clockSkew` | The time on iPhone and PC differs too much. Turn on automatic time on both devices. | Время на iPhone и ПК сильно расходится. Включите автоматическое время на обоих устройствах. | Card |
| `auth.replay` | `error.auth.replay` | The request was repeated. Try again. | Запрос повторился. Попробуйте ещё раз. | Toast |
| `confirm.required` | `error.confirm.required` | Confirm this action to continue. | Подтвердите действие, чтобы продолжить. | показать системный `confirmationDialog` действия; Toast — только если диалог не предусмотрен |
| `auth.confirmationRequired` | `error.confirm.required` | Confirm this action to continue. | Подтвердите действие, чтобы продолжить. | как `confirm.required` (см. «Нужно от протокола») |

### 3.2 План (Free/Pro)

| Код | Ключ | en | ru | Как |
|-----|------|----|----|-----|
| `plan.required` | `pro.card.title` + `pro.feature.<feature>.body` | Available in Pro · (строка функции, `ios-screens.md` 3) | Доступно в Pro · (строка функции) | Pro |
| `plan.required` при `details.state` = `expired` | `pro.expired.body` | Your Pro plan has ended. Renew it to use this again. | Тариф Pro закончился. Продлите его, чтобы снова пользоваться этим. | Pro |
| `plan.required` при `details.state` = `revoked` | `pro.revoked.body` | Pro is no longer active for the owner of this PC. | Pro больше не действует для владельца этого ПК. | Pro |
| `plan.limit` | `limit.title` + `limit.files.body` | Free Plan Limit Reached · You've reached the free limit for file transfer. Pro removes it. | Достигнут лимит бесплатного тарифа · Вы достигли бесплатного лимита передачи файлов. В Pro его нет. | Limit |
| `plan.limit` с `details.limit` | `limit.files.bodyWithLimit` | The free plan allows {LIMIT}. Pro removes the limit. | В бесплатном тарифе доступно {LIMIT}. В Pro лимита нет. | Limit (только если число пришло от ПК; в тексты приложения числа не вписываются) |

Права Pro — по аккаунту владельца ПК. Поэтому текст не говорит «ваш Pro», если подключившийся iPhone — не владелец — показывается `pro.card.ownerHint`:

| Ключ | en | ru | Где |
|------|----|----|-----|
| `pro.card.ownerHint` | Pro is checked for the account this PC is linked to. | Pro проверяется по аккаунту, к которому привязан этот ПК. | `ProCard` и экран Pro (A10) для не-владельца ПК |

### 3.3 Сопряжение

| Код | Ключ | en | ru | Как |
|-----|------|----|----|-----|
| `pair.noActiveSession` | `error.pair.noActiveSession` | The PC isn't waiting for a connection. On the PC, click the N icon → Connect iPhone, then scan the new QR code. | ПК сейчас не ждёт подключения. На ПК нажмите значок N → «Подключить iPhone» и отсканируйте новый QR-код. | Card (в `PairingFlowView`) |
| `pair.invalidCode` | `error.pair.invalidCode` | The code doesn't match. Scan the QR code on the PC again. | Код не совпал. Отсканируйте QR-код на ПК ещё раз. | Card |
| `pair.busy` | `error.pair.busy` | The PC is connecting another device. Wait a minute and try again. | ПК подключает другое устройство. Подождите минуту и повторите. | Card |
| `pair.rejected` | `error.pair.rejected` | The connection was declined on the PC. | Подключение отклонено на ПК. | Card |
| `pair.expired` | `error.pair.expired` | The QR code has expired. On the PC, show a new one and scan it. | Срок QR-кода истёк. Покажите на ПК новый и отсканируйте его. | Card |
| `pair.deviceLimit` | `error.pair.deviceLimit` | Too many devices are connected to this PC. Remove one on the PC: N icon → Devices and Permissions. | К этому ПК подключено слишком много устройств. Удалите лишнее на ПК: значок N → Устройства и разрешения. | Card |

### 3.4 Сеанс, питание, программы, профили

| Код | Ключ | en | ru | Как |
|-----|------|----|----|-----|
| `session.unavailable` | `status.trayMissing` | The Nexus PC app isn't running on the PC. Apps and lock are unavailable. | На ПК не запущено приложение Nexus PC. Программы и блокировка недоступны. | `TrayMissingCard` |
| `session.unavailable` (раздел Control, Files) | `error.session.signIn` | Sign in to Windows on the PC and make sure Nexus PC is running near the clock. | Войдите в Windows на ПК и проверьте, что Nexus PC запущен у часов. | `TrayMissingCard` |
| `power.failed` | `error.power.failed` | The PC couldn't do this. It may be blocked by another program. Try again. | ПК не смог это выполнить. Возможно, мешает другая программа. Повторите. | Toast |
| `power.nothingToCancel` | `error.power.nothingToCancel` | There's no scheduled shutdown to cancel. | Нет запланированного выключения. | Toast (информация) |
| `power.unsupported` | `error.power.unsupported` | Power control isn't available on this PC. | Управление питанием на этом ПК недоступно. | Card |
| `power.lockFailed` | `error.power.lockFailed` | The PC couldn't be locked. Try again. | Не удалось заблокировать ПК. Повторите. | Toast |
| `apps.notFound` | `error.apps.notFound` | This app was removed on the PC. Pull down to refresh the list. | Программа удалена на ПК. Потяните вниз, чтобы обновить список. | Toast + обновление списка |
| `apps.launchFailed` | `error.apps.launchFailed` | The app didn't start on the PC. Check that it's still installed. | Программа не запустилась на ПК. Проверьте, что она установлена. | Toast |
| `apps.closeNotAllowed` | `error.apps.closeNotAllowed` | This app can't be closed from iPhone. | Эту программу нельзя закрыть с iPhone. | Toast |
| `profiles.notFound` | `error.profiles.notFound` | This profile no longer exists. Pull down to refresh. | Этого профиля больше нет. Потяните вниз, чтобы обновить. | Toast + обновление |
| `profiles.invalid` | `error.profiles.invalid` | The profile uses an app that isn't on the PC's list. Remove it from the profile. | В профиле есть программа, которой нет в списке ПК. Уберите её из профиля. | Inline (редактор) |
| `profiles.limit` | `error.profiles.limit` | You've reached the maximum number of profiles. Delete one to add a new one. | Достигнуто максимальное число профилей. Удалите один, чтобы добавить новый. | Inline |

### 3.5 Подсветка, файлы, буфер, автоматизации

| Код | Ключ | en | ru | Как |
|-----|------|----|----|-----|
| `rgb.unavailable` | `error.rgb.unavailable` | Lighting isn't available. Make sure OpenRGB is running on the PC. | Подсветка недоступна. Проверьте, что на ПК запущен OpenRGB. | Card |
| `rgb.unavailable` (`rgb.status.state` = `conflict`) | `rgb.busy` | Lighting is used by another program on the PC. | Подсветкой управляет другая программа на ПК. | Card |
| `files.notFound` | `error.files.notFound` | The file is no longer in the Nexus PC folder. Refresh the list. | Файла больше нет в папке Nexus PC. Обновите список. | Toast + обновление |
| `files.offsetMismatch` | `error.files.resume` | The transfer was interrupted. Tap Try Again to continue. | Передача прервалась. Нажмите «Повторить», чтобы продолжить. | Inline у строки передачи (автоповтор один раз) |
| `files.uploadNotFound` | `error.files.resume` | (как выше) | (как выше) | Inline |
| `files.hashMismatch` | `error.files.hashMismatch` | The file arrived damaged and was deleted. Send it again. | Файл пришёл повреждённым и удалён. Отправьте его снова. | Inline |
| `files.noSpace` | `error.files.noSpace` | Not enough free space on the PC. | На ПК недостаточно свободного места. | Inline |
| `files.failed` | `error.files.failed` | The file couldn't be transferred. Try again. | Не удалось передать файл. Повторите. | Inline |
| `clipboard.failed` | `error.clipboard.failed` | The PC clipboard is busy in another program. Try again in a moment. | Буфер обмена ПК занят другой программой. Повторите чуть позже. | Toast |
| `automations.notFound` | `error.automations.notFound` | This automation is no longer available on the PC. It may have been deleted or created on another device. | Эта автоматизация больше недоступна на ПК. Возможно, её удалили или создали на другом устройстве. | Toast + обновление |
| `automations.limit` | `error.automations.limit` | You've reached the maximum number of automations. Delete one to add a new one. | Достигнуто максимальное число автоматизаций. Удалите одну, чтобы добавить новую. | Inline |
| `automations.running` | `error.automations.running` | This automation is already running. | Эта автоматизация уже выполняется. | Toast (информация) |

### 3.6 Экран и ввод

| Код | Ключ | en | ru | Как |
|-----|------|----|----|-----|
| `screen.videoUnavailable` | — | (не показывается: телефон сам переходит на кадры) | — | без показа; в «Диагностике» — `diag.screen.jpegMode` |
| `screen.denied` | `error.screen.denied` | Screen sharing was declined on the PC. | Показ экрана отклонён на ПК. | Card |
| `screen.blocked` | `error.screen.blocked` | The PC screen is locked, a security prompt is open, or screen sharing is turned off in the N icon menu. | Экран ПК заблокирован, открыт запрос безопасности или показ экрана запрещён в меню значка N. | Card |
| `screen.notStarted` | — | (не показывается: телефон заново начинает показ) | — | без показа; при повторной неудаче — `common.error.title` |
| `input.blocked` | `error.input.blocked` | The PC isn't accepting input: the screen is locked, a security prompt is open, or control is turned off in the N icon menu. | ПК не принимает ввод: экран заблокирован, открыт запрос безопасности или управление запрещено в меню значка N. | Toast (не чаще раза в 5 с) |

### 3.7 Общие

| Код | Ключ | en | ru | Как |
|-----|------|----|----|-----|
| `rate.limited` | `error.rateLimited` | Too many requests. Wait a moment and try again. | Слишком много запросов. Подождите немного и повторите. | Toast; кнопка выключена на `retryAfter` |
| — | `error.rateLimited.retryIn` | Try Again in %d s | Повторить через %d с | подпись кнопки |
| `internal` | `error.internal` | Something went wrong on the PC. Try again. If it repeats, restart the PC. | На ПК произошла ошибка. Повторите. Если повторяется — перезагрузите ПК. | Card |
| (неизвестный) | `error.generic.body` | Try again. If it repeats, see Help → Diagnostics. | Повторите. Если не помогает — откройте «Помощь → Диагностика». | Card |
| (обрыв связи) | `status.offline` + `offline.body` | (`foundations.md`) | (`foundations.md`) | Offline |

### 3.8 Причины недоступности метрик (`metrics.snapshot.unavailableCodes`)

Строка `Caption` (`textSecondary`) под значением «—» в карточке метрики PC Monitor. Не ошибка: без иконки ошибки, без `ErrorCard`. Одна-две строки на iPhone SE; при Dynamic Type AX переносится, не обрезается. Неизвестный код → `monitor.reason.unknown`. `warmingUp` показывается без иконки и исчезает сам.

| Код | Ключ | en | ru |
|-----|------|----|----|
| `sensorDriverMissing` | `monitor.reason.sensorDriverMissing` | Needs the sensor driver. Reinstall Nexus PC on the PC. | Нужен драйвер датчиков. Переустановите Nexus PC на ПК. |
| `sensorReadFailed` | `monitor.reason.sensorReadFailed` | Couldn't read the sensors. Restart the PC. | Не удалось прочитать датчики. Перезагрузите ПК. |
| `sensorMissing` | `monitor.reason.sensorMissing` | This processor doesn't report this value. | Процессор не сообщает это значение. |
| `warmingUp` | `monitor.reason.warmingUp` | Measuring… | Измеряем… |
| `osQueryFailed` | `monitor.reason.osQueryFailed` | Windows didn't respond. Try again in a moment. | Windows не ответила. Подождите немного. |
| `gpuDriverMissing` | `monitor.reason.gpuDriverMissing` | Available only for NVIDIA graphics with the driver installed. | Доступно только для видеокарт NVIDIA с установленным драйвером. |
| `gpuLibraryRejected` | `monitor.reason.gpuLibraryRejected` | The graphics driver files look wrong. Reinstall the NVIDIA driver. | Файлы драйвера видеокарты повреждены. Переустановите драйвер NVIDIA. |
| `gpuLibraryLoadFailed` | `monitor.reason.gpuLibraryLoadFailed` | Couldn't start the graphics driver. Update the NVIDIA driver. | Не удалось запустить драйвер видеокарты. Обновите драйвер NVIDIA. |
| `gpuInitFailed` | `monitor.reason.gpuInitFailed` | (как `gpuLibraryLoadFailed`, тот же ключ допустим) | (как выше) |
| `gpuListFailed` | `monitor.reason.gpuListFailed` | The graphics card didn't respond. Restart the PC. | Видеокарта не ответила. Перезагрузите ПК. |
| `platformUnsupported` | `monitor.reason.platformUnsupported` | Not available on this PC. | Недоступно на этом ПК. |
| (неизвестный) | `monitor.reason.unknown` | Not available right now. | Сейчас недоступно. |

`gpuInitFailed`: отдельный ключ с текстом `gpuLibraryLoadFailed` (en «Couldn't start the graphics driver. Update the NVIDIA driver.», ru «Не удалось запустить драйвер видеокарты. Обновите драйвер NVIDIA.»), чтобы потом можно было развести.

VoiceOver: метка карточки = «<метрика>, недоступно. <причина>» (`monitor.unavailable.a11y` · en «%@, unavailable. %@» · ru «%@, недоступно. %@»).

### 3.9 Подсветка: `rgb.status.reasonCode`

Существующие ключи покрывают оба кода, новых не нужно:

| `reasonCode` | Ключ | Примечание |
|--------------|------|-----------|
| `openrgbNotResponding` | `error.rgb.unavailable` | текст из 3.5 |
| `conflict` | `rgb.busy` | текст из 3.5; совпадает по смыслу с `reason` агента |
| (неизвестный) | — | показывать `reason` от агента (`docs/API.md`) |

### 3.10 Подтверждение «Забыть этот ПК»

Alert (`.alert`, кнопка действия `role: .destructive`, «Отмена» — `common.cancel`). Временные тексты в `Localizable.xcstrings` верны по смыслу; финальные добавляют, как подключиться снова, и путь через значок N.

| Ключ | en | ru |
|------|----|----|
| `pc.forget.confirm.title` | Forget This PC? | Забыть этот ПК? |
| `pc.forget.confirm.message` | It's removed only from this iPhone. To remove this iPhone on the PC: N icon → Devices and Permissions. To reconnect, scan the QR code again. | ПК удаляется только с этого iPhone. Чтобы удалить iPhone на ПК: значок N → «Устройства и разрешения». Подключиться снова можно по QR-коду. |
| `pc.forget.confirm.action` | Forget PC | Забыть ПК |

`admin.required`: код канала трея (E4-6), на iPhone не приходит. Текст — только в трее: `ProblemAdminRequired` (`windows.md`, 3.3). См. `ios-screens.md`, «Нужно от протокола».

## 4. Коды сервера аккаунтов (HTTPS)

Отдельное пространство кодов (`docs/API.md`, «Коды сервера прав»). На iPhone встречаются в экранах аккаунта (E5-3) и одобрения входа ПК; коды только службы ПК (`pending`, `slow_down`, `pc.key_required`, `revoked`, `reuse.detected`, `request.stale`, `nonce.replay`) показывает трей (E5-4), здесь — тексты на случай показа в «Диагностике» и для `windows.md`.

| Код | Ключ | en | ru | Где |
|-----|------|----|----|-----|
| `bad.request` | `account.error.badRequest` | Something went wrong. Update Nexus PC and try again. | Что-то пошло не так. Обновите Nexus PC и повторите. | iPhone, ПК |
| `auth.required` | `account.error.authRequired` | Please sign in again. | Войдите снова. | iPhone |
| `reauth.required` | `account.error.reauthRequired` | For your security, enter your password again. | В целях безопасности введите пароль ещё раз. | удаление аккаунта (E5-3) |
| `code.invalid` | `account.error.codeInvalid` | This code is wrong or has expired. Request a new one on the PC. | Код неверный или истёк. Запросите новый на ПК. | одобрение входа ПК |
| `pending` | `account.status.pending` | Waiting for approval on iPhone… | Ждём подтверждения на iPhone… | ПК |
| `slow_down` | `account.status.pending` | (как выше) | (как выше) | ПК, без показа ошибки |
| `rate.limited` | `error.rateLimited` | (см. 3.7) | (см. 3.7) | iPhone, ПК |
| `pcs.limit` | `account.error.pcsLimit` | Your account has the maximum number of PCs. Remove one in Account → My Devices. | В аккаунте максимальное число ПК. Удалите один: «Аккаунт» → «Мои устройства». | одобрение входа ПК |
| `pc.key_required` | `account.error.pcKey` | This PC couldn't be verified. Reinstall Nexus PC on the PC. | Не удалось проверить этот ПК. Переустановите Nexus PC на ПК. | ПК |
| `request.stale` | `account.error.clock` | Check that the date and time on this device are correct. | Проверьте дату и время на этом устройстве. | ПК (после автоповтора) |
| `nonce.replay` | `account.error.retry` | Couldn't complete the request. Try again. | Не удалось выполнить запрос. Повторите. | ПК (после автоповтора) |
| `revoked` | `account.error.revoked` | This PC is no longer linked to an account. Sign in again from the N icon menu. | ПК больше не привязан к аккаунту. Войдите снова через меню значка N. | ПК |
| `reuse.detected` | `account.error.revoked` | (как выше) | (как выше) | ПК |
| `pc.not_found` | `account.error.pcNotFound` | This PC is no longer in your account. | Этого ПК больше нет в вашем аккаунте. | My Devices |
| `internal` | `account.error.internal` | The service is temporarily unavailable. Try again later. Free features keep working. | Сервис временно недоступен. Повторите позже. Бесплатные функции продолжают работать. | iPhone, ПК |

## 5. Доступность

- `ErrorCard` и `Toast` с ошибкой объявляются VoiceOver (`foundations.md`, 6.1); заголовок `ErrorCard` — `.isHeader`.
- Ошибка поля (Inline) связывается с полем: `accessibilityHint` поля = текст ошибки, фокус переводится на поле.
- Цвет не единственный признак: у всех ошибок иконка `exclamationmark.triangle.fill`.
