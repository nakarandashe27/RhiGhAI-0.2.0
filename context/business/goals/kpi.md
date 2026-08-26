# Метрики

## Release gates

- Три blocker-spikes: pass/fail.
- Три acceptance workflows: pass/fail на Rhino 8.20 и current.
- Неудачные fault-injection attempts с остаточным host state: целевое значение 0.
- Undo records на успешный answer: ровно 1 в выбранном host.
- First run, требующий terminal/API key/manual config: целевое значение 0.
- GH save/reopen/upgrade/Undo matrix failures: целевое значение 0.

## Полезность

- Time-to-correct-result относительно ручного workflow.
- First-attempt success и success после repair.
- Доля immediate Undo после technically successful answer.
- Stop frequency и late-commit incidents.
- Unsupported intent share в журнале 30 реальных задач.
- Повторное добровольное использование без подготовленного demo prompt.

Текущих операционных значений ещё нет, поэтому LIVE markers не создаются. После instrumented MVP источник будет локальным redacted event log, а не выдуманной аналитикой.

Связано: [unit economics](../economics/unit-economics.md), [revenue](../economics/revenue.md), [funnel](../marketing/funnel.md), [architecture costs](../../architecture/07-нефункциональные/расходы.md).
