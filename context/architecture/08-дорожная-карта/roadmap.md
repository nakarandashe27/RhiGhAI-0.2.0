# Дорожная карта RhiGhAI MVP

## Правило: архитектура описывает целевую систему

Файлы архитектуры (01-обзор … 08-дорожная-карта) — единый источник правды о том, КАКОЙ система должна быть по итогам текущего замысла, а не только снимок сегодняшнего кода. Статусы: ✅ реализовано · 🔜 запланировано · 💡 гипотеза.

Для частей ✅ источник истины — код: при расхождении чинится документация. Для 🔜/💡 документация задаёт решение, по которому пишется код.

Решение поменялось — правится сам целевой файл. Значимое изменение стека, данных, границ или модулей дополнительно фиксируется ADR в [журнале решений](../06-решения/журнал-решений/). Мелкие уточнения вносятся прямо в документы.

## Последовательность

- [ ] **0. Воспроизводимый toolchain и каркас**
  - **Что делаем:** установить .NET 8 SDK, создать solution Core/RHP/GHA/Tests/HostTests, pin RhinoCommon/Grasshopper minimum 8.20 и включить deterministic build.
  - **Контекст:** [стек](../05-стек/технологии.md), [что не выбрали](../05-стек/что-не-выбрали.md).
  - **Критерий готовности:** solution собирается одной командой; empty RHP/GHA загружаются в Rhino 8.20/current; compat.exe проходит; tests запускаются без Rhino.

- [ ] **1. BLOCKER — Codex runtime/auth/protocol spike**
  - **Что делаем:** минимальный C# JSONL client, managed runtime staging/SHA/publisher, isolated home, conformance sequence и recorded fixtures.
  - **Контекст:** [управлять Codex](../02-ядро/способности/управлять-codex.md), [триггеры](../04-потоки/триггеры.md), [security](../07-нефункциональные/безопасность.md).
  - **Критерий готовности:** на чистой Windows без Codex/API key проходят install, ChatGPT login, account/read, model/list, thread start/resume, outputSchema turn, stream, interrupt и logout; несовместимый runtime отклоняется.

- [ ] **2. BLOCKER — Rhino atomicity spike**
  - **Что делаем:** transaction coordinator и journal prototype для layer/create/transform/attributes; fault/crash injection после каждой mutation.
  - **Контекст:** [commit protocol](../03-данные/правила-нерушимые.md#commit-protocol), [CommitJournal](../03-данные/сущности.md), [архитектурные риски](../07-нефункциональные/риски-архитектуры.md).
  - **Критерий готовности:** успешный answer = один Undo; каждая injected exception/Stop восстанавливает сравнимый before state; crash создаёт blocking uncertain state и никогда blind Undo.

- [ ] **3. BLOCKER — Grasshopper emission/Undo spike**
  - **Что делаем:** программная загрузка Grasshopper, чтение живого каталога `GH_ComponentServer`, `EmitObject` + провода + слайдеры + группа, замена прошлого набора и один GH Undo record.
  - **Контекст:** [способность](../02-ядро/способности/определение-grasshopper.md), [сущности](../03-данные/сущности.md), [инварианты GH](../03-данные/правила-нерушимые.md#grasshopper).
  - **Критерий готовности:** граф из компонентов, слайдеров и панели эмитится на 8.20/current, переживает save/reopen как обычное определение, повторный запрос заменяет предыдущий набор, и ровно один Undo снимает результат вместе с группой.

- [ ] **4. Versioned contracts и canonical graph**
  - **Что делаем:** TaskPlan schema/DTO, OperationNode graph, capability registry, canonical hashing, strict/tolerant readers и C# renderer.
  - **Контекст:** [главный кирпич](../03-данные/главный-кирпич.md), [entities](../03-данные/сущности.md), [capabilities](../02-ядро/способности/).
  - **Критерий готовности:** golden corpus, unknown-field/version tests и parity graphHash/C# pass under different cultures/property orders.

- [ ] **5. Адаптеры провайдеров и runtime manager**
  - **Что делаем:** `IPlanProvider` с двумя реализациями: Codex (correlation, account/models/usage, login lifecycle, LKG activation) и OpenAI-совместимый HTTP (json_schema strict с откатом в json_object, каталог моделей, DPAPI-хранилище ключа).
  - **Контекст:** [карта](../01-обзор/карта-системы.md), [Codex compatibility](../05-стек/технологии.md#контракт-совместимости-codex), [security](../07-нефункциональные/безопасность.md).
  - **Критерий готовности:** contract fixtures и process lifecycle tests проходят; tool/permission item прерывает turn; process leak отсутствует.

- [ ] **6. Local state, identity и recovery**
  - **Что делаем:** settings, transcript events, document binding state machine, ownership, mutex/atomic stores/quarantine.
  - **Контекст:** [память](../02-ядро/память-и-состояние.md), [EventEnvelope](../03-данные/главный-кирпич.md), [triggers](../04-потоки/триггеры.md).
  - **Критерий готовности:** New/Open/Save/Save As/Copy/Recovery/multi-process matrix и corrupt-write tests проходят без cross-document ownership.

- [ ] **7. Rhino capability vertical slices**
  - **Что делаем:** curves, surfaces, extrusion/Brep, bounded Boolean, transform/copy, layers/attributes — каждый с validator/prepare/execute/render/tests.
  - **Контекст:** [реестр](../02-ядро/способности/README.md), [permissions](../02-ядро/права-доступа.md), [invariants](../03-данные/правила-нерушимые.md).
  - **Критерий готовности:** panel и selected-move acceptance workflows проходят, fault injection не оставляет state, limits fail before mutation.

- [ ] **8. Полный конвейер GhGraph**
  - **Что делаем:** контракт и output schema `GhGraph`, каталог установки с отсевом script-компонентов, валидатор портов/циклов/литералов, раскладка по длиннейшему пути, читаемый рендер и bounded repair с подсказкой доступных портов.
  - **Контекст:** [способность](../02-ядро/способности/определение-grasshopper.md), [ADR-006](../06-решения/журнал-решений/ADR-006-native-gh-граф.md), [port entities](../03-данные/сущности.md).
  - **Критерий готовности:** три acceptance-запроса дают работающие редактируемые определения; ни один невалидный граф не доходит до холста; bake выполняется штатным механизмом Grasshopper.

- [ ] **9. Turn orchestrator, repair и Stop**
  - **Что делаем:** single-writer reducer, epochs, commit lease, timeout/interrupt, error taxonomy и bounded repair.
  - **Контекст:** [state machine](../04-потоки/триггеры.md#turn-state-machine), [repair](../02-ядро/способности/исправить-план.md), [расходы](../07-нефункциональные/расходы.md).
  - **Критерий готовности:** exhaustive event permutations не допускают late commit; auth/usage/stop/stale не retry; identical pair stops.

- [ ] **10. Eto panel в стиле Art.Brodsky**
  - **Что делаем:** header, message rail, code/error blocks, selection line, composer, selectors, Send/Stop, footer и settings view в одном panel.
  - **Контекст:** [визуальный язык](../../idea/06-principles/interface-style.md), [характер](../02-ядро/характер.md), [panel map](../01-обзор/карта-системы.md).
  - **Критерий готовности:** keyboard/focus, 100–200% DPI, minimum dock width и reduced motion проходят; CDN отсутствует; code/error копируются.

- [ ] **11. Settings, first-run и managed install UX**
  - **Что делаем:** runtime status/refresh/install, login/logout, retry/timeout validation, account/rate-limit footer и safe recovery messages.
  - **Контекст:** [управлять Codex](../02-ядро/способности/управлять-codex.md), [installation trust](../05-стек/технологии.md#installation-trust-chain), [характер](../02-ядро/характер.md).
  - **Критерий готовности:** standard user проходит happy/error paths без terminal/API key; invalid settings не записываются; LKG recovery работает.

- [ ] **12. Security, recovery и diagnostic bundle**
  - **Что делаем:** origin-tagged snapshot, injection fixtures, redaction, SBOM/notices, journal recovery UI и resource caps.
  - **Контекст:** [security](../07-нефункциональные/безопасность.md), [risks](../07-нефункциональные/риски-архитектуры.md), [costs](../07-нефункциональные/расходы.md).
  - **Критерий готовности:** adversarial document literals не расширяют action scope; bundle не содержит secrets/full model; corrupt stores follow policy.

- [ ] **13. Acceptance и локальный RHI**
  - **Что делаем:** три golden workflows, clean-machine matrix Rhino 8.20/current, package install/uninstall и user guide.
  - **Контекст:** [MVP metrics](../../idea/04-mvp/success-metrics.md), [stack](../05-стек/технологии.md), [operational risks](../../idea/07-risks/operational.md).
  - **Критерий готовности:** с первого запуска без SDK/Codex/API key проходят install/login и три scenarios; один Undo; failed attempts leave zero residual state.

- [ ] **14. После MVP — решение о Yak**
  - **Что делаем:** сравнить официальный RhinoMCP, подтвердить повторное использование, signing и только затем подготовить public Yak.
  - **Контекст:** [research delta](../../idea/05-research/delta.md), [strategic risks](../../idea/07-risks/strategic.md), [press release](../01-обзор/пресс-релиз.md).
  - **Критерий готовности:** отдельное go/no-go решение и ADR; публичная упаковка не маскирует неподтверждённый MVP.
