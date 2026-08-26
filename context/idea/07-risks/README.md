# Pre-mortem RhiGhAI

Независимая проверка исходит из сценария: MVP собран, но пользователь отказался от него как от медленного, ненадёжного и сложного в установке инструмента.

## TOP-5

1. [[technical#T2 — Undo и rollback не обеспечивают атомарность]]
2. [[technical#T1 — Codex app-server ломает основной сценарий]]
3. [[technical#T3 — GH-компонент не переживает reopen]]
4. [[strategic#S1 — официальный RhinoMCP стирает дельту]]
5. [[operational#O1 — installer, подпись и Defender ломают first run]]

## Три blocker-gate до полной реализации

### 1. Codex runtime gate

На чистой Windows без Codex, SDK и API-ключа должен пройти spike: установка закреплённого официального runtime, ChatGPT OAuth, account read, model list, start/resume thread, structured turn, streaming и interrupt. Должны быть подтверждены допустимость распространения и поведение несовместимой версии.

### 2. Atomicity gate

Прототипы Rhino и Grasshopper проходят fault injection после каждого шага, Stop между шагами, stale fingerprint и смену документа. Неудача возвращает сравнимое состояние целиком; успех создаёт ровно один Undo выбранного host.

### 3. Grasshopper persistence gate

Компонент с динамическими number/integer/bool/text/Point/Curve/Brep item/list inputs сохраняется в GH, открывается в чистой сессии и пересчитывается без Codex. Сохраняются wires, input identities, алгоритм и preview; обновление алгоритма — один GH Undo. Параллельно фиксируется политика Save As/Copy для связи document ↔ thread.

Пока любой gate не пройден, соответствующая часть остаётся spike, а не обещанием MVP.

Категории: [[technical]], [[product]], [[strategic]], [[operational]].
