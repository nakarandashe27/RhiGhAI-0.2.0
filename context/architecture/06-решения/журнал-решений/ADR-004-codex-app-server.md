# ADR-004 — Pinned Codex app-server

- **Дата:** 2026-08-24
- **Статус:** принято с blocker-gate
- **Решение:** официальный pinned binary по JSONL stdio за versioned C# adapter; ChatGPT managed auth; no API key.
- **Причина:** app-server даёт threads/auth/models/events/interrupt, но experimental protocol нельзя распространять по core.
- **Последствия:** conformance matrix, LKG runtime, isolated planning-only profile и clean-machine gate обязательны.

Затрагивает: [стек](../../05-стек/технологии.md), [управлять Codex](../../02-ядро/способности/управлять-codex.md).
