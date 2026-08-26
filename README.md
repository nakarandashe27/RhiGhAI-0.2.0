# RhiGhAI

**Natural language → a real, editable Grasshopper definition. Not a black box.**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Rhino 8](https://img.shields.io/badge/Rhino-8.20%2B-801010)
![.NET 8](https://img.shields.io/badge/.NET-8.0--windows-512BD4)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)
![Status](https://img.shields.io/badge/status-MVP%200.2.0-orange)

RhiGhAI is an open-source plugin for Rhino 8. You describe a task in plain language; a language model returns a **strictly typed plan**, never code; and a deterministic executor performs only allowlisted operations.

*[Русская версия ниже](#rhighai-по-русски) · Russian version below*

---

## Two kinds of result

| Mode | What you get |
|---|---|
| **Rhino** | Geometry in the document. One successful answer is undone by a single `Ctrl+Z`. |
| **Grasshopper** | **An editable definition made of ordinary Grasshopper components** — nodes, wires, sliders and panels on the canvas, wrapped in one group. |

The Grasshopper mode is the point of the project. Instead of one opaque component with four inputs, RhiGhAI assembles a genuine definition — `Number Slider → Series → Construct Point → Circle → Extrude`, or whatever your task needs — from the components **your** installation actually has. Third-party plugins included. Afterwards it is your graph: rewire it, extend it, reuse pieces of it.

## The safety model

This is the part worth reading before you install anything that talks to an LLM.

- **The model supplies a plan, not code.** RhiGhAI never executes C#, Python, or shell commands suggested by a model, and never opens files it proposes.
- **Script components cannot enter the graph.** Everything under Grasshopper's `Script` subcategory — `C# Script`, `Python 3 Script`, `IronPython 2 Script`, `VB Script`, their legacy variants, plus the free-form `Expression` and `Evaluate` evaluators — is excluded from the catalogue, and the emitter checks a second time against the object Grasshopper actually built. The catalogue is not the only gate.
- **Every answer is untrusted, whichever provider produced it.** It passes the same schema check, semantic validation and live-catalogue check.
- **Nothing invalid reaches the canvas.** Unknown components, wrong port names, cycles and bad literals are rejected *before* emission, and the precise error (with the list of valid ports) is fed back for bounded self-repair.
- **Your API key** is encrypted with DPAPI for the current Windows user in `%LOCALAPPDATA%\RhiGhAI\provider.key`. It never enters `settings.json`, the transcript or diagnostics; the UI shows the last 4 characters only.

Threat model and trust boundary: [`context/architecture/07-нефункциональные/безопасность.md`](context/architecture/07-нефункциональные/безопасность.md).

## Requirements

- Windows x64
- Rhino 8.20 or newer
- .NET 8 SDK (to build from source)
- An account for one of the model providers below

## Build and install from source

No prebuilt binaries are committed to this repository. From the project root:

```powershell
.\packaging\Build-Package.ps1
```

The script restores pinned NuGet dependencies, runs the test suite, and produces a local installer ZIP plus a `.yak` in `artifacts\`. Then:

1. Close Rhino.
2. Unpack `artifacts\RhiGhAI-0.2.0-local-installer.zip` **completely**.
3. Run `Install RhiGhAI.cmd`, then start Rhino again.
4. Type `RhiGhAI` in the Rhino command line.

No administrator rights, Node.js or npm required. The installer writes to a single unversioned path, removes previous versions, and refuses to run while `Rhino.exe` is open. The build number actually loaded is shown in the panel header — check there that an update applied.

> The legacy `.rhi` format is deliberately not produced: the Rhino Installer Engine inspector is incompatible with modern .NET 8 plugins.

## Model providers

**Settings → Model provider.** Two sources:

| Source | What it needs | When to use |
|---|---|---|
| Codex · ChatGPT sign-in | nothing but a sign-in | default, while your subscription limits last |
| API key · OpenAI-compatible | API base URL + key + model | when limits run out, or you want a different model |

Verified endpoints: `https://api.openai.com/v1`, `https://openrouter.ai/api/v1`, `https://api.deepseek.com/v1`, `https://api.anthropic.com/v1`, `http://localhost:11434/v1` (Ollama), `http://localhost:1234/v1` (LM Studio). The model catalogue is read from `GET /models`; if a provider does not expose one, type the model id by hand.

Structured output is requested as strict `json_schema`; providers without a strict mode automatically get one retry as `json_object` with the schema in the message text. Plain `http://` is accepted for loopback addresses only — the key travels on every request.

## What is in the MVP

**Rhino:** box, polyline, planar surface, extrusion/Brep, bounded Booleans, copy/translation, layers and attributes. Modifying existing geometry is allowed only for the current selection. One successful answer is undone by a single `Ctrl+Z`.

**Grasshopper:** up to 80 nodes and 240 wires, a slider for every meaningful parameter, its own group on the canvas. A repeat request in the same conversation replaces the previous set entirely, in one GH undo.

**Both:** up to five bounded self-repair attempts leaving no geometry behind, stop/timeout, conversation continuation, and a readable listing of the checked plan in the message feed.

Mesh, SubD, materials, rendering, layouts and blocks are deliberately out of scope for 0.2.0.

## Try it

In a fresh millimetre document:

- Rhino — `Create a 2400×1200×18 mm panel on layer Panels`
- with something selected — `Move the selection 500 mm up and assign layer Raised`
- Grasshopper — `A row of columns: count, spacing, height and radius on sliders`
- Grasshopper — `A spiral of points around a circle, radius and turn count on sliders`

Prompts work in any language the model speaks; the examples throughout this repo are in Russian.

## Project layout

```
src/RhiGhAI.Core          contracts, validation, providers, persistence  (no Rhino dependency)
src/RhiGhAI.Rhino         the .rhp plugin: panel, settings, executor
src/RhiGhAI.Grasshopper   the .gha: component catalogue and emitter
tests/RhiGhAI.Tests       unit tests, runnable without Rhino
tests/RhiGhAI.HostTests   host tests, require a live Rhino
context/                  the full design record: idea, architecture, ADRs
```

`context/` is not decoration — it is the reasoning behind every decision, including the [ADR journal](context/architecture/06-решения/журнал-решений/). Start at [`context/INDEX.md`](context/INDEX.md). It is written in Russian.

## Development

```powershell
dotnet build     # 0 warnings expected: TreatWarningsAsErrors is on
dotnet test      # unit tests, no Rhino required
```

Contributions are welcome — issues and pull requests both. Please keep the existing conventions: warnings stay at zero, a behavioural fix comes with a test, and a change that alters an architectural decision gets a note appended to the relevant ADR rather than a silent rewrite.

## Data and removal

Settings, the encrypted provider key, local conversation bindings and the working copy of the Codex runtime live in `%LOCALAPPDATA%\RhiGhAI`. Authorization belongs to Codex and stays in its standard profile `%USERPROFILE%\.codex`; signing out of RhiGhAI also ends the shared Codex session. No Codex identifiers are written into `.3dm` files. Uninstalling does not delete these folders automatically.

## Status and honesty

0.2.0 is an MVP. It is not code-signed and not published to Yak. It has been exercised on a live Rhino 8 installation, but it has not been through a broad clean-machine matrix — treat it as early software and keep backups of work you care about.

RhiGhAI is not an engineering tool: it can produce a geometrically valid result that is nonetheless wrong for your purpose. You remain the architect.

## License

[MIT](LICENSE) © 2026 nakarandashe27.

---

# RhiGhAI по-русски

**Обычный язык → настоящее редактируемое определение Grasshopper. Не чёрный ящик.**

RhiGhAI — open-source плагин для Rhino 8. Вы описываете задачу обычным языком, модель возвращает **строго типизированный план**, а не код, и контролируемый исполнитель выполняет только разрешённые операции.

## Два режима результата

| Режим | Что получается |
|---|---|
| **Rhino** | Геометрия в документе; один успешный ответ отменяется одним `Ctrl+Z`. |
| **Grasshopper** | **Редактируемое определение из обычных компонентов**: узлы, провода, слайдеры и панели на холсте, в собственной группе. |

Ради второго режима проект и существует. Вместо единственного непрозрачного компонента с четырьмя входами плагин собирает настоящее определение — `Number Slider → Series → Construct Point → Circle → Extrude` и что угодно ещё — из компонентов **вашей** установки, включая сторонние плагины. Дальше это ваш граф: правьте, расширяйте, переиспользуйте куски.

## Модель безопасности

- **Модель поставляет план, а не код.** Плагин никогда не выполняет предложенные C#, Python или shell-команды и не открывает предложенные файлы.
- **Script-компоненты не могут попасть в граф.** Всё из подкатегории `Script` — `C# Script`, `Python 3 Script`, `IronPython 2 Script`, `VB Script`, их legacy-варианты, а также вычислители произвольных выражений `Expression` и `Evaluate` — исключено из каталога, и эмиттер проверяет это второй раз уже у объекта, который Grasshopper фактически создал. Каталог не является единственной точкой контроля.
- **Ответ любого провайдера остаётся недоверенным** и проходит ту же проверку схемы, семантики и живого каталога.
- **Ничего невалидного не доходит до холста.** Неизвестные компоненты, неверные имена портов, циклы и плохие литералы отклоняются **до** эмиссии, а точная ошибка со списком доступных портов уходит в ограниченное самоисправление.
- **Ключ API** шифруется DPAPI под текущего пользователя Windows в `%LOCALAPPDATA%\RhiGhAI\provider.key`. В `settings.json`, историю диалогов и диагностику он не попадает; в интерфейсе видны только последние 4 символа.

Модель угроз и граница доверия: [`context/architecture/07-нефункциональные/безопасность.md`](context/architecture/07-нефункциональные/безопасность.md).

## Требования

Windows x64 · Rhino 8.20+ · .NET 8 SDK для сборки · аккаунт одного из провайдеров ниже.

## Сборка и установка из исходников

Готовые сборки в репозитории не хранятся. Из корня проекта:

```powershell
.\packaging\Build-Package.ps1
```

Скрипт восстанавливает закреплённые NuGet-зависимости, запускает тесты и создаёт локальный installer ZIP и `.yak` в `artifacts\`. Затем:

1. Закройте Rhino.
2. Распакуйте `artifacts\RhiGhAI-0.2.0-local-installer.zip` **целиком**.
3. Запустите `Install RhiGhAI.cmd`, снова откройте Rhino.
4. Выполните команду `RhiGhAI` в командной строке Rhino.

Права администратора, Node.js и npm не нужны. Установщик кладёт файлы в один неверсионированный путь, удаляет папки прошлых версий и не запускается, пока работает `Rhino.exe`. Номер фактически загруженной сборки показан в шапке панели.

> Устаревший формат `.rhi` намеренно не выпускается: инспектор Rhino Installer Engine несовместим с современными .NET 8-плагинами.

## Провайдер модели

**Настройки → Провайдер модели.** Два источника:

| Источник | Что нужно | Когда |
|---|---|---|
| Codex · вход ChatGPT | ничего, кроме входа | по умолчанию, пока хватает лимитов подписки |
| API-ключ · OpenAI-совместимый | адрес API + ключ + модель | когда лимиты кончились или нужна другая модель |

Проверенные адреса: `https://api.openai.com/v1`, `https://openrouter.ai/api/v1`, `https://api.deepseek.com/v1`, `https://api.anthropic.com/v1`, `http://localhost:11434/v1` (Ollama), `http://localhost:1234/v1` (LM Studio). Каталог моделей читается из `GET /models`; если провайдер его не отдаёт, впишите идентификатор модели вручную.

Structured output запрашивается строгой `json_schema`; провайдеры без strict-режима автоматически получают один повтор в `json_object` со схемой в тексте. Обычный `http://` принимается только для loopback-адресов — ключ уходит на каждом запросе.

## Что входит в MVP

**Rhino:** box, polyline, planar surface, extrusion/Brep, ограниченные Boolean, copy/translation, слои и атрибуты. Изменение существующей модели — только для текущего выделения. Один успешный ответ целиком отменяется одним `Ctrl+Z`.

**Grasshopper:** до 80 узлов и 240 связей, слайдер на каждый значимый параметр, собственная группа на холсте. Повторный запрос в том же диалоге заменяет предыдущий набор целиком, одним GH Undo.

**Общее:** до пяти попыток самоисправления без остаточной геометрии, остановка и тайм-аут, продолжение диалога, читаемый листинг проверенного плана в ленте.

Mesh, SubD, материалы, рендеринг, листы и блоки в 0.2.0 намеренно не входят.

## Быстрая проверка

В новом миллиметровом документе:

- Rhino: `Создай панель 2400×1200×18 мм на слое Panels`
- с выделенным объектом: `Перемести выделенное на 500 мм вверх и назначь слой Raised`
- Grasshopper: `Ряд колонн: количество, шаг, высота и радиус — слайдерами`
- Grasshopper: `Спираль из точек по кругу, радиус и число витков слайдерами`

## Структура проекта

```
src/RhiGhAI.Core          контракты, валидация, провайдеры, хранение  (без зависимости от Rhino)
src/RhiGhAI.Rhino         плагин .rhp: панель, настройки, исполнитель
src/RhiGhAI.Grasshopper   .gha: каталог компонентов и эмиттер
tests/RhiGhAI.Tests       юнит-тесты, работают без Rhino
tests/RhiGhAI.HostTests   host-тесты, требуют живого Rhino
context/                  полная запись замысла: идея, архитектура, ADR
```

`context/` — не украшение, а обоснование каждого решения, включая [журнал ADR](context/architecture/06-решения/журнал-решений/). Точка входа — [`context/INDEX.md`](context/INDEX.md).

## Разработка

```powershell
dotnet build     # ожидается 0 предупреждений: включён TreatWarningsAsErrors
dotnet test      # юнит-тесты, Rhino не нужен
```

Issues и pull requests приветствуются. Пожалуйста, держитесь текущих правил: предупреждений остаётся ноль, правка поведения приходит с тестом, а изменение архитектурного решения дописывается последствием в соответствующий ADR, а не переписывает его молча.

## Данные и удаление

Настройки, зашифрованный ключ провайдера, локальные привязки диалогов и рабочая копия Codex хранятся в `%LOCALAPPDATA%\RhiGhAI`. Авторизация принадлежит Codex и лежит в его стандартном профиле `%USERPROFILE%\.codex`; выход из RhiGhAI завершает и общую сессию Codex. Идентификаторы Codex не записываются в `.3dm`. Удаление плагина не удаляет эти папки автоматически.

## Статус

0.2.0 — MVP. Сборка не подписана и не опубликована в Yak. Она проверена на живой установке Rhino 8, но не проходила широкую clean-machine матрицу: относитесь к ней как к раннему софту и держите резервные копии важной работы.

RhiGhAI не расчётный инструмент: он может выдать геометрически корректный результат, который неверен по существу задачи. Архитектор — по-прежнему вы.

## Лицензия

[MIT](LICENSE) © 2026 nakarandashe27.
