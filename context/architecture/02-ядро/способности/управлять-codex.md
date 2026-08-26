# Способность: управлять Codex

## Когда применять

Проверить/install pinned runtime, выполнить ChatGPT login/logout, account/model/usage read, start/resume thread, start/interrupt turn.

## Когда НЕ применять

Не использовать API key, глобальный PATH, неизвестный binary, dynamic tools или произвольный cwd.

## Примеры

- Good: открыть authUrl официального managed ChatGPT flow и дождаться account updated.
- Bad: попросить пользователя вставить секретный ключ в settings.

## Planning-only process profile

- isolated Codex home и пустой private cwd;
- no environments, apps, MCP, plugins, skills или dynamic tools;
- approvalPolicy never и read-only permission profile;
- только text input + outputSchema; локальные file/image paths не передаются;
- built-in tool-call event немедленно прерывает turn и делает результат invalid;
- conformance fixture доказывает, что pinned runtime не пишет файлы и не запрашивает дополнительные permissions.

Prompt policy не считается security boundary; фактическая permission configuration и deterministic TaskPlan validator обязательны.

## Что может пойти не так

Protocol mismatch, hash failure, browser callback error, unavailable model или usage limit. Adapter fail-closed; install staging удаляется; last-known-good сохраняется; auth/usage не retry.
Связано: [реестр](README.md), [права](../права-доступа.md), [инварианты](../../03-данные/правила-нерушимые.md).
