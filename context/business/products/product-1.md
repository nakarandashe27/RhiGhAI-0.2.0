# Продукт 1 — RhiGhAI MVP

## Задача

Сократить путь от намерения моделировщика до безопасного редактируемого результата в Rhino или Grasshopper, не требуя программирования/API-ключа.

## Основные возможности

- ChatGPT managed login и динамические Codex models/reasoning efforts.
- Compact snapshot единиц, tolerances, layers, selection и owned references.
- Strict TaskPlan и allowlisted Rhino operations.
- Один Rhino или один Grasshopper Undo на ответ.
- Rollback и repair 1–5 attempts без повторов auth/usage/Stop/stale.
- Один RhiGhAI Parametric на conversation с typed item/list inputs.
- Видимые stages, C# representation, errors, result и account status.

Полный allowlist: [реестр способностей](../../architecture/02-ядро/способности/README.md).

## Как работает

Prompt + fresh snapshot → Codex outputSchema plan → strict/semantic validation → canonical OperationNode graph → prepare → fresh fingerprint/cancellation check → один host commit → verified result. Ошибка откатывается и может получить bounded repair.

## Текущий этап

🔜 Архитектура завершена; реализация не начата. Первые обязательные работы — Codex, Rhino atomicity и Grasshopper persistence blocker-spikes из [roadmap](../../architecture/08-дорожная-карта/roadmap.md).

## Не входит

Arbitrary code, Mesh/SubD/materials/rendering/layouts/blocks, GH Data Trees, free graph editing, mixed Rhino+GH turn, macOS и публичный Yak.

Связано: [scope](../../idea/04-mvp/scope.md), [acceptance](../../idea/04-mvp/success-metrics.md).
