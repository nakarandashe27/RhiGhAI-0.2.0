# Возражения и ответы

## «AI испортит рабочую модель»

Selection/ownership ограничивают область, stale fingerprint блокирует старый plan, successful turn создаёт один Undo, failed attempt проходит rollback/fault checks.

## «Мне снова нужен API-ключ и отдельная оплата»

RhiGhAI использует managed ChatGPT login Codex. Отдельный API key не вводится; account limits показываются как их сообщил runtime.

## «Результат — непрозрачный black box»

Лента показывает stages, canonical C# representation, errors и actual created/modified counts. GH component имеет named typed ports и algorithm summary.

## «Плагин не умеет половину Rhino»

Это осознанный MVP. UI честно показывает capabilities; unsupported intent не маскируется попыткой arbitrary code.

## «Установка будет сложнее самого скрипта»

First-run gate проверяет local install, managed Codex, login и recovery на clean Windows без SDK/terminal/API key.

Связано: [риски продукта](../../idea/07-risks/product.md), [контент и ответы](../marketing/content.md).
