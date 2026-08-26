# Стратегические риски

## S1 — официальный RhinoMCP стирает дельту

**Провал:** McNeel выпускает доверенный panel с Codex, context, Undo, Stop и GH. **Сигнал:** roadmap становится стабильным Rhino 8 release. **Ответ:** ежемесячная проверка; moat в typed safety, fault-tested transactions и workflows; go/no-go на переход к безопасному executor-дополнению.

## S2 — Два вендора, нет fallback

**Провал:** OpenAI меняет auth/runtime, McNeel — loader/API. **Сигнал:** pinned runtime больше не логинится или Rhino patch не грузит plugin. **Ответ:** матрица Codex × Rhino, минимальные adapters, startup self-test, last-known-good, явный unsupported version.

## S3 — Реестр растёт быстрее ценности

**Провал:** каждая операция требует schema, validator, executor, renderer, undo и тесты. **Сигнал:** capabilities растут, weekly-use нет. **Ответ:** composable primitives, ratio successful workflows/capabilities, заморозка registry до подтверждения golden workflows.

## S4 — Демо не доказывает потребность

**Провал:** три подготовленных prompt проходят, но не встречаются в работе. **Сигнал:** пользователь не приносит новые задачи. **Ответ:** журнал реальных задач 1–2 недели, сравнение скорости и ручных исправлений, отдельные demo и adoption metrics.
