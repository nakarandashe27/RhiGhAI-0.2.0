# Операционные риски

## O1 — Installer, подпись и Defender ломают first run

**Провал:** unsigned RHP/GHA/runtime блокируются SmartScreen или антивирусом. **Сигнал:** load failed, quarantine, admin/terminal/manual Unblock. **Ответ:** подпись installer/assemblies, точный официальный asset и SHA manifest, явная кнопка; чистая Windows 11 VM, standard user, Defender on, без SDK/Codex.

## O2 — Ошибку невозможно воспроизвести

**Провал:** без телеметрии остаётся «не сработало». **Сигнал:** проблема только на машине пользователя. **Ответ:** локальный structured log с correlation ID, версиями, стадией и error code; redaction; diagnostic bundle без аккаунта и геометрии.

## O3 — Rhino 8.20+ проверен только на свежей версии

**Провал:** minimum host имеет другой API/loader, GHA конфликтует с plugins. **Сигнал:** missing method или component не обнаружен. **Ответ:** clean-VM matrix 8.20 + current, запрет новых API, smoke RHP/panel/GHA/save/reopen/uninstall.

## O4 — Crash во время commit расходится с локальной историей

**Провал:** Rhino падает между мутацией, undo и ownership. **Сигнал:** orphan objects или thread считает commit успешным. **Ответ:** prepared/committing/committed journal, ownership после host commit, обнаружение незавершённого commit на старте, crash injection.

## O5 — Брендовые шрифты ломают автономность или распространение

**Провал:** Eto не читает webfont, CDN недоступен или лицензия не разрешает вложить desktop asset. **Сигнал:** fallback меняет геометрию UI, первый запуск требует сеть, package содержит неподтверждённый font. **Ответ:** не загружать CDN в runtime; подтвердить право распространения и desktop-формат до bundling; тестировать fallback и DPI как поддерживаемое состояние.
