# Технические риски

## T1 — Codex app-server ломает основной сценарий

**Провал:** Experimental-протокол меняет OAuth, модели, threads, events или interrupt. **Сигнал:** method not found, пустые модели, login без account state. **Ответ:** точная версия и SHA-256, один adapter, protocol fixtures, startup smoke-test, last-known-good runtime.

## T2 — Undo и rollback не обеспечивают атомарность

**Провал:** после исключения остаются слои, объекты или атрибуты; Ctrl-Z задевает чужое действие. **Сигнал:** state до/после различается, требуется два Undo. **Ответ:** prepare вне документа, короткий UI commit, transaction journal, fault injection после каждого шага, запрет параллельного commit.

## T3 — GH-компонент не переживает reopen

**Провал:** динамические inputs, wires, алгоритм или references теряются. **Сигнал:** GUID/порядок портов меняются, preview пуст, новая GHA не читает старое состояние. **Ответ:** версионированная сериализация и миграции, стабильные input IDs, round-trip и upgrade tests.

## T4 — Stop не работает во время native call

**Провал:** Boolean занимает UI-поток и не может быть прерван. **Сигнал:** Rhino не отвечает, Stop нажат, но commit продолжается. **Ответ:** budgets сложности, cancellation между вызовами, запрет commit после Stop, worst-case benchmarks registry.

## T5 — Fingerprint не закрывает TOCTOU

**Провал:** старый план применяется после смены документа, геометрии или layer. **Сигнал:** одинаковый GUID пропускает изменённую геометрию. **Ответ:** document runtime ID, host, geometry hash/serial, attributes, layer и selection; проверка непосредственно перед Undo; stale без retry.

## T6 — Document ↔ thread неоднозначен

**Провал:** Save As, copy, rename и recovery делят или теряют thread. **Сигнал:** две копии продолжают одну беседу. **Ответ:** политика canonical path + file fingerprint + runtime serial; новый thread при неоднозначности; тесты New/Open/Save/Save As/Rename/Copy/Recovery.

## T7 — TaskPlan небезопасен или слишком беден

**Провал:** schema-valid план имеет неверные единицы/границы либо полезная задача не выражается. **Сигнал:** semantic rejection и retry доминируют. **Ответ:** dimensional semantics, limits, adversarial corpus, property tests; capability только полным vertical slice.

## T8 — Repair увеличивает задержку, не шанс успеха

**Провал:** повторяется тот же план или error. **Сигнал:** одинаковая пара plan/error и рост median latency. **Ответ:** repairable taxonomy, свежий snapshot, machine-readable diagnostics, остановка повторяющейся пары.

## T9 — C#-представление расходится с исполнением

**Провал:** показанный код не отражает units, validation или ownership. **Сигнал:** golden plan и code дают разные signatures. **Ответ:** один canonical operation graph, честное название «представление плана», parity tests.
